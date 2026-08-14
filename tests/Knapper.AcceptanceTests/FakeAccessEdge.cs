using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Knapper.AcceptanceTests;

/// <summary>
/// Cloudflare Access, as far as `knapper verify` can tell: a reverse proxy in
/// front of a real Knapper server that refuses requests carrying no valid
/// service-token assertion. It exists because the shape of a refusal is not
/// uniform, and `verify` once got that wrong in the direction that matters —
/// it called a correctly-secured deployment EXPOSED (CT 106, 2026-08-14) and
/// an emergency tunnel shutdown followed.
///
/// The two applications here mirror the production topology (runbook §6.2)
/// and, crucially, their POLICY TYPES differ, which is what makes the refusal
/// shapes differ:
///
///   /  and /health — the host application, which carries an identity policy
///                    (`OAuth ME`) alongside Service Auth. A caller with no
///                    assertion might be a human with a browser, so Access
///                    sends them to log in: 302 → the login page → 200 HTML.
///   /up            — the monitoring application, Service Auth ONLY. There is
///                    no human to send anywhere, so it refuses flat: 403.
///
/// A probe that follows redirects sees the login page's 200 and reports the
/// vault surface as answering. The same probe against /up is unaffected,
/// because a flat 403 has nothing to follow — which is exactly why the defect
/// survived: the check that would have caught it passed.
/// </summary>
internal sealed class FakeAccessEdge : IDisposable
{
    // The host application's service token — `verify`'s --client-id/secret.
    internal const string VaultTokenId = "vault-token.access";
    internal const string VaultTokenSecret = "vault-token-secret";

    // The /up application's — --monitor-client-id/secret. Deliberately valid
    // ONLY on /up: the credential asymmetry is the thing §6.5 asserts.
    internal const string MonitorTokenId = "monitor-token.access";
    internal const string MonitorTokenSecret = "monitor-token-secret";

    /// <summary>
    /// The hostname a tunneled request keeps, and the server behind this must
    /// be told to allow (`Mcp__AllowedHosts__0`, runbook §6.3). Load-bearing,
    /// not decoration: it is what makes the origin see a request that did NOT
    /// come from this box, so /health answers 404 the way it does in
    /// production. Forward a loopback Host instead and the same-box exemption
    /// engages, /health answers 200 through the tunnel, and the check that
    /// exists to catch exactly that reads the fixture's mistake as the
    /// server's. `.test` is reserved and resolves nowhere, by design.
    /// </summary>
    internal const string PublicHost = "mcp.example.test";

    private const string LoginPath = "/cdn-cgi/access/login/";

    private readonly WebApplication _app;
    private readonly HttpClient _origin;
    private readonly bool _vaultSurfaceExposed;

    /// <param name="originPort">The real <see cref="AcceptanceServer"/> behind the edge.</param>
    /// <param name="vaultSurfaceExposed">
    /// The misconfiguration `verify` exists to catch: the host application
    /// lets an unauthenticated caller straight through to the MCP surface.
    /// Everything else about the deployment still looks right.
    /// </param>
    internal FakeAccessEdge(int originPort, bool vaultSurfaceExposed = false)
    {
        _vaultSurfaceExposed = vaultSurfaceExposed;
        _origin = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{originPort}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var port = AcceptanceServer.FreePort();
        Url = new Uri($"http://127.0.0.1:{port}/");

        // Empty builder: no appsettings.json, no logging providers. This
        // project's output directory belongs to the SERVER, and its
        // configuration must not leak into the fixture standing in for
        // Cloudflare.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel(options => options.ListenLocalhost(port));
        _app = builder.Build();
        _app.Run(HandleAsync);
        _app.StartAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Loopback, because a test cannot conjure a public hostname — which is
    /// why `verify` needs --expect-access to run its ingress checks here.
    /// What is under test is the EDGE's behavior, not the URL's shape.
    /// </summary>
    internal Uri Url { get; }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // The landing page, served unauthenticated by design — a redirect
        // nobody can follow would not reproduce anything.
        if (path.StartsWith(LoginPath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<!doctype html><html><head><title>Sign in</title></head>" +
                "<body><h1>Sign in to continue</h1></body></html>");
            return;
        }

        var id = context.Request.Headers["CF-Access-Client-Id"].ToString();
        var secret = context.Request.Headers["CF-Access-Client-Secret"].ToString();

        if (path.StartsWith("/up", StringComparison.Ordinal))
        {
            if (Presented(id, secret, MonitorTokenId, MonitorTokenSecret))
                await ProxyAsync(context);
            else
                RefuseFlat(context);
            return;
        }

        if (Presented(id, secret, VaultTokenId, VaultTokenSecret) || _vaultSurfaceExposed)
            await ProxyAsync(context);
        else
            RedirectToLogin(context);
    }

    private static bool Presented(string id, string secret, string expectedId, string expectedSecret) =>
        string.Equals(id, expectedId, StringComparison.Ordinal)
        && string.Equals(secret, expectedSecret, StringComparison.Ordinal);

    /// <summary>Service-Auth-only: no identity to redirect, so a bare 403.</summary>
    private static void RefuseFlat(HttpContext context) => context.Response.StatusCode = 403;

    /// <summary>
    /// The identity-policy refusal, headers and all — including the
    /// `www-authenticate` Access sends, which is a second signal a probe
    /// could read and today does not.
    /// </summary>
    private void RedirectToLogin(HttpContext context)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = new Uri(Url, $"{LoginPath}{Url.Host}").ToString();
        context.Response.Headers["www-authenticate"] = "Cloudflare-Access";
    }

    private async Task ProxyAsync(HttpContext context)
    {
        var target = new Uri(_origin.BaseAddress!, context.Request.Path + context.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);

        // cloudflared's `httpHostHeader`: the public hostname survives the
        // hop. See PublicHost — dropping this line silently converts every
        // tunneled request into a same-box one.
        request.Headers.Host = PublicHost;

        foreach (var (name, values) in context.Request.Headers)
        {
            if (Hop(name))
                continue;
            if (!request.Headers.TryAddWithoutValidation(name, values.AsEnumerable()))
                request.Content?.Headers.TryAddWithoutValidation(name, values.AsEnumerable());
        }

        using var response = await _origin
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            // Length and framing are this hop's to decide: the MCP surface
            // answers text/event-stream, which must stream through rather
            // than be buffered to a length.
            if (!Hop(name))
                context.Response.Headers[name] = values.ToArray();
        }
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool Hop(string name) =>
        name is "Host" or "Connection" or "Keep-Alive" or "Transfer-Encoding" or "Content-Length"
             or "Upgrade" or "Proxy-Connection";

    public void Dispose()
    {
        try
        {
            _app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _origin.Dispose();
    }
}
