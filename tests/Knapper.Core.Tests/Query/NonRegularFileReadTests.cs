using System.Diagnostics;
using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// The read surface opened files with File.OpenRead, and open(2) for reading
/// BLOCKS on a FIFO until a writer appears. Mutations already refused
/// non-regular files; reads did not, so a single FIFO named *.md hung every
/// lint (which reads the whole vault) and every in-scope frontmatter search,
/// indefinitely, each holding a request thread. Every call here runs under a
/// timeout so a regression FAILS instead of hanging the suite.
/// </summary>
public sealed class NonRegularFileReadTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();
    private readonly VaultPathResolver _resolver;
    private readonly VaultOptions _options;
    private readonly VaultGenerationCounter _generation = new();
    private readonly VaultFileLister _lister;
    private readonly VaultReadService _reader;

    public NonRegularFileReadTests()
    {
        _dir.File("Notes/Real.md", "---\nstatus: active\n---\n# Real\n[[Pipe]]\n");
        _dir.File("Notes/placeholder");
        MkFifo(Path.Combine(_dir.Path, "Notes", "Pipe.md"));
        _resolver = new VaultPathResolver(_dir.Path);
        _options = new VaultOptions { RootPath = _resolver.Root };
        _lister = new VaultFileLister(_resolver, _generation, _options, ArchivedPrefixes.None);
        _reader = new VaultReadService(_resolver, _options, _generation);
    }

    public void Dispose() => _dir.Dispose();

    private static void MkFifo(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path } })!;
        p.WaitForExit();
        p.ExitCode.ShouldBe(0);
    }

    private static T Bounded<T>(Func<T> call)
    {
        var task = Task.Run(call);
        task.Wait(Bound).ShouldBeTrue("the call hung — a FIFO was opened for reading");
        return task.Result;
    }

    private static KnapperException BoundedThrow(Action call)
    {
        var task = Task.Run(() => Should.Throw<KnapperException>(call));
        task.Wait(Bound).ShouldBeTrue("the call hung — a FIFO was opened for reading");
        return task.Result;
    }

    [Fact]
    public void Read_refuses_a_FIFO_without_blocking() =>
        BoundedThrow(() => _reader.Read("Notes/Pipe.md")).Code.ShouldBe(VaultErrorCode.InvalidArgument);

    [Fact]
    public void Stat_refuses_a_FIFO_without_blocking() =>
        BoundedThrow(() => _reader.Stat("Notes/Pipe.md")).Code.ShouldBe(VaultErrorCode.InvalidArgument);

    [Fact]
    public void Batch_read_reports_the_FIFO_per_item_and_still_reads_the_rest()
    {
        var result = Bounded(() => _reader.BatchRead(
            [new VaultReadRequest("Notes/Pipe.md"), new VaultReadRequest("Notes/Real.md")]));
        result.Items[0].ErrorCode.ShouldBe(VaultErrorCode.InvalidArgument);
        result.Items[1].ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Lint_finishes_and_reports_the_FIFO_as_unexamined()
    {
        var lint = new VaultLintService(_resolver, _lister, _reader, _generation, _options, ArchivedPrefixes.None);
        var result = Bounded(() => lint.Lint(new LintQuery()));
        result.UnexaminedFiles.ShouldContain("Notes/Pipe.md");
    }

    [Fact]
    public void Frontmatter_search_finishes_and_reports_the_FIFO_as_unparseable()
    {
        var search = new FrontmatterSearchService(_resolver, _lister, _reader, _generation, _options, ArchivedPrefixes.None);
        var result = Bounded(() => search.Search(new FrontmatterQuery { Field = "status" }));
        result.Items.ShouldHaveSingleItem().Path.ShouldBe("Notes/Real.md");
        result.UnparseableFiles.ShouldContain("Notes/Pipe.md");
    }

    [Fact]
    public void Listing_with_hashes_refuses_the_FIFO_without_blocking_or_leaking_the_server_path()
    {
        var ex = BoundedThrow(() => _lister.List(new VaultFilesQuery { IncludeSha = true }));
        ex.Message.ShouldContain("Notes/Pipe.md");
        ex.Message.ShouldNotContain(_resolver.Root);
    }

    [Fact]
    public void Ordinary_reads_are_unchanged()
    {
        var read = _reader.Read("Notes/Real.md");
        read.Sha256.ShouldBe(VaultHash.Sha256Hex(File.ReadAllBytes(Path.Combine(_dir.Path, "Notes/Real.md"))));
        var stat = _reader.Stat("Notes/Real.md");
        stat.Sha256.ShouldBe(read.Sha256);
        stat.Size.ShouldBe(new FileInfo(Path.Combine(_dir.Path, "Notes/Real.md")).Length);
    }

    [Fact]
    public void Stat_of_a_file_past_the_read_cap_still_hashes_the_whole_file()
    {
        var big = new string('x', 5000) + "\n";
        _dir.File("Notes/Big.md", big);
        var capped = new VaultReadService(_resolver, new VaultOptions { RootPath = _resolver.Root, MaxReadBytes = 1000 }, _generation);
        var stat = capped.Stat("Notes/Big.md");
        stat.Sha256.ShouldBe(VaultHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(big)));
        stat.IsText.ShouldBe(true);
    }
}
