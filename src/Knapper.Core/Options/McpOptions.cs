namespace Knapper.Core.Options;

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>IP literal (never "localhost"). 127.0.0.1 for dev; the LXC binds loopback too — cloudflared is the only ingress and runs on the same host.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3535;

    /// <summary>
    /// Extra Host-header names accepted by the DNS-rebinding guard beyond the
    /// always-allowed loopback names — the public hostname
    /// (mcp.example.com) goes here in production. See HostGuard.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>
    /// Tool names to remove from this deployment's surface — absent from
    /// tools/list AND rejected on tools/call. Unknown names fail startup: a
    /// typo would otherwise silently leave the tool it meant to disable
    /// exposed. The likely use here is running a read-only deployment by
    /// disabling the mutation tools.
    /// </summary>
    public string[] DisabledTools { get; set; } = [];

    /// <summary>Cloudflare Access assertion validation at the origin (the brief's B2 ingress).</summary>
    public AccessOptions Access { get; set; } = new();

    /// <summary>
    /// Serve /health (the detailed body: filesystem paths, generation, conflict
    /// names) to loopback callers only; everyone else gets 404. /up is the
    /// external monitor's endpoint — booleans only.
    /// </summary>
    public bool RestrictHealthToLoopback { get; set; } = true;

    /// <summary>Log every tool call (name, caller, duration, outcome) at Information.</summary>
    public bool LogToolCalls { get; set; } = true;
}
