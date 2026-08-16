using System.Net;
using System.Text;
using System.Text.Json;
using Knapper.Core;
using ModelContextProtocol.Client;

namespace Knapper.Cli;

/// <summary>
/// `knapper verify --url` — the DEPLOYED-service check (runbook §5/§6). The
/// acceptance suite proves this code honors its contract by spawning its own
/// servers over a fixture vault; nothing in the repo can prove that the thing
/// running on CT 106, behind the tunnel, with the real Access apps in front of
/// it, is that same contract. This closes exactly that gap.
///
/// STRICTLY READ-ONLY, and deliberately so: at the point in the runbook where
/// it runs, the vault is already Helios via Obsidian Sync, so a write test
/// would land real notes on the user's devices. Every check here either reads
/// or is refused BY DESIGN — the one mutation call is aimed at a path that
/// cannot exist, so it dies at the fresh-read step before any byte is written.
/// Write-side races belong in §8b's disposable-vault session, where the blast
/// radius is zero. Do not "strengthen" this by adding a real write.
///
/// It is also the natural post-upgrade smoke test: no vault configuration is
/// read, so it runs from anywhere that can reach the URL.
/// </summary>
internal static class Verify
{
    private const string ProbePrefix = "_knapper-verify-probe-";

    private const string Loopback =
        "loopback URL — the same-box exemption applies here by design; re-run through the tunnel, " +
        "or pass --expect-access if an Access edge does front this URL";

    internal static int Run(string[] args)
    {
        string? url = null, clientId = null, clientSecret = null, monitorId = null, monitorSecret = null;
        string? expectVersion = null;
        var expectAccess = false;
        var expectVersionFromSelf = false;
        for (var i = 1; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--url": url = value; i++; break;
                case "--expect-version": expectVersion = value; i++; break;
                // "There is an Access edge in front of this URL" — run the
                // ingress checks and refuse to skip them. Two uses: the
                // runbook's §6.5 sign-off, where a mistyped loopback URL
                // would otherwise turn the whole ingress section into skips
                // an operator reads as clean; and the acceptance suite, which
                // can only stand its fake edge on loopback.
                case "--expect-access": expectAccess = true; break;
                // The version this very binary was built from. The upgrade
                // case in one flag: unpack a tarball, run its own knapper
                // against the URL, and the check is "is the service running
                // the build I just installed" with nothing typed by hand.
                // Where the expectation CAME FROM is carried forward: a
                // mismatch against a self-sourced expectation can be a stale
                // CLI, which no server-side remediation would fix.
                case "--expect-this-version":
                    expectVersion = BuildInfo.Version;
                    expectVersionFromSelf = true;
                    break;
                case "--client-id": clientId = value; i++; break;
                case "--client-secret": clientSecret = value; i++; break;
                case "--monitor-client-id": monitorId = value; i++; break;
                case "--monitor-client-secret": monitorSecret = value; i++; break;
                default:
                    Console.Error.WriteLine($"verify: unknown argument '{args[i]}'");
                    return 2;
            }
        }

        // Env fallbacks so the service-token SECRET never has to appear in a
        // shell history or a systemd unit's ExecStart.
        clientId ??= Environment.GetEnvironmentVariable("CF_ACCESS_CLIENT_ID");
        clientSecret ??= Environment.GetEnvironmentVariable("CF_ACCESS_CLIENT_SECRET");
        monitorId ??= Environment.GetEnvironmentVariable("CF_MONITOR_CLIENT_ID");
        monitorSecret ??= Environment.GetEnvironmentVariable("CF_MONITOR_CLIENT_SECRET");

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            Console.Error.WriteLine(
                "usage: knapper verify --url <https://mcp.example.com/> [--client-id ID --client-secret SECRET] " +
                "[--monitor-client-id ID --monitor-client-secret SECRET] " +
                "[--expect-version X.Y.Z | --expect-this-version] [--expect-access]\n" +
                "  Service-token credentials also read from CF_ACCESS_CLIENT_ID/CF_ACCESS_CLIENT_SECRET and " +
                "CF_MONITOR_CLIENT_ID/CF_MONITOR_CLIENT_SECRET.");
            return 2;
        }

        return new Checker(
                endpoint, Token(clientId, clientSecret), Token(monitorId, monitorSecret),
                expectVersion, expectVersionFromSelf, expectAccess)
            .RunAsync().GetAwaiter().GetResult();

        static (string Id, string Secret)? Token(string? id, string? secret) =>
            string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ? null : (id, secret);
    }

    private sealed class Checker(
        Uri endpoint,
        (string Id, string Secret)? owner,
        (string Id, string Secret)? monitor,
        string? expectVersion,
        bool expectVersionFromSelf,
        bool expectAccess)
    {
        private int _failures;
        private int _passed;
        private int _skipped;

        // What /up reported, kept so the MCP surface's version can be compared
        // against it. Two surfaces of ONE process must agree; if they don't,
        // the tunnel is routing /up and the MCP endpoint to different
        // processes — a stale unit still bound on the port, say — and every
        // other green check here is describing whichever one happened to answer.
        private string? _upVersion;

        // A loopback URL is the same-box exemption's own target: Access is
        // bypassed there by design (AccessOptions.AllowLoopback), so the
        // ingress checks would assert the opposite of the contract. They are
        // SKIPPED loudly rather than quietly passing — a skipped ingress check
        // is exactly the thing an operator must not mistake for a green one.
        // --expect-access overrides the inference: it says an edge IS in
        // front regardless of what the URL looks like, and turns the skips
        // back into checks that must pass.
        private bool Tunnelled => expectAccess || !endpoint.IsLoopback;

        internal async Task<int> RunAsync()
        {
            // The CLI's own build, on every run, unconditionally. `verify`
            // reports on a REMOTE process, so every version string it prints
            // is about the far end and the near end is invisible — which is
            // how a CLI two releases stale ran fourteen checks instead of
            // fifteen and printed "all checks passed" (observed §8b,
            // 2026-08-16). The missing check was recoverable only by someone
            // who knew the expected count from elsewhere. Now the transcript
            // says which binary produced it.
            Console.WriteLine($"verifying {endpoint} (read-only) · knapper CLI {BuildInfo.Version}");

            await IngressAsync().ConfigureAwait(false);
            await SurfaceAsync().ConfigureAwait(false);

            // The tally is a separate line ABOVE the verdict, and the verdict
            // keeps its exact wording: the runbook tells an operator to diff a
            // run against a good one, and greps for these strings exist.
            // A shorter check list than another version's is otherwise
            // invisible — every line reads ok in both.
            Console.WriteLine($"{_passed} ok, {_failures} failed, {_skipped} skipped");
            Console.WriteLine(_failures == 0
                ? "all checks passed"
                : $"{_failures} check(s) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        // ---- ingress: Access, the /up disclosure contract, /health ---------

        private async Task IngressAsync()
        {
            if (!Tunnelled)
            {
                // Same LABELS the checks carry when they run: the runbook
                // tells an operator every line must read ok, and a skip that
                // renames the check it stood in for cannot be diffed against
                // a good run.
                Skip("Access refuses an unauthenticated /up", Loopback);
                Skip("Access refuses an unauthenticated MCP request", Loopback);
                Skip("/health is unreachable from outside the box", "loopback URL — /health is SUPPOSED to answer here");
            }
            else
            {
                await CheckAsync("Access refuses an unauthenticated /up", async () =>
                {
                    using var http = NewClient(null);
                    using var response = await http.GetAsync(new Uri(endpoint, "/up")).ConfigureAwait(false);
                    return AssertRefused(response,
                        "an unauthenticated caller must be refused — Mcp__Access__Enabled is off, " +
                        "or the tunnel is not routed through the Access app");
                }).ConfigureAwait(false);

                await CheckAsync("Access refuses an unauthenticated MCP request", async () =>
                {
                    using var http = NewClient(null);
                    using var response = await PostInitializeAsync(http).ConfigureAwait(false);
                    return AssertRefused(response, "the vault surface must be refused without an assertion");
                }).ConfigureAwait(false);

                // Two correct answers, and which one you get depends on
                // whether the credential in hand passes the Access app in
                // front of /health. 404 is the stronger one — it proves the
                // request REACHED the origin and the loopback-only filter
                // turned it away; a refusal at the edge proves only that the
                // origin was never asked, which is also fine but tests less.
                // Reporting which arrived is the point: the runbook used to
                // promise "404 from outside" flatly, and an operator who got
                // the 302 read a correct deployment as a broken one.
                await CheckAsync("/health is unreachable from outside the box", async () =>
                {
                    using var http = NewClient(owner);
                    using var response = await http.GetAsync(new Uri(endpoint, "/health")).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return "HTTP 404 from the origin — the loopback-only filter refused it";
                    if (Refusal(response) is { } refusal)
                        return $"{refusal} — the origin was never asked";
                    throw new InvalidOperationException(
                        $"got HTTP {(int)response.StatusCode}, expected 404 from the origin or a refusal at the " +
                        "edge — /health names filesystem paths and conflict filenames; " +
                        "Mcp__RestrictHealthToLoopback must stay true");
                }).ConfigureAwait(false);
            }

            // /up itself: reachable with the credential the MONITOR will use.
            var upToken = monitor ?? owner;
            await CheckAsync($"/up answers 200 with the {(monitor is null ? "owner" : "monitoring")} token", async () =>
            {
                using var http = NewClient(upToken);
                var response = await http.GetAsync(new Uri(endpoint, "/up")).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidOperationException(
                        $"got HTTP {(int)response.StatusCode} — 503 means the service degraded itself " +
                        "(vault, sync, ripgrep, audit, or a conflict file); a 3xx means this credential did " +
                        "not pass Access, so the login page answered and not the origin; anything else is ingress");
                }
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertUpDisclosesBooleansOnly(body);
                // Case-insensitive like the disclosure assert above: the
                // serializer's casing policy is not this check's contract.
                // Presence is already guaranteed — the assert just required it.
                using var document = JsonDocument.Parse(body);
                _upVersion = document.RootElement.EnumerateObject()
                    .Where(p => string.Equals(p.Name, "version", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Value.GetString())
                    .FirstOrDefault();
            }).ConfigureAwait(false);

            if (!Tunnelled)
            {
                // Would FAIL on a loopback URL, and correctly so: the
                // exemption opens the vault surface to any same-box caller,
                // monitoring credential or not. Nothing about the Access
                // applications is observable from here.
                Skip("the monitoring token cannot reach the vault surface", Loopback);
            }
            else if (monitor is null)
            {
                Skip("the monitoring token cannot reach the vault surface",
                    "no --monitor-client-id/--monitor-client-secret given (single-app setup)");
            }
            else
            {
                await CheckAsync("the monitoring token cannot reach the vault surface", async () =>
                {
                    using var http = NewClient(monitor);
                    using var response = await PostInitializeAsync(http).ConfigureAwait(false);
                    return AssertRefused(response,
                        "the /up credential must NOT open the vault — the monitoring Access app must be " +
                        "a separate, path-scoped application");
                }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// How a correctly-configured Access application refuses a request
        /// carrying no valid assertion — a short description of WHICH refusal
        /// arrived, or null if this response is not a refusal at all.
        ///
        /// There are two shapes and both are correct; which one you get is a
        /// property of the application's policy TYPE, not of how wrong the
        /// caller was. An application with an identity policy might be
        /// talking to a human, so it sends them to log in (302 →
        /// team.cloudflareaccess.com/cdn-cgi/access/login/…). A Service-Auth-
        /// only application has nobody to log in and refuses flat (403). The
        /// origin is consulted in neither case — Access decides at the edge,
        /// which is demonstrable by stopping cloudflared and watching the 302
        /// still arrive.
        ///
        /// Deliberately a named allowlist and not "anything but 200": a probe
        /// whose pass condition is not-200 also passes on a 500, on a
        /// misrouted tunnel, on DNS failure — the same defect this method
        /// exists to fix, one size down.
        /// </summary>
        private static string? Refusal(HttpResponseMessage response)
        {
            var code = (int)response.StatusCode;
            if (code is 401 or 403)
                return $"HTTP {code} at the edge — a service-auth-only policy has no login to offer";
            if (code is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                var where = location is null ? "no Location"
                    : location.IsAbsoluteUri ? location.Host
                    : location.ToString();
                return $"HTTP {code} at the edge → {where}";
            }
            return null;
        }

        /// <summary>Refusal or bust, returning the evidence for the ok line.</summary>
        private static string AssertRefused(HttpResponseMessage response, string why) =>
            Refusal(response) ?? throw new InvalidOperationException(
                $"got HTTP {(int)response.StatusCode}; {why}. A refusal is 401/403, or a 3xx to the " +
                "Access login page — redirects are not followed here, so this 200 is the origin answering");

        /// <summary>
        /// /up's body is a disclosure contract, not just a status code: no
        /// paths, no conflict filenames, no generation counter. Checked live
        /// because the monitor's credential is the weakest one in the system.
        /// </summary>
        private static void AssertUpDisclosesBooleansOnly(string body)
        {
            using var document = JsonDocument.Parse(body);
            var properties = document.RootElement.EnumerateObject()
                .Select(p => p.Name.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal).ToList();
            string[] expected = ["audit", "conflicts", "oversized", "ripgrep", "status", "sync", "vault", "version"];
            if (!properties.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"/up body properties are [{string.Join(", ", properties)}], expected [{string.Join(", ", expected)}]");
            }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var inner = property.Value.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
                if (inner.Count != 1 || inner[0] != "ok")
                {
                    throw new InvalidOperationException(
                        $"/up's '{property.Name}' carries [{string.Join(", ", inner)}], expected a lone boolean 'ok'");
                }
            }
        }

        /// <summary>
        /// The upgrade question every other check here leaves open: is the
        /// service running the build that was just installed? A restart onto
        /// the OLD binary — unit not reloaded, tarball unpacked beside the live
        /// directory rather than into it — passes every surface check in this
        /// file, because the old build satisfies the same contract. Only the
        /// version distinguishes them, and only if someone compares it.
        /// </summary>
        private void VersionChecks(string reported)
        {
            // A CLI older than the service, said out loud on EVERY run — not
            // only when an expectation was passed, and not as a check, because
            // it is a fact about this transcript rather than about the
            // deployment. An old `knapper` runs the check list IT shipped
            // with: fourteen checks where the deployed build's own CLI runs
            // fifteen, every line ok, "all checks passed" (§8b, 2026-08-16).
            // With --expect-version <release> it does not even mismatch —
            // a 0.3.2 CLI asked for release 0.5.1 sees 0.5.1 and passes,
            // over a check list that predates the schema check entirely.
            if (BuildInfo.CompareReleases(BuildInfo.Version, reported) is < 0)
            {
                Console.WriteLine(
                    $"warn  this CLI is {BuildInfo.Version} and the service reports {reported} — the client is " +
                    "the older of the two, so this run used the check list that build shipped with, which may be " +
                    "shorter than the deployed build's. Re-run from the installed CLI before trusting the verdict");
            }

            // /up and the MCP endpoint are two surfaces of one process. Always
            // checked, expectation or not: it costs nothing and it is the check
            // that catches a second, stale process still answering on the port.
            if (_upVersion is null)
            {
                Skip("/up and the MCP surface are the same build", "/up did not answer — fix the failure above first");
            }
            else
            {
                Check("/up and the MCP surface are the same build", () =>
                {
                    if (!string.Equals(_upVersion, reported, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"/up reports '{_upVersion}' but the MCP endpoint reports '{reported}' — one URL is " +
                            "reaching two different processes; check for a stale unit still bound to the port");
                    }
                });
            }

            if (expectVersion is null)
            {
                Skip($"the deployed build is the expected one (it reports '{reported}')",
                     "no --expect-version/--expect-this-version given — say which build you installed and this is checked");
                return;
            }

            // The LABEL says whose claim this is. With --expect-this-version
            // the expectation is the running CLI's own build, and "the
            // deployed build is '0.3.2+g9ebbc48'" then reads as a statement
            // about the server when it is a statement about the client.
            var label = expectVersionFromSelf
                ? $"the service is running this CLI's own build ('{expectVersion}')"
                : $"the deployed build is '{expectVersion}'";
            Check(label, () =>
            {
                // An expectation carrying '+' names an exact build and is
                // compared exactly. A bare X.Y.Z is what an operator types from
                // the tag, so it matches any build OF that release — except a
                // dirty one, which is refused: "0.2.0" asked for the release,
                // and a tree with uncommitted edits is not it. That refusal is
                // the entire reason the suffix exists.
                if (expectVersion.Contains('+', StringComparison.Ordinal))
                {
                    if (!string.Equals(reported, expectVersion, StringComparison.Ordinal))
                        throw new InvalidOperationException(Mismatch(reported, expectVersion));
                    return;
                }

                var release = reported.Split('+')[0];
                if (!string.Equals(release, expectVersion, StringComparison.Ordinal))
                    throw new InvalidOperationException(Mismatch(reported, expectVersion));
                if (reported.EndsWith(".dirty", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"the service reports '{reported}': the right release, but built from a tree with " +
                        "uncommitted changes — it is not the tagged build and cannot be reproduced from the tag. " +
                        "Rebuild from a clean checkout, or pass the exact string to --expect-version to accept it");
                }
            });
        }

        /// <summary>
        /// Both sides, each with its ROLE, and no causal claim the evidence
        /// does not support. The message this replaces named only server-side
        /// causes — "the restart did not pick up the new binary, or the wrong
        /// tarball was unpacked" — which is right for an expectation an
        /// operator typed and exactly wrong for one the CLI supplied about
        /// itself: a `knapper` two releases stale sends its reader to restart
        /// or re-unpack a production service that is entirely correct
        /// (observed §8b, 2026-08-16). When the self-sourced expectation is
        /// the OLDER of the two, the near end is the likelier fault and the
        /// message says so; when it is newer, the original diagnosis stands,
        /// because that is the build someone just installed.
        /// </summary>
        private string Mismatch(string reported, string expected)
        {
            var side = expectVersionFromSelf
                ? $"this CLI is '{expected}'"
                : $"expected '{expected}'";
            if (expectVersionFromSelf && BuildInfo.CompareReleases(expected, reported) is < 0)
            {
                return $"{side}, the service reports '{reported}' — and this CLI is the OLDER of the two, so " +
                    "the likelier fault is a stale `knapper` on the box you are typing on, not the deployment. " +
                    "Re-run from the build you just installed (or state the expectation with --expect-version) " +
                    "before touching the service";
            }
            return $"{side}, the service reports '{reported}' — the installed build is not the expected one; " +
                "the restart did not pick up the new binary, or the wrong tarball was unpacked";
        }

        // ---- the MCP surface itself ---------------------------------------

        private async Task SurfaceAsync()
        {
            McpClient? client = null;
            try
            {
                await CheckAsync("the MCP endpoint completes initialize", async () =>
                {
                    client = await ConnectAsync().ConfigureAwait(false);
                    if (client.ServerInfo.Name != "knapper")
                        throw new InvalidOperationException($"server identifies as '{client.ServerInfo.Name}', not 'knapper'");
                }).ConfigureAwait(false);

                if (client is null)
                {
                    Skip("the remaining tool checks", "no MCP session — fix the failure above first");
                    return;
                }

                VersionChecks(client.ServerInfo.Version);

                Check("initialize carries the routing instruction and mutation protocol", () =>
                {
                    var instructions = client.ServerInstructions;
                    if (string.IsNullOrWhiteSpace(instructions))
                        throw new InvalidOperationException("server sent no instructions — connected agents get no ground rules");
                    if (!instructions.Contains("expect_sha256", StringComparison.Ordinal))
                        throw new InvalidOperationException("instructions do not state the mutation protocol");
                });

                // The COUNT rides on the ok line even though the set comparison
                // below is the stronger check: the deployment checklist this
                // runbook is worked from asks for a tool count, and without it
                // printed here that number has to be fetched from a second
                // tools/list call by hand — a check re-derived by the operator
                // is a check that can be re-derived wrong.
                await CheckAsync("tools/list is EXACTLY the locked surface", async () =>
                {
                    var names = (await client.ListToolsAsync().ConfigureAwait(false)).Select(t => t.Name).ToList();
                    var missing = ToolNames.All.Except(names, StringComparer.Ordinal).ToList();
                    var unexpected = names.Except(ToolNames.All, StringComparer.Ordinal).ToList();
                    if (missing.Count > 0 || unexpected.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"{names.Count} tool(s) exposed" +
                            (missing.Count > 0 ? $"; MISSING: {string.Join(", ", missing)}" : "") +
                            (unexpected.Count > 0 ? $"; UNEXPECTED: {string.Join(", ", unexpected)}" : "") +
                            (names.Count == 0 ? " — zero tools is the WithTools overload trap, not a config problem" : ""));
                    }
                    return $"{names.Count} tools";
                }).ConfigureAwait(false);

                // What the previous check does NOT cover: it asserts the tool
                // NAMES, and a manifest can carry all thirteen correct names
                // and still be one no client can load. The 2026-08-14 run that
                // accepted §6 ingress returned 14 ok and exit 0 against a
                // server whose vault_search published outputSchema `true` —
                // Claude Code rejected the tool list whole and could not call
                // anything. A green verify has to mean a usable server.
                await CheckAsync("every published tool schema is one a client can load", async () =>
                {
                    var tools = await RawToolsListAsync().ConfigureAwait(false);
                    var problems = tools
                        .SelectMany(tool => ToolSchemaContract.Validate(
                            tool.TryGetProperty("name", out var name) ? name.GetString() ?? "?" : "?",
                            tool.TryGetProperty("inputSchema", out var input) ? input : null,
                            tool.TryGetProperty("outputSchema", out var output) ? output : null))
                        .ToList();
                    if (problems.Count > 0)
                    {
                        throw new InvalidOperationException(
                            string.Join("; ", problems.Take(3)) +
                            (problems.Count > 3 ? $"; (+{problems.Count - 3} more)" : ""));
                    }
                    return $"{tools.Count} schemas";
                }).ConfigureAwait(false);

                await CheckAsync("a no-match search still reports exhaustive scan evidence", async () =>
                {
                    var result = await CallOkAsync(client, "vault_search", new()
                    {
                        ["pattern"] = $"{ProbePrefix}no-such-string-{Environment.ProcessId}",
                        ["literal"] = true,
                    }).ConfigureAwait(false);
                    if (result.GetProperty("truncated").GetBoolean())
                        throw new InvalidOperationException("an empty result claimed truncation");
                    var scanned = result.GetProperty("scannedFiles").GetInt32();
                    if (scanned <= 0)
                    {
                        throw new InvalidOperationException(
                            "scannedFiles is 0 for a query that matched nothing — this is the ripgrep 14 " +
                            "signature: \"no match\" arrives with no evidence the scope was searched. " +
                            "Install a 15.x release build (`knapper doctor` on the CT gates this)");
                    }
                }).ConfigureAwait(false);

                string? firstFile = null;
                await CheckAsync("listing returns a well-formed completeness envelope", async () =>
                {
                    var page = await CallOkAsync(client, "vault_files", new() { ["maxResults"] = 5 }).ConfigureAwait(false);
                    foreach (var field in new[] { "items", "truncated", "generationStart", "generationEnd" })
                    {
                        if (!page.TryGetProperty(field, out _))
                            throw new InvalidOperationException($"envelope has no '{field}'");
                    }
                    var first = page.GetProperty("items").EnumerateArray()
                        .FirstOrDefault(e => !e.GetProperty("isDirectory").GetBoolean());
                    firstFile = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("path", out var path)
                        ? path.GetString()
                        : null;
                }).ConfigureAwait(false);

                if (firstFile is null)
                {
                    Skip("a read returns the whole-file sha256", "no file came back from vault_files (empty vault?)");
                }
                else
                {
                    await CheckAsync($"a read of {firstFile} returns the whole-file sha256", async () =>
                    {
                        var read = await CallOkAsync(client, "vault_read", new() { ["path"] = firstFile }).ConfigureAwait(false);
                        var sha = read.GetProperty("sha256").GetString() ?? "";
                        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower))
                            throw new InvalidOperationException($"sha256 is '{sha}', not 64 lowercase hex digits");
                    }).ConfigureAwait(false);
                }

                await CheckAsync("the mutation surface is wired and answers with typed codes", async () =>
                {
                    // Aimed at a path that cannot exist: the fresh read inside
                    // the critical section fails before any temp file is
                    // created, so this reaches the mutation path and writes
                    // nothing. See the class comment.
                    var error = await CallErrorAsync(client, "vault_edit", new()
                    {
                        ["path"] = $"{ProbePrefix}{Environment.ProcessId}.md",
                        ["expectSha256"] = new string('0', 64),
                        ["edits"] = new[] { new { old = "x", @new = "y" } },
                    }).ConfigureAwait(false);
                    // Contains, not StartsWith: the SDK client prefixes tool
                    // errors with its own "An error occurred invoking 'x':",
                    // so the bracketed code arrives inside the message — which
                    // is exactly how a real agent sees it.
                    if (error.Contains("[NotFound]", StringComparison.Ordinal))
                        return;
                    if (error.Contains("[MutationBlocked]", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "the write gate is CLOSED: " + error + " — an unresolved Sync conflict file, or the " +
                            "sync heartbeat is stale (obsidian-headless down, or knapper-heartbeat.timer not running)");
                    }
                    throw new InvalidOperationException($"expected [NotFound], got: {error}");
                }).ConfigureAwait(false);
            }
            finally
            {
                if (client is not null)
                    await client.DisposeAsync().ConfigureAwait(false);
            }
        }

        // ---- plumbing ------------------------------------------------------

        /// <summary>
        /// Redirects are NEVER followed, and that is a correctness property,
        /// not a preference. An Access application carrying an identity
        /// policy refuses an unauthenticated caller with 302 → the Cloudflare
        /// login page → 200 HTML; a handler that follows it hands every
        /// refusal probe the login page's 200 and the probe reports the vault
        /// surface as answering. Shipped here, and the consequence was not
        /// theoretical: on 2026-08-14 it called CT 106 — correctly secured,
        /// default-deny, no Bypass policy — EXPOSED, and a tunnel was taken
        /// down on the strength of it.
        ///
        /// It stayed invisible because the deployment's OTHER application is
        /// Service Auth only: a flat 403 has nothing to follow, so the one
        /// check positioned to catch this passed while its twin misfired.
        /// </summary>
        private HttpClient NewClient((string Id, string Secret)? token)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var http = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
            if (token is { } t)
            {
                http.DefaultRequestHeaders.Add("CF-Access-Client-Id", t.Id);
                http.DefaultRequestHeaders.Add("CF-Access-Client-Secret", t.Secret);
            }
            return http;
        }

        private async Task<McpClient> ConnectAsync()
        {
            var http = NewClient(owner);
            var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint }, http);
            return await McpClient.CreateAsync(transport).ConfigureAwait(false);
        }

        /// <summary>
        /// A raw initialize POST — used only where the point is the HTTP
        /// STATUS (an Access refusal), which the SDK client turns into an
        /// exception that hides which code came back.
        /// </summary>
        private async Task<HttpResponseMessage> PostInitializeAsync(HttpClient http)
        {
            const string body = """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18",
                "capabilities":{},"clientInfo":{"name":"knapper-verify","version":"1"}}}
                """;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            return await http.SendAsync(request).ConfigureAwait(false);
        }

        /// <summary>
        /// tools/list off the WIRE, deliberately not through the SDK client:
        /// the client does not hand back what the server sent. For a tool
        /// returning a scalar the server publishes the wrapped
        /// <c>{"properties": {"result": …}}</c> schema while
        /// <c>McpClientTool.ProtocolTool.OutputSchema</c> reports the
        /// UNWRAPPED inner one — so a schema check run through the client
        /// inspects a document no client ever receives, and would both accuse
        /// the wrapper tools and miss the real defect in the shape that
        /// reaches Claude Code. Read-only, like everything here.
        /// </summary>
        private async Task<IReadOnlyList<JsonElement>> RawToolsListAsync()
        {
            using var http = NewClient(owner);
            using (var initialize = await PostInitializeAsync(http).ConfigureAwait(false))
            {
                initialize.EnsureSuccessStatusCode();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            // The transport answers either plain JSON or an SSE frame.
            var payload = body.TrimStart().StartsWith('{')
                ? body
                : body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?[5..].Trim()
                  ?? throw new InvalidOperationException("tools/list returned neither JSON nor an SSE data frame");
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"tools/list returned a JSON-RPC error: {error}");
            // Cloned: JsonElements outlive the document they came from here.
            return [.. document.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.Clone())];
        }

        private static async Task<JsonElement> CallOkAsync(McpClient client, string tool, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(tool, args).ConfigureAwait(false);
            if (result.IsError ?? false)
                throw new InvalidOperationException($"{tool} errored: {ErrorText(result)}");
            return result.StructuredContent
                ?? throw new InvalidOperationException($"{tool} returned no structured content — UseStructuredContent is off");
        }

        private static async Task<string> CallErrorAsync(McpClient client, string tool, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(tool, args).ConfigureAwait(false);
            if (!(result.IsError ?? false))
                throw new InvalidOperationException($"{tool} SUCCEEDED where it had to be refused");
            return ErrorText(result);
        }

        private static string ErrorText(ModelContextProtocol.Protocol.CallToolResult result) =>
            string.Join(" | ", result.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(c => c.Text));

        // ---- reporting (same ok/FAIL shape as `knapper doctor`) ------------

        private void Check(string what, Action probe) =>
            CheckAsync(what, () => { probe(); return Task.CompletedTask; }).GetAwaiter().GetResult();

        private Task CheckAsync(string what, Func<Task> probe) =>
            CheckAsync(what, async () => { await probe().ConfigureAwait(false); return (string?)null; });

        /// <summary>
        /// The probe may return one short string of EVIDENCE, appended to the
        /// ok line — a number the check already had to compute, so that reading
        /// the output answers "how many?" as well as "was it right?".
        /// </summary>
        private async Task CheckAsync(string what, Func<Task<string?>> probe)
        {
            try
            {
                var detail = await probe().ConfigureAwait(false);
                Console.WriteLine($"ok    {what}" + (detail is null ? "" : $" ({detail})"));
                _passed++;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"FAIL  {what} ({Flatten(e)})");
                _failures++;
            }
        }

        private void Skip(string what, string why)
        {
            Console.WriteLine($"skip  {what} — {why}");
            _skipped++;
        }

        private static string Flatten(Exception e) =>
            e is AggregateException aggregate ? Flatten(aggregate.Flatten().InnerExceptions[0]) : e.Message;
    }
}
