using Knapper.Core.Mutation;
using Knapper.Core.Options;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The one mapping from configuration to a sync gate, shared by server
/// startup and <c>knapper doctor</c> — which disagreed while each spelled it
/// (security review 2026-11-24).
/// </summary>
public sealed class SyncGatesTests
{
    [Theory]
    [InlineData("heartbeat")]
    [InlineData("Heartbeat")]
    [InlineData("HEARTBEAT")]
    public void Heartbeat_in_any_case_is_the_file_age_gate(string mode) =>
        SyncGates.Create(new SyncOptions { Mode = mode, HeartbeatPath = "/nonexistent/heartbeat" })
            .ShouldBeOfType<FileAgeSyncGate>();

    [Theory]
    [InlineData("open")]
    [InlineData("Open")]
    public void Open_is_the_explicit_opt_out(string mode) =>
        SyncGates.Create(new SyncOptions { Mode = mode }).ShouldBeSameAs(StaticSyncGate.Open);

    [Theory]
    [InlineData("hearbeat")]
    [InlineData("")]
    [InlineData(" open")]
    public void Anything_else_is_refused_never_treated_as_open(string mode) =>
        Should.Throw<InvalidOperationException>(() => SyncGates.Create(new SyncOptions { Mode = mode }));

    [Fact]
    public void Heartbeat_with_no_path_is_refused() =>
        Should.Throw<InvalidOperationException>(() =>
            SyncGates.Create(new SyncOptions { Mode = "heartbeat", HeartbeatPath = "" }));
}
