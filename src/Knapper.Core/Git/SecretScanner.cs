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
        ("api-key-like", ApiKeyLike()),
        ("bearer-token", BearerToken()),
    ];

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\b(ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[bpoas]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex("""(?i)\b(api[_-]?key|secret|token|passwd|password)\b\s*[:=]\s*["']?[A-Za-z0-9_\-/+]{20,}""")]
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
