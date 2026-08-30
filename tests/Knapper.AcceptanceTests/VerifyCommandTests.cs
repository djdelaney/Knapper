using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Knapper.AcceptanceTests;

/// <summary>
/// `knapper verify --url` is the only check that will ever run against the
/// LIVE service, so it gets the same treatment as the server: the shipped
/// binary, a real server process, a real socket. Two properties matter and
/// neither is provable by reading the code — that it passes a healthy
/// deployment, and that it FAILS a broken one (a verifier that always prints
/// ok is worse than no verifier). The third test pins the read-only promise
/// the runbook makes when it points this at Helios.
/// </summary>
public sealed class VerifyCommandTests : IDisposable
{
    private readonly string _vaultDir = Wire.NewTempDir("knapper-verify-vault");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-verify-out");

    public VerifyCommandTests()
    {
        Wire.Seed(_vaultDir, "note.md", "# Note\nsome content\n");
        Wire.Seed(_vaultDir, "Projects/plan.md", "milestones\n");
    }

    [Fact]
    public void A_healthy_deployment_passes_every_check()
    {
        using var server = new AcceptanceServer(_vaultDir, _outsideDir);

        var (exitCode, output) = RunVerify(server.Port);

        exitCode.ShouldBe(0, output);
        output.ShouldContain("all checks passed");
        output.ShouldContain("ok    tools/list is EXACTLY the locked surface");
        // Names are not validity: the 2026-08-14 run that accepted §6 ingress
        // passed the line above, exit 0, no skips, against a manifest Claude
        // Code rejected outright. The count rides on the ok line so an
        // operator can see the check actually inspected thirteen schemas.
        output.ShouldContain("ok    every published tool schema is one a client can load (14 schemas)");
        output.ShouldContain("ok    a no-match search still reports exhaustive scan evidence");
        output.ShouldContain("ok    the mutation surface is wired and answers with typed codes");
        // Loopback is the Access exemption's own target: the ingress checks
        // must announce themselves as skipped, never quietly pass — and by
        // the same LABEL they would carry if they ran, so that an operator
        // told "every line must read ok" can diff the two runs.
        output.ShouldContain("skip  Access refuses an unauthenticated /up");
        output.ShouldContain("skip  Access refuses an unauthenticated MCP request");
        output.ShouldContain("skip  /health is unreachable from outside the box");
    }

    /// <summary>
    /// A transcript has to say which binary produced it. `verify` reports on a
    /// REMOTE process, so every version string in its output is about the far
    /// end and the near end was invisible — and on 2026-08-16 a CLI two
    /// releases stale ran FOURTEEN checks where the current build runs
    /// fifteen, printed "all checks passed", and the missing one (the schema
    /// check added in 0.4.0) was recoverable only by someone who knew the
    /// expected count from an external note. Every line read ok in both runs;
    /// the length of the list was the only signal, and nothing printed it.
    /// </summary>
    [Fact]
    public void Every_run_names_the_CLI_that_produced_it_and_tallies_its_checks()
    {
        using var server = new AcceptanceServer(_vaultDir, _outsideDir);

        var (exitCode, output) = RunVerify(server.Port);

        exitCode.ShouldBe(0, output);
        // Server and CLI are the same tree here, so this is also a real
        // statement that the two agree about the build they came from.
        output.ShouldContain($"knapper CLI {Knapper.Core.BuildInfo.Version}");

        var tally = System.Text.RegularExpressions.Regex.Match(
            output, @"^(\d+) ok, (\d+) failed, (\d+) skipped$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        tally.Success.ShouldBeTrue($"no tally line in:\n{output}");
        int.Parse(tally.Groups[2].Value).ShouldBe(0);
        // The number is not asserted exactly — it moves whenever a check is
        // added, which is the point. What must hold is that it is REPORTED,
        // and that it counts the ok lines actually printed.
        int.Parse(tally.Groups[1].Value)
            .ShouldBe(output.Split('\n').Count(l => l.StartsWith("ok    ", StringComparison.Ordinal)));
        int.Parse(tally.Groups[3].Value)
            .ShouldBe(output.Split('\n').Count(l => l.StartsWith("skip  ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The regression this file exists for as of 2026-08-14: the two-application
    /// deployment §6.5 signs off, with Access ACTUALLY in front, refusing
    /// exactly as Cloudflare does. `verify` reported two FAILs here — the
    /// refusal probes followed the 302 to the Access login page and read its
    /// 200 as the vault surface answering — and a correctly-secured deployment
    /// was taken down on the strength of it. A false FAIL is not the harmless
    /// direction: it is the one an operator acts on immediately.
    /// </summary>
    [Fact]
    public void A_deployment_behind_two_Access_applications_passes_every_check()
    {
        // The public hostname the tunnel preserves must be an allowed Host or
        // the rebinding guard refuses every tunneled request — §6.3's
        // `Mcp__AllowedHosts__0`, and the reason it is called out there.
        using var server = new AcceptanceServer(_vaultDir, _outsideDir,
            new Dictionary<string, string> { ["Mcp__AllowedHosts__0"] = FakeAccessEdge.PublicHost });
        using var edge = new FakeAccessEdge(server.Port);

        var (exitCode, output) = RunVerify(edge);

        exitCode.ShouldBe(0, output);
        output.ShouldContain("all checks passed");
        // The status each probe SAW is asserted, not merely that it passed:
        // 302 is the shape that was misread, and reading it back proves the
        // redirect was not followed to the login page's 200.
        output.ShouldContain("ok    Access refuses an unauthenticated MCP request (HTTP 302");
        output.ShouldContain("ok    the monitoring token cannot reach the vault surface (HTTP 302");
        // The Service-Auth-only refusal, which passed even while broken.
        output.ShouldContain("ok    Access refuses an unauthenticated /up (HTTP 403");
        // 404 means the request REACHED the origin: the edge let the owner
        // credential through and the loopback-only filter turned it away.
        output.ShouldContain("ok    /health is unreachable from outside the box (HTTP 404");
        // §6.5's acceptance criterion is all-ok with no skips, and it must be
        // reachable — it was not, which is what left §6 unsigned.
        output.ShouldNotContain("skip  ");
    }

    /// <summary>
    /// Enabling Managed OAuth on the ROOT application changes how it encodes
    /// a refusal — 302 to the login page becomes 401 with an RFC 9728 pointer
    /// — while changing no policy at all (CT 106, 2026-08-16). Every verdict
    /// stayed correct across that change and every EXPLANATION did not: the
    /// refusal text was selected from the status code, so three releases told
    /// the operator that "a service-auth-only policy has no login to offer"
    /// about the one application whose entire purpose is offering an OAuth
    /// login to MCP clients (reported 2026-08-22).
    ///
    /// What is pinned is that ONE deployment's TWO applications are described
    /// DIFFERENTLY, each from what its own response carries: a fix that moved
    /// all three rows together would have swapped one wrong explanation for
    /// another, since /up's app genuinely is service-auth-only and the root
    /// app genuinely is not.
    /// </summary>
    [Fact]
    public void A_Managed_OAuth_refusal_is_described_by_what_it_carries_not_by_its_status_code()
    {
        using var server = new AcceptanceServer(_vaultDir, _outsideDir,
            new Dictionary<string, string> { ["Mcp__AllowedHosts__0"] = FakeAccessEdge.PublicHost });
        using var edge = new FakeAccessEdge(server.Port, rootRefusal: RootRefusal.ManagedOAuth);

        var (exitCode, output) = RunVerify(edge);

        // The verdicts were never the defect and must not become one: the
        // 401 is a refusal, exactly as the 302 it replaced was.
        exitCode.ShouldBe(0, output);
        output.ShouldContain("all checks passed");
        output.ShouldNotContain("skip  ");

        // Both rows that reach the root app: an application that HAS an
        // authorization path, said to have one.
        foreach (var row in new[]
                 {
                     Line(output, "ok    Access refuses an unauthenticated MCP request"),
                     Line(output, "ok    the monitoring token cannot reach the vault surface"),
                 })
        {
            row.ShouldContain("HTTP 401");
            row.ShouldContain("RFC 9728");
            row.ShouldNotContain("no login to offer");
        }

        // ...and the service-auth-only app, still described as the flat
        // refusal it actually sends.
        var up = Line(output, "ok    Access refuses an unauthenticated /up");
        up.ShouldContain("HTTP 403");
        up.ShouldContain("refused flat");
        up.ShouldNotContain("RFC 9728");
    }

    /// <summary>
    /// The other half, and the reason the fix is a named allowlist of refusal
    /// statuses rather than "anything but 200": a probe that passes on
    /// not-200 passes on a 500, a misrouted tunnel, or a DNS failure just as
    /// happily as on a real refusal. Here the host application genuinely lets
    /// an unauthenticated caller through to the vault, and nothing else about
    /// the deployment is wrong.
    /// </summary>
    [Fact]
    public void An_Access_application_that_does_not_refuse_FAILS_the_ingress_check()
    {
        // The public hostname the tunnel preserves must be an allowed Host or
        // the rebinding guard refuses every tunneled request — §6.3's
        // `Mcp__AllowedHosts__0`, and the reason it is called out there.
        using var server = new AcceptanceServer(_vaultDir, _outsideDir,
            new Dictionary<string, string> { ["Mcp__AllowedHosts__0"] = FakeAccessEdge.PublicHost });
        using var edge = new FakeAccessEdge(server.Port, vaultSurfaceExposed: true);

        var (exitCode, output) = RunVerify(edge);

        exitCode.ShouldBe(1, output);
        output.ShouldContain("FAIL  Access refuses an unauthenticated MCP request");
        output.ShouldContain("FAIL  the monitoring token cannot reach the vault surface");
        output.ShouldContain("check(s) FAILED");
    }

    [Fact]
    public void A_server_missing_one_tool_FAILS_the_surface_check()
    {
        // The failure this exists for: a deployment that answers, serves
        // queries, and is quietly missing part of its contract.
        using var server = new AcceptanceServer(_vaultDir, _outsideDir,
            new Dictionary<string, string> { ["Mcp__DisabledTools__0"] = "vault_delete" });

        var (exitCode, output) = RunVerify(server.Port);

        exitCode.ShouldBe(1, output);
        output.ShouldContain("FAIL  tools/list is EXACTLY the locked surface");
        output.ShouldContain("MISSING: vault_delete");
        output.ShouldContain("check(s) FAILED");
    }

    [Fact]
    public void Verification_writes_nothing_to_the_vault()
    {
        using var server = new AcceptanceServer(_vaultDir, _outsideDir);
        var before = Snapshot(_vaultDir);

        var (exitCode, output) = RunVerify(server.Port);

        exitCode.ShouldBe(0, output);
        // Including .trash and any probe path: the runbook points this at
        // Helios, where a stray file syncs to the user's devices.
        Snapshot(_vaultDir).ShouldBe(before);
    }

    /// <summary>
    /// The CLI's OWN build output, stamped in by the csproj — not a copy in
    /// this project's directory. See the ProjectReference comment there: a
    /// copy here is missing its configuration assemblies whenever the shared
    /// framework wins conflict resolution, which depends on the machine.
    /// </summary>
    private static string CliDll() =>
        typeof(VerifyCommandTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "KnapperCliDll").Value!;

    /// <summary>
    /// One named check's own line. A bare ShouldContain over the whole
    /// transcript is satisfied by any OTHER row carrying the text, which is
    /// precisely the confusion these assertions exist to rule out.
    /// </summary>
    private static string Line(string output, string prefix) =>
        output.Split('\n').Select(l => l.TrimEnd('\r'))
            .SingleOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"no line starting '{prefix}' in:\n{output}");

    /// <summary>Straight at the server, no edge — the §5 same-box shape.</summary>
    private static (int ExitCode, string Output) RunVerify(int port) =>
        RunVerify(new Uri($"http://127.0.0.1:{port}/"), null);

    /// <summary>
    /// Through the Access fixture, with both credential pairs, exactly as
    /// §6.5 runs it. --expect-access because the fixture is necessarily on
    /// loopback and the ingress checks would otherwise skip themselves.
    /// </summary>
    private static (int ExitCode, string Output) RunVerify(FakeAccessEdge edge) =>
        RunVerify(edge.Url, edge);

    private static (int ExitCode, string Output) RunVerify(Uri url, FakeAccessEdge? edge)
    {
        var dll = CliDll();
        File.Exists(dll).ShouldBeTrue($"CLI binary not found at {dll}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("verify");
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(url.ToString());
        if (edge is not null)
        {
            psi.ArgumentList.Add("--expect-access");
            // Server and CLI are built from this same tree, so they stamp the
            // same string — and supplying it is what makes a run with NO skip
            // lines possible, which is §6.5's acceptance criterion.
            psi.ArgumentList.Add("--expect-this-version");
            // Through the environment, not the argument list: the same path
            // the runbook uses, so the secret never reaches a process table.
            psi.Environment["CF_ACCESS_CLIENT_ID"] = FakeAccessEdge.VaultTokenId;
            psi.Environment["CF_ACCESS_CLIENT_SECRET"] = FakeAccessEdge.VaultTokenSecret;
            psi.Environment["CF_MONITOR_CLIENT_ID"] = FakeAccessEdge.MonitorTokenId;
            psi.Environment["CF_MONITOR_CLIENT_SECRET"] = FakeAccessEdge.MonitorTokenSecret;
        }

        using var process = Process.Start(psi)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(60_000).ShouldBeTrue("verify did not finish");
        process.WaitForExit(); // flush the async readers
        return (process.ExitCode, output.ToString());

        static void Append(StringBuilder builder, string? line)
        {
            if (line is null)
                return;
            lock (builder)
                builder.AppendLine(line);
        }
    }

    /// <summary>Every path under the vault plus its bytes — content, not receipts.</summary>
    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? $"{Path.GetRelativePath(root, path)}={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}"
                : $"{Path.GetRelativePath(root, path)}=<dir>")
            .ToList();

    public void Dispose()
    {
        Wire.TryDeleteDir(_vaultDir);
        Wire.TryDeleteDir(_outsideDir);
    }
}
