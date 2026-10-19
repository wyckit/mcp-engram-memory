using System.Text.RegularExpressions;

namespace McpEngramMemory.Tools;

/// <summary>
/// A bounded preference for explicit historical evidence, not a timeline or date inference.
/// Other in-scope context stays eligible when the requested period cannot be established.
/// </summary>
internal sealed partial class RecallHistorySelection(RecallQueryEvidence topic, HashSet<string> anchor)
{
    public static RecallHistorySelection? Parse(string query)
    {
        if (query.Length > 1024 || RecallFreshness.PrefersCurrent(query) || Comparison().IsMatch(query)) return null;
        query = RecallQueryEvidence.NormalizeQuestion(query);
        // A past-tense incident explanation asks for causes/actions or lessons,
        // not an earlier setting. Preserve that route's existing recovery policy.
        if (ExplanationQuestion().IsMatch(query)) return null;
        var before = Before().Match(query);
        if (!before.Success && UnsupportedPeriod().IsMatch(query)) return null;
        var anchor = before.Success ? Tokens(before.Groups[1].Value) : [];
        if (before.Success && (anchor.Count == 0 || anchor.Count > 8)) return null;
        string subject = before.Success ? query[..before.Index] : query;
        subject = PastAuxiliary().Replace(subject, "is");
        subject = PastQualifier().Replace(subject, "");
        if (!Question().IsMatch(subject)) subject = "What is " + subject;
        var topic = RecallQueryEvidence.Parse(subject);
        return topic is null ? null : new(topic, anchor);
    }

    public bool Prefers(string? text, string ns)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384) return false;
        var sentences = Statements().Split(text);
        if (sentences.Length > 32) return false;
        var statements = sentences.SelectMany(RecallQueryEvidence.EvidenceClauses).Take(65).ToArray();
        if (statements.Length > 64) return false;
        for (int i = 0; i < statements.Length; i++)
        {
            string statement = statements[i];
            if (!Assertion().IsMatch(statement) || !topic.Matches(statement, ns)) continue;
            if (anchor.Count == 0)
            {
                if (RecallFreshness.IsSuperseded(text) || PastFact().IsMatch(statement)) return true;
                continue;
            }
            if (HasAnchor(statement, Before())) return true;
            // A record's next sentence may identify the event that retired it.
            // Never borrow a temporal phrase from an unrelated neighboring fact.
            if (i + 1 < statements.Length && RecallFreshness.IsSuperseded(text) &&
                RetirementContinuation().IsMatch(statements[i + 1]) &&
                HasAnchor(statements[i + 1], Retirement())) return true;
        }
        return false;
    }

    private bool HasAnchor(string statement, Regex relation) => relation.Matches(statement).Any(m =>
        !Negation().IsMatch(statement[..m.Index]) &&
        anchor.IsSubsetOf(Tokens(OtherClause().Split(m.Groups[1].Value)[0])));

    // Preserve literal event names and single-digit dates; search tokenization
    // may drop short numbers or stem names that distinguish two periods.
    private static HashSet<string> Tokens(string text) => AnchorWords().Matches(text).Select(m => m.Value.ToLowerInvariant())
        .Where(t => t is not ("the" or "a" or "an" or "of")).ToHashSet(StringComparer.Ordinal);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    [GeneratedRegex(@"\b(?:compare|comparison|history)\b", Options)]
    private static partial Regex Comparison();
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AnchorWords();
    [GeneratedRegex(@"^\s*(?:how\s+(?:was|were|did)\b|why\b|what\s+(?:was|were)\s+(?:learned|learnt|done|discovered|reported|recorded|documented|changed|fixed|repaired|resolved)\b)", Options)]
    private static partial Regex ExplanationQuestion();
    [GeneratedRegex(@"\b(?:after|since|during|as of|original|originally)\b|\b(?:in|on)\s+\d", Options)]
    private static partial Regex UnsupportedPeriod();
    [GeneratedRegex(@"\b(?:before|prior to|until)\s+([^.!?;\r\n]+)", Options)]
    private static partial Regex Before();
    [GeneratedRegex(@"\b(?:was|were)\b", Options)]
    private static partial Regex PastAuxiliary();
    [GeneratedRegex(@"\b(?:old|previous|previously|former|formerly|historical)\s+", Options)]
    private static partial Regex PastQualifier();
    [GeneratedRegex(@"^\s*(?:what|which|who|where|when|why|how)\b", Options)]
    private static partial Regex Question();
    [GeneratedRegex(@"\b(?:is|are|was|were|use|uses|used|owns|owned)\s+\S+|:\s*\S+", Options)]
    private static partial Regex Assertion();
    [GeneratedRegex(@"\b(?:was|were|used to|formerly|previously)\b", Options)]
    private static partial Regex PastFact();
    [GeneratedRegex(@"[;\r\n]+|[.!?](?:\s+|$)", Options)]
    private static partial Regex Statements();
    [GeneratedRegex(@"^\s*(?:replaced by|superseded by|this (?:was|record|memory|decision|ownership record))\b", Options)]
    private static partial Regex RetirementContinuation();
    [GeneratedRegex(@"\b(?:before|replaced by|superseded by|obsolete after|superseded after)\s+([^.!?;\r\n]+)", Options)]
    private static partial Regex Retirement();
    [GeneratedRegex(@"\b(?:not|never)\s*$", Options)]
    private static partial Regex Negation();
    [GeneratedRegex(@",|\b(?:but|whereas|after|since)\b", Options)]
    private static partial Regex OtherClause();
}
