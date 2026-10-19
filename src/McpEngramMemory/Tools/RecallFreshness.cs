using System.Text.RegularExpressions;

namespace McpEngramMemory.Tools;

/// <summary>
/// Conservative, local record-status cues for recall selection. These are not truth,
/// authority, or date inference: unmarked memories remain of unknown currentness.
/// </summary>
internal static partial class RecallFreshness
{
    public static bool IsSuperseded(string? text) => !string.IsNullOrWhiteSpace(text) &&
        (StatusLabel().IsMatch(text) || PastRecordLabel().IsMatch(text) || SelfStatus().IsMatch(text));

    public static bool PrefersCurrent(string query)
    {
        // Comparisons need both versions. "What replaced the old setting?" asks for
        // the replacement, whereas "What was replaced?" asks for the earlier value.
        if (HistoryOrComparison().IsMatch(query) || PassiveReplacement().IsMatch(query)) return false;
        if (ReplacementQuestion().IsMatch(query)) return true;
        return !PastQuestion().IsMatch(query);
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    [GeneratedRegex(@"^\s*(?:\[(?:superseded|obsolete)\]|(?:superseded|obsolete)\s*:)", Options)]
    private static partial Regex StatusLabel();

    // Natural-language labels must also describe a past fact or label a record;
    // merely discussing "superseded records" is not a status declaration.
    [GeneratedRegex(@"^\s*(?:superseded|obsolete)\b[^.!?\r\n]*\b(?:was|were|(?:decision|record|note|setting)\s*(?::|from\b))", Options)]
    private static partial Regex PastRecordLabel();

    [GeneratedRegex(@"(?:^|[.!?\r\n])\s*this\s+(?:\w+\s+){0,2}(?:memory|record|decision|note|fact|setting|policy|rule)\s+(?:is|has been)\s+(?:now\s+)?(?:superseded|obsolete)(?:\s*(?:[.!?;]|$)|\s+(?:after|since|by|as of)\b)", Options)]
    private static partial Regex SelfStatus();

    [GeneratedRegex(@"\b(?:history|historical|compare|comparison)\b", Options)]
    private static partial Regex HistoryOrComparison();

    [GeneratedRegex(@"\b(?:was|were)\s+(?:\w+\s+){0,3}(?:replaced|superseded)\b", Options)]
    private static partial Regex PassiveReplacement();

    [GeneratedRegex(@"^\s*(?:what|which|who)\b[^?\r\n]*\b(?:replaced|replaces|superseded|supersedes)\b", Options)]
    private static partial Regex ReplacementQuestion();

    [GeneratedRegex(@"\b(?:old|previous|previously|former|formerly|original|originally|used to|at the time)\b|\b(?:what|which|who|where|when|why|how)\s+(?:was|were)\b|\bbefore\s+(?:\w+\s+){0,4}(?:migration|handover|change|incident|update|upgrade|cutover|transition|review)\b", Options)]
    private static partial Regex PastQuestion();
}
