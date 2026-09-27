using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Knapper.AcceptanceTests;

/// <summary>
/// The deployed shape of proposal §5, black-box: the REAL `knapper` CLI
/// commits and accepts a baseline into a state file outside the vault, and a
/// separate REAL server process — reading the same file, never told anything
/// else — reports only what changed since. The two binaries share nothing but
/// configuration and the disk, exactly as on CT 106.
/// </summary>
public sealed class LintBaselineAcceptanceTests : IDisposable
{
    private readonly string _vaultDir = Wire.NewTempDir("knapper-accept-vault-");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-accept-outside-");
    private readonly string _baselinePath;

    public LintBaselineAcceptanceTests()
    {
        _baselinePath = Path.Combine(_outsideDir, "state", "lint-baseline.json");
        Wire.Seed(_vaultDir, "Notes/Hub.md", "# Hub\nBacklog: [[No Such Note]].\n");
    }

    public void Dispose()
    {
        Wire.TryDeleteDir(_vaultDir);
        Wire.TryDeleteDir(_outsideDir);
    }

    [Fact]
    public async Task An_accepted_baseline_reaches_a_running_server_through_the_state_file_alone()
    {
        Cli("git-init").ExitCode.ShouldBe(0);
        using var server = new AcceptanceServer(_vaultDir, _outsideDir,
            new Dictionary<string, string> { ["Lint__BaselinePath"] = _baselinePath });
        await using var client = await server.ConnectAsync();

        // Before any accept: the backlog is reported, and the response says
        // there is no baseline rather than leaving the caller to guess.
        var before = await Wire.CallOk(client, "vault_lint", new());
        before.GetProperty("items").GetArrayLength().ShouldBe(1);
        before.GetProperty("baselineCommit").ValueKind.ShouldBe(JsonValueKind.Null);

        var (code, output) = Cli("lint", "--accept");
        code.ShouldBe(0, output);
        output.ShouldContain("1 finding(s) are now the accepted backlog: unresolved_link 1");
        var commit = output.Split('\n').Single(l => l.StartsWith("accepted ", StringComparison.Ordinal)).Split(' ')[1];

        // No restart: the server reads the record on every call.
        var after = await Wire.CallOk(client, "vault_lint", new());
        after.GetProperty("items").GetArrayLength().ShouldBe(0);
        after.GetProperty("suppressedByBaseline").GetInt32().ShouldBe(1);
        after.GetProperty("baselineCommit").GetString().ShouldBe(commit);

        // A write through the server itself is new at once — no commit needed.
        await Wire.CallOk(client, "vault_create",
            new() { ["path"] = "Notes/New.md", ["text"] = "Fresh: [[Brand New Missing]].\n" });
        var fresh = await Wire.CallOk(client, "vault_lint", new());
        var item = fresh.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("subject").GetString().ShouldBe("Brand New Missing");

        var all = await Wire.CallOk(client, "vault_lint", new() { ["all"] = true });
        all.GetProperty("items").GetArrayLength().ShouldBe(2);
        all.GetProperty("baselineCommit").ValueKind.ShouldBe(JsonValueKind.Null);

        Cli("status").Output.ShouldContain($"lint:       baseline {commit} accepted ");
        Cli("doctor").Output.Split('\n')
            .ShouldContain(l => l.StartsWith($"ok    lint baseline commit {commit} is in the vault repository",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Accept_without_a_configured_path_refuses_and_writes_nothing()
    {
        Cli("git-init").ExitCode.ShouldBe(0);
        var (code, output) = Cli(withBaseline: false, "lint", "--accept");
        code.ShouldBe(1, output);
        output.ShouldContain("Lint:BaselinePath is not configured");
        File.Exists(_baselinePath).ShouldBeFalse();
    }

    private (int ExitCode, string Output) Cli(params string[] args) => Cli(withBaseline: true, args);

    private (int ExitCode, string Output) Cli(bool withBaseline, params string[] args)
    {
        var dll = typeof(LintBaselineAcceptanceTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "KnapperCliDll").Value!;
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _outsideDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["exec", dll, .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["Vault__RootPath"] = _vaultDir;
        psi.Environment["Vault__LockDirectory"] = Path.Combine(_outsideDir, "locks");
        psi.Environment["Vault__AuditLogPath"] = Path.Combine(_outsideDir, "cli-audit.jsonl");
        if (withBaseline)
            psi.Environment["Lint__BaselinePath"] = _baselinePath;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(60_000).ShouldBeTrue($"knapper {string.Join(' ', args)} did not finish");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
