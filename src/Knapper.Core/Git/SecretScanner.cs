using System.Text.RegularExpressions;

namespace Knapper.Core.Git;

/// <summary>
/// Pre-commit credential scan (brief §10): once git history exists in the
/// vault, a committed secret is forever — Sync can restore files but never
/// erase history, and the 2026-08-01 credential sweep is still open. The
/// commit job refuses to snapshot staged content matching these shapes.
/// Patterns favor precision over recall — this is a tripwire against
/// obvious credentials landing in notes, not a DLP product.
/// </summary>
public static partial class SecretScanner
{
    public sealed record Finding(string File, int Line, string Kind, string Masked);

    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("private-key", PrivateKey()),
        ("aws-access-key", AwsAccessKey()),
        ("github-token", GitHubToken()),
        ("slack-token", SlackToken()),
        ("stripe-key", StripeKey()),
        ("google-api-key", GoogleApiKey()),
        ("jwt", Jwt()),
        ("api-key-like", ApiKeyLike()),
        ("bearer-token", BearerToken()),
    ];

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    // Classic (ghp_), OAuth (gho_), user-to-server (ghu_), server-to-server
    // (ghs_) and refresh (ghr_) tokens share one shape; fine-grained PATs differ.
    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[bpoas]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\b[rs]k_(live|test)_[A-Za-z0-9]{16,}")]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}")]
    private static partial Regex GoogleApiKey();

    // Header and payload are base64url JSON, so both open with "eyJ" ('{"').
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex Jwt();

    // No leading \b, so compound names match too (openaiApiKey, client_secret,
    // accessToken, CF-Access-Client-Secret). An optional closing quote before
    // the separator, because the likeliest place for a key in this vault is a
    // plugin's JSON settings file — `"apiKey": "…"` — and the separator used
    // to have to follow the name directly, so every JSON key walked past.
    [GeneratedRegex("""(?i)(api[_-]?key|secret|token|passwd|password)["']?\s*[:=]\s*["']?[A-Za-z0-9_\-/+]{20,}""")]
    private static partial Regex ApiKeyLike();

    [GeneratedRegex(@"\b(sk-[A-Za-z0-9_\-]{20,}|Bearer\s+[A-Za-z0-9_\-\.=]{30,})")]
    private static partial Regex BearerToken();

    /// <summary>Scan one file's text; line numbers are 1-based.</summary>
    public static IReadOnlyList<Finding> Scan(string file, string content)
    {
        var findings = new List<Finding>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (kind, pattern) in Patterns)
            {
                var match = pattern.Match(lines[i]);
                if (match.Success)
                    findings.Add(new Finding(file, i + 1, kind, Mask(match.Value)));
            }
        }
        return findings;
    }

    /// <summary>Enough to locate the hit, never enough to reconstruct it.</summary>
    /// <summary>
    /// Enough to IDENTIFY the finding (which token type, roughly where),
    /// never enough to reconstruct it: 4 leading chars — which for most
    /// token formats is just the recognizable prefix (AKIA, ghp_, xoxb) —
    /// plus the length. The old 8+2 shape leaked 10 of 13 chars of a short
    /// secret to anyone who could read the refusal message.
    /// </summary>
    private static string Mask(string value) =>
        $"{value[..Math.Min(4, value.Length)]}… ({value.Length} chars)";
}
