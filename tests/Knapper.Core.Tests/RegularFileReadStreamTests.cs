using System.Security.Cryptography;
using Knapper.Core.Interop;

namespace Knapper.Core.Tests;

/// <summary>
/// A handle from <see cref="Posix.OpenRegularForRead"/> is O_NONBLOCK, and
/// from .NET 11 the BCL reads such a handle as a pipe: FileStream and
/// RandomAccess stop honouring offsets, without an error. A stat past the
/// read cap answered with the SHA-256 of empty input. These pin the
/// pread-based replacements, and that nothing hands the handle back to the
/// BCL — on .NET 10 the old spelling still passes every behavioural test, so
/// only the source check can fail before the runtime changes underneath it.
/// </summary>
public sealed class RegularFileReadStreamTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_dir.Path, "data.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void PRead_honours_the_offset_and_reports_end_of_file_as_zero()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var handle = Posix.OpenRegularForRead(Write(bytes), "data.bin");

        var buffer = new byte[4];
        Posix.PRead(handle, buffer, 100).ShouldBe(4);
        buffer.ShouldBe(new byte[] { 100, 101, 102, 103 });

        // Out of order, and again: no descriptor position is involved.
        Posix.PRead(handle, buffer, 0).ShouldBe(4);
        buffer.ShouldBe(new byte[] { 0, 1, 2, 3 });

        Posix.PRead(handle, buffer, 254).ShouldBe(2);
        Posix.PRead(handle, buffer, 256).ShouldBe(0);
        Posix.PRead(handle, buffer, 10_000).ShouldBe(0);
    }

    [Fact]
    public void The_stream_hashes_the_whole_file_after_a_partial_read_and_a_rewind()
    {
        // The exact shape Stat uses past the read cap: read a prefix, set
        // Position back to 0, hash the stream. On .NET 11 a FileStream over
        // this handle hashed nothing.
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        using var handle = Posix.OpenRegularForRead(Write(bytes), "data.bin");
        using var stream = new RegularFileReadStream(handle);

        var prefix = new byte[65_536];
        stream.ReadAtLeast(prefix, prefix.Length).ShouldBe(prefix.Length);
        prefix.ShouldBe(bytes[..prefix.Length]);
        stream.Position.ShouldBe(prefix.Length);

        stream.Position = 0;
        Convert.ToHexStringLower(SHA256.HashData(stream))
            .ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        stream.Read(prefix).ShouldBe(0);
    }

    [Fact]
    public void The_stream_seeks_from_every_origin_and_sees_the_live_length()
    {
        var path = Write([1, 2, 3, 4, 5]);
        using var handle = Posix.OpenRegularForRead(path, "data.bin");
        using var stream = new RegularFileReadStream(handle);

        stream.Length.ShouldBe(5);
        stream.Seek(-2, SeekOrigin.End).ShouldBe(3);
        stream.ReadByte().ShouldBe(4);
        stream.Seek(-3, SeekOrigin.Current).ShouldBe(1);
        stream.ReadByte().ShouldBe(2);
        stream.Seek(4, SeekOrigin.Begin).ShouldBe(4);
        stream.ReadByte().ShouldBe(5);
        stream.ReadByte().ShouldBe(-1);

        File.AppendAllBytes(path, [6]);
        stream.Length.ShouldBe(6);
        stream.ReadByte().ShouldBe(6);
    }

    [Fact]
    public void No_source_file_hands_the_non_blocking_handle_to_the_BCL()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Knapper.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return; // not a checkout (a published test binary): nothing to read

        var src = Path.Combine(dir.FullName, "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("OpenRegularForRead(")
                    && (text.Contains("new FileStream(handle") || text.Contains("RandomAccess."));
            })
            .Select(f => Path.GetRelativePath(dir.FullName, f))
            .ToList();

        offenders.ShouldBeEmpty(
            "a handle from Posix.OpenRegularForRead is read through Posix.PRead or RegularFileReadStream only");
    }
}
