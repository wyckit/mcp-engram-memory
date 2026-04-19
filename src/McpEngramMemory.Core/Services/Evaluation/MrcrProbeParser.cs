using System.Text.RegularExpressions;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Parses an MRCR v2 probe into its ordinal components and normalizes the user-side
/// "ask" text into a reusable category signature.
///
/// MRCR 8-needle probes follow a fixed template:
///   <c>Prepend RAND_STRING to the Nth (1 indexed) SCENE_TYPE about TOPIC. Do not include any other text in your response.</c>
///
/// The user turns that plant each needle use a twin template:
///   <c>Write me a SCENE_TYPE about TOPIC</c>
///
/// We normalize both to a shared category signature (e.g. <c>short scene in a play about temperatures</c>)
/// so ordinal-indexed retrieval can match on exact category + position.
/// </summary>
public static class MrcrProbeParser
{
    private static readonly Regex ProbeRegex = new(
        @"Prepend\s+(?<rand>\S+)\s+to\s+the\s+(?<ord>\d+)(?:st|nd|rd|th)\s*\(\s*1\s*indexed\s*\)\s+(?<topic>.+?)\s*(?:\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AskPrefixRegex = new(
        @"^\s*(?:write|give|provide|generate|compose|draft|create)\s+(?:me\s+)?(?:a|an|the)?\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string? probe, out MrcrProbeInfo info)
    {
        info = default!;
        if (string.IsNullOrWhiteSpace(probe)) return false;

        var m = ProbeRegex.Match(probe);
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups["ord"].Value, out int ordinal) || ordinal < 1)
            return false;

        info = new MrcrProbeInfo(
            RandomPrefix: m.Groups["rand"].Value.Trim(),
            Ordinal: ordinal,
            CategorySignature: NormalizeSignature(m.Groups["topic"].Value));
        return true;
    }

    /// <summary>
    /// Normalize a user-side ask into a category signature. Strips common request
    /// prefixes ("write me a ...") and collapses whitespace so the same scene type
    /// planted eight times produces eight identical category strings.
    /// </summary>
    public static string NormalizeSignature(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var stripped = AskPrefixRegex.Replace(text, string.Empty);
        var collapsed = Regex.Replace(stripped, @"\s+", " ").Trim().TrimEnd('.', '!', '?', ',');
        return collapsed.ToLowerInvariant();
    }
}

/// <summary>
/// Parsed MRCR probe — the ordinal index, the random string to prepend, and the
/// category signature that both the probe and its matching user asks reduce to.
/// </summary>
public sealed record MrcrProbeInfo(
    string RandomPrefix,
    int Ordinal,
    string CategorySignature);
