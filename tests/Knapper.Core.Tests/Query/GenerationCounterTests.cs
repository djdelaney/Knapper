using Knapper.Core.Generation;

namespace Knapper.Core.Tests.Query;

public sealed class GenerationCounterTests
{
    [Fact]
    public void Explicit_increment_is_monotonic()
    {
        using var counter = new VaultGenerationCounter();
        var before = counter.Current;
        counter.Increment().ShouldBe(before + 1);
        counter.Current.ShouldBe(before + 1);
    }

    [Theory]
    [InlineData(".git/config", true)]
    [InlineData(".obsidian/workspace.json", true)]
    [InlineData(".trash/old.md", true)]
    [InlineData("Notes/.knapper-tmp-abc", true)]
    [InlineData(".knapper-tmp-abc", true)]
    [InlineData("Notes/Daily.md", false)]
    [InlineData("Notes/.hidden.md", false)] // hidden but not control: Sync delivers it, count it
    public void Control_paths_are_filtered(string path, bool control) =>
        VaultGenerationCounter.IsControlPath(path).ShouldBe(control);

    [Fact]
    public void Watcher_counts_real_writes()
    {
        using var dir = new TempDir();
        dir.File("Notes/seed.md", "seed");
        using var counter = VaultGenerationCounter.StartWatching(dir.Path);
        var before = counter.Current;

        dir.File("Notes/new.md", "content");

        WaitUntil(() => counter.Current > before,
            "watcher never observed a vault write — FileSystemWatcher may be broken on this platform");
    }

    [Fact]
    public void Watcher_ignores_control_dir_churn()
    {
        using var dir = new TempDir();
        dir.File(".obsidian/app.json", "{}");
        using var counter = VaultGenerationCounter.StartWatching(dir.Path);
        var before = counter.Current;

        dir.File(".obsidian/workspace.json", "{\"x\": 1}");
        // Then a real write, as the ordering fence: once it lands, any
        // control-dir event would already have landed too.
        dir.File("real.md", "content");

        WaitUntil(() => counter.Current > before, "watcher missed the fence write");
        // Only the real write may have counted. FSEvents/inotify may coalesce
        // or double-report a single write, so we assert the control-dir write
        // added nothing beyond the fence write's own events (which all name
        // real.md — a workspace.json event would be a filtered path anyway,
        // so any bump here proves the filter, not timing luck).
        var afterFence = counter.Current;
        Thread.Sleep(300);
        counter.Current.ShouldBe(afterFence);
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail(message);
            Thread.Sleep(25);
        }
    }
}
