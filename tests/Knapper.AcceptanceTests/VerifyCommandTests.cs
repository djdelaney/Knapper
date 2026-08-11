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
        output.ShouldContain("ok    a no-match search still reports exhaustive scan evidence");
        output.ShouldContain("ok    the mutation surface is wired and answers with typed codes");
        // Loopback is the Access exemption's own target: the ingress checks
        // must announce themselves as skipped, never quietly pass.
        output.ShouldContain("skip  Access refuses unauthenticated callers");
        output.ShouldContain("skip  /health is unreachable from outside the box");
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

    private static (int ExitCode, string Output) RunVerify(int port)
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
        psi.ArgumentList.Add($"http://127.0.0.1:{port}/");

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
