using System.Diagnostics;
using System.Text.Json;
using Knapper.Core.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// GENUINE two-process mutation races (brief §7/§13): real child processes
/// (Knapper.MutationProbe) execute mutations through the real
/// VaultMutationService while this process races them. The transport-level
/// version of these tests arrives with the MCP host; the mutation semantics
/// are pinned here first.
/// </summary>
public sealed class TwoProcessMutationTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    private sealed record ProbeResult(bool Ok, string? Code, string? NewSha);

    private Process StartProbe(params string[] probeArgs)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Knapper.MutationProbe.dll");
        File.Exists(dll).ShouldBeTrue($"probe not found at {dll}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        foreach (var a in probeArgs)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static ProbeResult Await(Process probe)
    {
        var stdout = probe.StandardOutput.ReadToEnd();
        var stderr = probe.StandardError.ReadToEnd();
        probe.WaitForExit(30_000).ShouldBeTrue("probe never exited");
        var line = stdout.Trim().Split('\n').LastOrDefault();
        line.ShouldNotBeNullOrEmpty($"probe produced no result; stderr: {stderr}");
        var json = JsonDocument.Parse(line!).RootElement;
        var result = new ProbeResult(
            json.GetProperty("ok").GetBoolean(),
            json.GetProperty("code").GetString(),
            json.GetProperty("newSha").GetString());
        probe.Dispose();
        return result;
    }

    private string LockDir => _v.Options.LockDirectory;

    [Fact]
    public void An_edit_landed_by_another_process_stales_our_base()
    {
        var sha = _v.Write("Notes/shared.md", "line one\n");

        // The other process wins with the same base we read.
        var winner = Await(StartProbe("edit", _v.VaultDir.Path, LockDir,
            "Notes/shared.md", sha, "line one", "their line"));
        winner.Ok.ShouldBeTrue();

        // Our edit against the now-stale base: clean typed rejection, no corruption.
        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/shared.md", sha, [new EditSpec("line one", "our line")]));
        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        _v.ReadText("Notes/shared.md").ShouldBe("their line\n");
    }

    [Fact]
    public void Concurrent_edits_from_the_same_base_produce_exactly_one_winner()
    {
        var sha = _v.Write("counter.md", "value: 0\n");

        var probes = Enumerable.Range(1, 4)
            .Select(n => StartProbe("edit", _v.VaultDir.Path, LockDir,
                "counter.md", sha, "value: 0", $"value: {n}"))
            .ToList();
        var results = probes.Select(Await).ToList();

        results.Count(r => r.Ok).ShouldBe(1);
        results.Where(r => !r.Ok).ShouldAllBe(r => r.Code == "PreconditionFailed");

        var content = _v.ReadText("counter.md");
        content.ShouldMatch(@"^value: [1-4]\n$"); // one winner's value, never a mangled mix
        var winner = results.Single(r => r.Ok);
        VaultHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(content)).ShouldBe(winner.NewSha);
    }

    [Fact]
    public void Simultaneous_no_clobber_creates_yield_exactly_one_file()
    {
        var a = StartProbe("create", _v.VaultDir.Path, LockDir, "fresh.md", "content A\n");
        var b = StartProbe("create", _v.VaultDir.Path, LockDir, "fresh.md", "content B\n");
        var results = new[] { Await(a), Await(b) };

        results.Count(r => r.Ok).ShouldBe(1);
        results.Single(r => !r.Ok).Code.ShouldBe("AlreadyExists");

        var content = _v.ReadText("fresh.md");
        content.ShouldBeOneOf("content A\n", "content B\n");
        VaultHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(content))
            .ShouldBe(results.Single(r => r.Ok).NewSha);
    }

    [Fact]
    public void Appends_serialized_by_the_lock_from_fresh_reads_all_land()
    {
        var sha = _v.Write("log.md", "start\n");

        // Two processes append sequentially, each against a FRESH read — the
        // protocol agents must follow. Both must land, nothing lost.
        var first = Await(StartProbe("append", _v.VaultDir.Path, LockDir, "log.md", sha, "first\n"));
        first.Ok.ShouldBeTrue();
        var second = Await(StartProbe("append", _v.VaultDir.Path, LockDir, "log.md", first.NewSha!, "second\n"));
        second.Ok.ShouldBeTrue();

        _v.ReadText("log.md").ShouldBe("start\nfirst\nsecond\n");
    }
}
