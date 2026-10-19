using System.Globalization;
using System.Text.RegularExpressions;

namespace McpEngramMemory.Tools;

/// <summary>
/// A deliberately small claim grammar, not semantic entailment or truth resolution.
/// Only compare the same subject and single-valued setting in the same namespace.
/// Dates, IDs, similarity, and graph direction do not establish factual authority.
/// </summary>
internal static partial class RecallConflictDetection
{
    internal sealed record Claim(string Subject, string Kind, string Value)
    {
        public bool ConflictsWith(Claim other) => Subject == other.Subject && Kind == other.Kind && Value != other.Value;
    }

    internal static Claim? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 ||
            RecallFreshness.IsSuperseded(text) || Qualified().IsMatch(text)) return null;

        // Only the leading factual sentence participates. A period inside a decimal,
        // version, or path is not a sentence delimiter. Never parse quoted examples.
        var sentence = FirstSentence().Match(text.Trim());
        if (!sentence.Success || sentence.Groups[1].Length > 256) return null;
        string statement = Whitespace().Replace(sentence.Groups[1].Value.Trim(), " ").ToLowerInvariant();
        var match = Assertion().Match(statement);
        if (!match.Success) return null;
        string subject = match.Groups[1].Value;
        if (subject.StartsWith("the ", StringComparison.Ordinal)) subject = subject[4..];
        string value = match.Groups[2].Value;

        if (value is "enabled" or "disabled" or "not enabled" or "not disabled")
            return new(subject, "boolean", value is "enabled" or "not disabled" ? "enabled" : "disabled");

        if (DurationSubject().IsMatch(subject))
        {
            var duration = Duration().Match(value);
            if (!duration.Success || !decimal.TryParse(duration.Groups[1].Value,
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number > 1_000_000_000m)
                return null;
            decimal milliseconds = duration.Groups[2].Value switch
            {
                "ms" or "millisecond" or "milliseconds" => 1,
                "s" or "second" or "seconds" => 1000,
                "minute" or "minutes" => 60_000,
                "hour" or "hours" => 3_600_000,
                "day" or "days" => 86_400_000,
                _ => 0
            };
            return new(subject, "duration", (number * milliseconds).ToString("G29", CultureInfo.InvariantCulture) + "ms");
        }
        if (CountSubject().IsMatch(subject) && long.TryParse(value, NumberStyles.None,
            CultureInfo.InvariantCulture, out long count)) return new(subject, "count", count.ToString(CultureInfo.InvariantCulture));
        if (subject.EndsWith(" owner", StringComparison.Ordinal) && Owner().IsMatch(value)) return new(subject, "owner", value);
        if (subject.EndsWith(" region", StringComparison.Ordinal) && Identifier().IsMatch(value)) return new(subject, "region", value);
        if (subject.EndsWith(" endpoint", StringComparison.Ordinal) && Endpoint().IsMatch(value)) return new(subject, "endpoint", value);
        if (subject.EndsWith(" version", StringComparison.Ordinal) && Version().IsMatch(value)) return new(subject, "version", value.TrimStart('v'));
        return null;
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    // Reject conditional, proposed, historical, or multi-valued assertions instead of
    // flattening their qualifiers into a current single-valued claim.
    [GeneratedRegex(@"[\""'`]|\b(?:if|unless|when|before|after|since|until|during|as of|in \d{4}|was|were|previous|formerly|historical|proposed|planned|expected|example|hypothetical|may|might|should|could|would|will|and|or|except)\b", Options)]
    private static partial Regex Qualified();

    [GeneratedRegex(@"^([^\r\n!?;]+?)(?:\.(?:\s|$)|$)", Options)]
    private static partial Regex FirstSentence();

    [GeneratedRegex(@"\s+", Options)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^([\p{L}\p{N}_-]+(?: [\p{L}\p{N}_-]+)+) is (.+)$", Options)]
    private static partial Regex Assertion();

    [GeneratedRegex(@" (?:timeout|ttl|retention|retention period)$", Options)]
    private static partial Regex DurationSubject();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?) (ms|s|milliseconds?|seconds?|minutes?|hours?|days?)$", Options)]
    private static partial Regex Duration();

    [GeneratedRegex(@" (?:retry limit|replica count|batch size|port)$", Options)]
    private static partial Regex CountSubject();

    [GeneratedRegex(@"^[\p{L}][\p{L}\p{N}_-]*(?: [\p{L}][\p{L}\p{N}_-]*){0,3}$", Options)]
    private static partial Regex Owner();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$", Options)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^/[a-z0-9/_.-]+$", Options)]
    private static partial Regex Endpoint();

    [GeneratedRegex(@"^v?\d+(?:\.\d+){0,3}(?:-[a-z0-9.-]+)?$", Options)]
    private static partial Regex Version();
}
