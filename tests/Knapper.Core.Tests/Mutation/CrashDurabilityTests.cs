using System.Diagnostics;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// What a move or a delete leaves on disk when the process DIES in the middle
/// of it — kill -9, OOM, a power cut, systemd restarting the unit during a
/// deploy. Real second processes terminated with <c>Environment.FailFast</c>,
/// because a try/finally proves nothing here: the whole question is what
/// survives when no handler runs at all.
///
/// <para>The invariant: <b>at every instant, a pathname a human, an agent, a
/// query, a health walk and git can all see holds the content.</b> An earlier
/// ordering captured the source before publishing the destination, with an
/// fsync in between — so a crash in that window left the note reachable only
/// through <c>.knapper-tmp-*</c> names, which are gitignored, skipped by every
/// walk and unaddressable through the resolver, while Obsidian Sync
/// propagated the visible deletion to every device. Publishing first is what
/// makes that window not exist (found in review, 2026-08-19).</para>
///
/// <para>Note what is NOT required: a journal, a startup recovery pass, or a
/// sweeper. There is nothing to recover — the vault is always in one of two
/// consistent states, and the residue is a hidden duplicate of content that
/// is already at a normal pathname. ONE demonstrated exception exists — a
/// kill between a raced replace's two exchanges — pinned at the bottom of
/// this class as the accepted residual it is, not hidden behind the rule.</para>
/// </summary>
public sealed class CrashDurabilityTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    private const string Content = "the note that must not vanish\n";

    /// <summary>Every file a human, an agent, a query or git could find — no dot-entries, at any depth.</summary>
    private string[] VisibleFiles() =>
        Directory.EnumerateFiles(_v.VaultDir.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_v.VaultDir.Path, f))
            .Where(rel => !rel.Split('/').Any(segment => segment.StartsWith('.')))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private void CrashDuring(string killPoint, params string[] probeArgs)
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
        psi.ArgumentList.Add(killPoint);

        using var probe = Process.Start(psi)!;
        probe.StandardOutput.ReadToEnd();
        probe.StandardError.ReadToEnd();
        probe.WaitForExit(30_000).ShouldBeTrue("probe never exited");
        probe.ExitCode.ShouldNotBe(0, "the probe was supposed to die mid-operation");
    }

    private void ShouldStillHaveTheNote(string because)
    {
        var visible = VisibleFiles();
        visible.ShouldNotBeEmpty(because);
        visible.Any(rel => File.ReadAllText(_v.Absolute(rel)) == Content)
            .ShouldBeTrue($"{because} — visible files were: {string.Join(", ", visible)}");
    }

    [Theory]
    [InlineData("after-link")]
    [InlineData("after-commit")]
    [InlineData("after-capture")]
    public void A_move_killed_mid_operation_always_leaves_the_note_under_a_visible_pathname(string killPoint)
    {
        var sha = _v.Write("Notes/a.md", Content);
        Directory.CreateDirectory(_v.Absolute("Archive"));

        CrashDuring(killPoint, "crash-move", _v.VaultDir.Path, _v.Options.LockDirectory,
            "Notes/a.md", "Archive/a.md", sha);

        ShouldStillHaveTheNote($"a crash {killPoint} must not leave the note only in hidden temps");
    }

    [Theory]
    [InlineData("after-link")]
    [InlineData("after-commit")]
    [InlineData("after-capture")]
    public void A_delete_killed_mid_operation_always_leaves_the_note_under_a_visible_pathname(string killPoint)
    {
        var sha = _v.Write("Notes/a.md", Content);

        CrashDuring(killPoint, "crash-delete", _v.VaultDir.Path, _v.Options.LockDirectory, "Notes/a.md", sha);

        // A soft delete's destination is `.trash/`, which is hidden by
        // design — so for delete the guarantee is the SOURCE side of the same
        // property: the note stays at its own pathname until the trash entry
        // exists, and the trash entry is where a human looks for a deleted
        // note. Either way it is never reachable only through a temp.
        var visible = VisibleFiles();
        var inTrash = File.Exists(_v.Absolute(".trash/Notes/a.md"))
            && File.ReadAllText(_v.Absolute(".trash/Notes/a.md")) == Content;
        (visible.Any(rel => File.ReadAllText(_v.Absolute(rel)) == Content) || inTrash)
            .ShouldBeTrue($"a crash {killPoint} left the note only in temps; visible: {string.Join(", ", visible)}");
    }

    /// <summary>
    /// The residue a crash leaves is a hidden DUPLICATE, never the only copy —
    /// which is what makes it safe to reason about, and what distinguishes it
    /// from the ordering this replaced. Stated as its own assertion because a
    /// future reordering would break it silently.
    /// </summary>
    [Fact]
    public void Crash_residue_is_never_the_only_copy()
    {
        foreach (var killPoint in new[] { "after-link", "after-commit", "after-capture" })
        {
            using var vault = new MutationVault();
            var sha = vault.Write("Notes/a.md", Content);
            Directory.CreateDirectory(vault.Absolute("Archive"));

            var dll = Path.Combine(AppContext.BaseDirectory, "Knapper.MutationProbe.dll");
            var psi = new ProcessStartInfo { FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(dll);
            foreach (var a in new[] { "crash-move", vault.VaultDir.Path, vault.Options.LockDirectory,
                         "Notes/a.md", "Archive/a.md", sha, killPoint })
            {
                psi.ArgumentList.Add(a);
            }
            using var probe = Process.Start(psi)!;
            probe.StandardOutput.ReadToEnd();
            probe.StandardError.ReadToEnd();
            probe.WaitForExit(30_000).ShouldBeTrue("probe never exited");

            foreach (var temp in vault.TempFiles())
            {
                var bytes = File.ReadAllText(vault.Absolute(temp));
                var elsewhere = Directory.EnumerateFiles(vault.VaultDir.Path, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .Any(f => File.ReadAllText(f) == bytes);
                elsewhere.ShouldBeTrue(
                    $"crash residue '{temp}' ({killPoint}) is the ONLY copy of its content — " +
                    "a sweeper could not safely remove it, and no ordinary surface can see it");
            }
        }
    }

    /// <summary>
    /// The ONE exception to the residue-is-a-duplicate rule, DEMONSTRATED so
    /// the vault owner's risk decision rests on evidence (review round
    /// three): a process killed between a raced replace's two exchanges
    /// leaves the stale agent bytes canonical and the displaced external
    /// version only under a hidden temp — a state no serialization of the
    /// raced writes produces, with no receipt issued. Reaching it requires
    /// an external write inside the one-syscall window of the final check
    /// AND a kill inside the few syscalls before the swap-back. Closing it
    /// needs durable recovery metadata plus a startup pass, which this
    /// design deliberately does not have; this test PINS the accepted state
    /// so any change to it — either direction — is loud.
    /// </summary>
    [Fact]
    public void A_raced_replace_killed_between_the_exchanges_leaves_the_documented_residual()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");

        var dll = Path.Combine(AppContext.BaseDirectory, "Knapper.MutationProbe.dll");
        File.Exists(dll).ShouldBeTrue($"probe not found at {dll}");
        var psi = new ProcessStartInfo { FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        foreach (var a in new[] { "crash-replace-raced", _v.VaultDir.Path, _v.Options.LockDirectory,
                     "Notes/a.md", sha, "agent base", "agent update", "sync won\n" })
        {
            psi.ArgumentList.Add(a);
        }
        using var probe = Process.Start(psi)!;
        probe.StandardOutput.ReadToEnd();
        probe.StandardError.ReadToEnd();
        probe.WaitForExit(30_000).ShouldBeTrue("probe never exited");
        probe.ExitCode.ShouldNotBe(0, "the probe was supposed to die between the exchanges");

        // The residual, exactly as accepted: stale agent bytes canonical…
        _v.ReadText("Notes/a.md").ShouldBe("agent update\n");
        // …and the displaced external version surviving ONLY at a hidden name.
        var kept = _v.TempFiles().ShouldHaveSingleItem();
        _v.ReadText(kept).ShouldBe("sync won\n");
    }
}
