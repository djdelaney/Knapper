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
    // ANY dot-segment is filtered, matching visibility: queries can't see
    // hidden entries, so their churn (.DS_Store, workspace saves) must not
    // flip changed_during_query for results that can't contain them.
    [InlineData("Notes/.hidden.md", true)]
    [InlineData(".DS_Store", true)]
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

        // Control-dir churn ALONE first, and assert the quiet window before any
        // real write exists to muddy it. Order matters: one real write produces
        // an unpredictable NUMBER of native events (inotify reports creation
        // and modification separately; FSEvents usually coalesces them), so a
        // fence written first races its own second event against this
        // assertion. That raced, and the resulting "counter moved" was
        // indistinguishable from an actual filter leak — the one thing this
        // test exists to detect. Do not reintroduce the fence-first ordering.
        dir.File(".obsidian/workspace.json", "{\"x\": 1}");
        dir.File(".obsidian/workspace.json", "{\"x\": 2}");
        dir.File(".trash/deleted.md", "gone");
        Thread.Sleep(500);
        counter.Current.ShouldBe(before, "control-dir churn moved the generation counter");

        // Then a real write, proving the watcher was live throughout — without
        // this, the assertion above would also pass on a watcher seeing nothing.
        dir.File("real.md", "content");
        WaitUntil(() => counter.Current > before, "watcher missed the fence write");
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
