// knapper — the vault's admin binary. Four small commands, hand-dispatched
// (no CLI framework: nothing here needs option parsing beyond a count).
//
//   knapper git-init            init the vault repo + .gitignore (deliberate act; brief §10)
//   knapper commit              snapshot under the vault-wide commit lock (systemd timer runs this)
//   knapper status              one-screen operational summary
//   knapper doctor              config/dependency checks; exit 1 on any failure
//   knapper audit-tail [n]      last n audit entries (default 20)
//   knapper verify --url …      READ-ONLY checks against a DEPLOYED server
//
// Configuration: appsettings.json next to the binary (same schema as the MCP
// server's Vault/Sync sections) + environment variables (Vault__RootPath=…).
// `verify` is the exception: it is a pure client and reads no vault config,
// so it runs from anywhere that can reach the URL.

using Knapper.Core;
using Knapper.Core.Git;
using Knapper.Core.Locking;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();
var vaultOptions = configuration.GetSection(VaultOptions.SectionName).Get<VaultOptions>() ?? new VaultOptions();
var syncOptions = configuration.GetSection(SyncOptions.SectionName).Get<SyncOptions>() ?? new SyncOptions();

try
{
    return args.FirstOrDefault() switch
    {
        "git-init" => GitInit(),
        "commit" => Commit(),
        "status" => Status(),
        "doctor" => Doctor(),
        "audit-tail" => AuditTail(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 20),
        "verify" => Knapper.Cli.Verify.Run(args),
        _ => Usage(),
    };
}
catch (KnapperException e)
{
    Console.Error.WriteLine($"[{e.Code}] {e.Message}");
    return 1;
}

int Usage()
{
    Console.Error.WriteLine(
        "usage: knapper <git-init|commit|status|doctor|audit-tail [n]|verify --url <url> [--client-id ID --client-secret SECRET]>");
    return 2;
}

(VaultPathResolver Resolver, VaultLockManager Locks) Open()
{
    if (string.IsNullOrWhiteSpace(vaultOptions.RootPath))
        throw new KnapperException(VaultErrorCode.IoError, "Vault:RootPath is not configured");
    if (string.IsNullOrWhiteSpace(vaultOptions.LockDirectory))
        throw new KnapperException(VaultErrorCode.IoError, "Vault:LockDirectory is not configured");
    return (new VaultPathResolver(vaultOptions.RootPath), new VaultLockManager(vaultOptions.LockDirectory));
}

int GitInit()
{
    var (resolver, locks) = Open();
    new GitCommitJob(resolver, locks).Init();
    Console.WriteLine($"initialized git repository in {resolver.Root} with the standard .gitignore");
    Console.WriteLine("REMINDER (brief §10): local-only — NO remote until the credential sweep closes; " +
                      "PBS backups are now the only protection for vault history.");
    return 0;
}

int Commit()
{
    var (resolver, locks) = Open();
    var outcome = new GitCommitJob(resolver, locks).Commit(
        TimeSpan.FromMilliseconds(vaultOptions.LockTimeoutMs),
        vaultOptions.CommitStampPath);
    Console.WriteLine(outcome.Committed ? $"committed {outcome.CommitSha}: {outcome.Message}" : outcome.Message);
    return 0;
}

int Status()
{
    var (resolver, locks) = Open();
    var job = new GitCommitJob(resolver, locks);
    var conflicts = new ConflictDetector(resolver).ScanAll();
    Console.WriteLine($"vault:      {resolver.Root}");
    Console.WriteLine($"locks:      {vaultOptions.LockDirectory}");
    Console.WriteLine($"audit:      {vaultOptions.AuditLogPath}");
    Console.WriteLine($"conflicts:  {(conflicts.Count == 0 ? "none" : string.Join(", ", conflicts))}");
    Console.WriteLine($"git:        {(job.RepoExists ? $"repo present, last commit {Describe(job.LastCommitAgeSeconds())}" : "NO repo (knapper git-init)")}");
    Console.WriteLine($"sync gate:  {syncOptions.Mode}" + (syncOptions.Mode == "heartbeat"
        ? string.IsNullOrWhiteSpace(syncOptions.HeartbeatPath)
            ? " — NO heartbeat path configured (mutations would be blocked; set Sync__HeartbeatPath)"
            : $" — heartbeat {Describe(new FileAgeSyncGate(syncOptions).HeartbeatAgeSeconds())} (max {syncOptions.MaxAgeSeconds}s)"
        : " (mutations NOT gated — dev only)"));
    return conflicts.Count == 0 ? 0 : 1;

    static string Describe(double? ageSeconds) =>
        ageSeconds is { } age ? $"{age:F0}s ago" : "never/missing";
}

int Doctor()
{
    var failures = 0;
    Check("Vault:RootPath configured and exists",
        () => !string.IsNullOrWhiteSpace(vaultOptions.RootPath) && Directory.Exists(vaultOptions.RootPath));
    Check("Vault:LockDirectory configured, outside the vault",
        () => !string.IsNullOrWhiteSpace(vaultOptions.LockDirectory)
              && !PathContainment.IsInsideOrEqual(vaultOptions.LockDirectory, vaultOptions.RootPath));
    Check("Vault:AuditLogPath configured, outside the vault",
        () => !string.IsNullOrWhiteSpace(vaultOptions.AuditLogPath)
              && !PathContainment.IsInsideOrEqual(vaultOptions.AuditLogPath, vaultOptions.RootPath));
    Check("vault filesystem is case-SENSITIVE (hard production requirement)",
        () => !string.IsNullOrWhiteSpace(vaultOptions.RootPath)
              && Directory.Exists(vaultOptions.RootPath)
              && !CaseSensitivityProbe.IsCaseInsensitive(vaultOptions.RootPath));
    Check("Vault:CommitStampPath outside the vault (or unset)",
        () => string.IsNullOrWhiteSpace(vaultOptions.CommitStampPath)
              || !PathContainment.IsInsideOrEqual(vaultOptions.CommitStampPath, vaultOptions.RootPath));
    Check("Vault:MetricsPath outside the vault (or unset)",
        () => string.IsNullOrWhiteSpace(vaultOptions.MetricsPath)
              || !PathContainment.IsInsideOrEqual(vaultOptions.MetricsPath, vaultOptions.RootPath));
    Check($"ripgrep runs and is {RipgrepVersion.MinimumMajor}+ ({vaultOptions.RipgrepPath})", () =>
    {
        var probe = RipgrepVersion.Read(vaultOptions.RipgrepPath);
        // Thrown, not returned false: Check appends the message, and WHICH rg
        // was found is the whole diagnosis — "too old" without a version sends
        // the operator back to the shell to find out.
        if (probe.Error is { } probeError)
            throw new InvalidOperationException(probeError);
        var version = probe.Output!;
        var firstLine = version.Split('\n')[0].Trim();
        if (RipgrepVersion.ParseMajor(version) is not { } major)
            throw new InvalidOperationException($"unrecognized `rg --version` output: '{firstLine}'");
        if (major < RipgrepVersion.MinimumMajor)
            throw new InvalidOperationException(
                $"found '{firstLine}', need {RipgrepVersion.MinimumMajor}+ — older rg reports \"searches\": 0 " +
                "for a query with no matches, which empties the scannedFiles evidence behind every " +
                "\"no match\" answer. Install a 15.x release build; Debian's apt package is still 14.x.");
        return true;
    });
    Check("git repo is LOCAL-ONLY — no remote (brief §10, hard prohibition)", () =>
        string.IsNullOrWhiteSpace(vaultOptions.RootPath)
        || string.IsNullOrWhiteSpace(vaultOptions.LockDirectory)
        || !Directory.Exists(Path.Combine(vaultOptions.RootPath, ".git"))
        || !new GitCommitJob(
                new VaultPathResolver(vaultOptions.RootPath),
                new VaultLockManager(vaultOptions.LockDirectory)).HasRemote());
    Check("git runs", () =>
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "git", RedirectStandardOutput = true };
        psi.ArgumentList.Add("--version");
        using var p = System.Diagnostics.Process.Start(psi);
        p!.WaitForExit(5000);
        return p.ExitCode == 0;
    });
    if (syncOptions.Mode == "heartbeat")
    {
        Check($"sync heartbeat fresh (<{syncOptions.MaxAgeSeconds}s)",
            () => new FileAgeSyncGate(syncOptions).HeartbeatAgeSeconds() is { } age && age <= syncOptions.MaxAgeSeconds);
    }
    else
    {
        Console.WriteLine("warn  sync gate is OPEN — dev only; production sets Sync:Mode=heartbeat");
    }
    return failures == 0 ? 0 : 1;

    void Check(string what, Func<bool> probe)
    {
        bool ok;
        try
        {
            ok = probe();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ok = false;
            what += $" ({e.Message})";
        }
        Console.WriteLine($"{(ok ? "ok   " : "FAIL ")} {what}");
        if (!ok)
            failures++;
    }
}

int AuditTail(int count)
{
    if (string.IsNullOrWhiteSpace(vaultOptions.AuditLogPath) || !File.Exists(vaultOptions.AuditLogPath))
    {
        Console.WriteLine("no audit log");
        return 0;
    }
    foreach (var line in File.ReadLines(vaultOptions.AuditLogPath).TakeLast(count))
        Console.WriteLine(line);
    return 0;
}
