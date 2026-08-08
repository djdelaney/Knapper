namespace Knapper.Core.Tests;

/// <summary>Disposable scratch directory; recursively deleted best-effort.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } =
        Directory.CreateTempSubdirectory("knapper-test-").FullName;

    public string File(string relative, string? content = null)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        if (content is not null)
            System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
