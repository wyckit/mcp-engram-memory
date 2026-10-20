using System.Text.RegularExpressions;

namespace McpEngramMemory.Tools;

/// <summary>Explicit coverage requests and bounded, lexical source evidence signals.</summary>
internal sealed partial class BroadRecallIntent(bool compound)
{
    public bool Compound { get; } = compound;

    public static BroadRecallIntent? Parse(string query)
    {
        if (query.Length > 1024 || !BroadRecallRanking.IsTopicQuery(query) ||
            query.Any(char.IsDigit) || query.Contains('"') || query.Contains('\'')) return null;
        bool compound = CompoundTopics().IsMatch(query);
        return compound || Overview().IsMatch(query) ? new(compound) : null;
    }

    // Prefer substantive context to questions, pointers and plans to document
    // an answer. This is a bounded lexical preference, not entailment: fragments
    // and explanations remain useful without a prescribed subject/verb form.
    public static bool HasSourceEvidence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384) return false;
        var statements = Statements().Split(text);
        if (statements.Length > 32) return false;
        return statements.Any(statement => !Question().IsMatch(statement) &&
            RecallQueryEvidence.EvidenceClauses(RecallQueryEvidence.WithoutNonAnswerClauses(statement))
                .Any(clause => !string.IsNullOrWhiteSpace(clause) && !TopicOnly().IsMatch(clause) &&
                    !ReferenceOrPlan().IsMatch(clause)));
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    [GeneratedRegex(@"\b(?:overview|summary)\b", Options)]
    private static partial Regex Overview();
    [GeneratedRegex(@"\b[\p{L}][\p{L}-]*\s+and\s+[\p{L}][\p{L}-]*\b", Options)]
    private static partial Regex CompoundTopics();
    [GeneratedRegex(@"[;\r\n]+|[.!?](?:\s+|$)", Options)]
    private static partial Regex Statements();
    [GeneratedRegex(@"^\s*(?:what|which|who|where|when|why|how|is|are|does|do|can|could|should|would)\b", Options)]
    private static partial Regex Question();
    [GeneratedRegex(@"\b(?:lists?|indexes?)\b.*\bas\s+topics?\b|\bwithout\s+(?:adopting|choosing|specifying|recording)\b", Options)]
    private static partial Regex TopicOnly();
    [GeneratedRegex(@"\b(?:links?|points?|refers?)\s+to\b|\b(?:asks?|requests?)\b.*\bto\s+(?:review|document|record|label|update)\b|\b(?:documentation\s+task|task\s+to\s+(?:document|record|update))\b", Options)]
    private static partial Regex ReferenceOrPlan();
}
