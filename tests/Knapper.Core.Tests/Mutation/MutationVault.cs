using System.Text;
using Knapper.Core.Generation;
using Knapper.Core.Locking;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// A fresh vault per test (mutation tests mutate — no shared fixture).
/// Lock dir and audit log live OUTSIDE the vault, as in production.
/// </summary>
public sealed class MutationVault : IDisposable
{
    public TempDir VaultDir { get; } = new();
    public TempDir Outside { get; } = new();
    public VaultPathResolver Resolver { get; }
    public VaultLockManager Locks { get; }
    public VaultGenerationCounter Generation { get; } = new();
    public ConflictDetector Conflicts { get; }
    public VaultOptions Options { get; }
    public AuditLog Audit { get; }
    public string AuditPath { get; }
    public VaultMutationService Service { get; }

    public KnapperMetrics Metrics { get; } = new();

    public MutationVault()
    {
        Resolver = new VaultPathResolver(VaultDir.Path);
        Locks = new VaultLockManager(Path.Combine(Outside.Path, "locks"));
        Conflicts = new ConflictDetector(Resolver);
        AuditPath = Path.Combine(Outside.Path, "audit.jsonl");
        Audit = new AuditLog(AuditPath, Metrics);
        Options = new VaultOptions
        {
            RootPath = Resolver.Root,
            LockDirectory = Path.Combine(Outside.Path, "locks"),
            AuditLogPath = AuditPath,
        };
        Service = new VaultMutationService(
            Resolver, Locks, Generation, Conflicts, StaticSyncGate.Open, Options, SyncOptions, Audit);
    }

    /// <summary>
    /// The size ceiling applies in every sync mode (a mode-shaped hole in a
    /// guard is a bypass), so fixtures carry a real one. Tests that need to
    /// exercise the limit shrink it rather than writing 5 MB of fixture.
    /// </summary>
    public SyncOptions SyncOptions { get; } = new();

    /// <summary>A service whose sync gate is closed — for fail-closed tests.</summary>
    public VaultMutationService BlockedService => new(
        Resolver, Locks, Generation, Conflicts,
        new StaticSyncGate(false), Options, SyncOptions, Audit);

    /// <summary>A service with a deliberately tiny sync ceiling.</summary>
    public VaultMutationService ServiceWithMaxFileBytes(long max) => new(
        Resolver, Locks, Generation, Conflicts, StaticSyncGate.Open, Options,
        new SyncOptions { MaxFileBytes = max }, Audit);

    public string Write(string relative, string content)
    {
        VaultDir.File(relative, content);
        return VaultHash.Sha256Hex(Encoding.UTF8.GetBytes(content));
    }

    public string ReadText(string relative) =>
        File.ReadAllText(Path.Combine(VaultDir.Path, relative));

    public string[] AuditLines() =>
        File.Exists(AuditPath) ? File.ReadAllLines(AuditPath) : [];

    public void Dispose()
    {
        Generation.Dispose();
        Metrics.Dispose();
        Outside.Dispose();
        VaultDir.Dispose();
    }
}
