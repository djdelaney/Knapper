namespace Knapper.Core.Vault;

/// <summary>
/// A path that has passed <see cref="VaultPathResolver"/>: vault-relative,
/// normalized, contained, symlink-free, and outside every banned directory.
/// Constructible only by the resolver — an API taking <see cref="VaultPath"/>
/// is stating that validation already happened.
/// </summary>
public sealed record VaultPath
{
    /// <summary>Normalized vault-relative path, '/'-separated, no leading slash.</summary>
    public required string Relative { get; init; }

    /// <summary>Absolute path under the canonical vault root.</summary>
    public required string Absolute { get; init; }

    internal VaultPath() { }

    public override string ToString() => Relative;
}
