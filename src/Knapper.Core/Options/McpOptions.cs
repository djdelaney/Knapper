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

    /// <summary>
    /// Read-only deployment: every tool whose <c>[McpServerTool]</c> does not
    /// declare <c>ReadOnly = true</c> is disabled (absent from list AND call),
    /// on top of <see cref="DisabledTools"/>. DERIVED from the attributes,
    /// never listed, so a write tool added later is covered without anyone
    /// remembering this setting exists — and a tool that forgets to declare
    /// the flag counts as a writer, the safe direction.
    /// </summary>
    public bool ReadOnly { get; set; }

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

    /// <summary>
    /// The vault's display name, as the server instructions tell agents to
    /// call it (e.g. the name the operator's own notes use). Unset, the
    /// instructions name no vault. Deployment config for the reason
    /// <see cref="ConventionsOptions"/> is: the build ships nobody's vault.
    /// Validated at startup by <see cref="ValidateVaultName"/>.
    /// </summary>
    public string? VaultName { get; set; }

    /// <summary>
    /// Where ASP.NET Core Data Protection keeps its key ring (e.g.
    /// <c>/var/lib/knapper/dataprotection-keys</c>). Unset, a host with no
    /// writable profile — systemd's ProtectHome — falls back to an in-memory
    /// ring and logs three warnings on every start. Nothing here uses Data
    /// Protection for anything that matters, but permanent benign warnings
    /// train an operator to stop reading the log. Absolute, OUTSIDE the vault
    /// (refused at startup otherwise), created owner-only.
    ///
    /// <para>⚠️ The keys protect nothing TODAY, which is the whole argument for
    /// keeping them as plain files. The day Data Protection gains a real
    /// consumer (cookies, antiforgery, an IDataProtector anywhere), they stop
    /// being inert and an at-rest decision (ProtectKeysWith*) is owed — in
    /// the same change. See docs/extending.md.</para>
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }

    /// <summary>Upper bound on <see cref="VaultName"/>: it is a name, spliced into capped prose.</summary>
    public const int MaxVaultNameLength = 64;

    /// <summary>Null when valid (or unset); otherwise the reason startup refuses it.</summary>
    public static string? ValidateVaultName(string? name)
    {
        if (name is null || name.Length == 0)
            return null;
        if (name.Trim().Length == 0)
            return "Mcp:VaultName is whitespace; unset it instead";
        if (name.Length > MaxVaultNameLength)
            return $"Mcp:VaultName is {name.Length} characters; the cap is {MaxVaultNameLength}";
        if (name.Any(char.IsControl) || name.Contains('"'))
            return "Mcp:VaultName may not contain control characters or a double quote (it is quoted in the instructions)";
        return null;
    }
}
