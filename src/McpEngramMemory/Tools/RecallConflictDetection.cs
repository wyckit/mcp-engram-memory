using System.Globalization;
using System.Text.RegularExpressions;

namespace McpEngramMemory.Tools;

/// <summary>
/// A bounded English claim grammar with explicit paraphrases and negation handling,
/// not general semantic entailment or truth resolution.
/// Only compare the same subject and single-valued setting in the same namespace.
/// Dates, IDs, similarity, and graph direction do not establish factual authority.
/// </summary>
internal static partial class RecallConflictDetection
{
    internal sealed record Claim(string Subject, string Kind, string Value, bool Negated = false)
    {
        public bool ConflictsWith(Claim other)
        {
            if (Subject != other.Subject || Kind != other.Kind || (Negated && other.Negated)) return false;
            // Excluding 30 seconds agrees with 45 seconds; two different exclusions
            // can both hold. A positive assertion conflicts only with its own denial.
            return Negated != other.Negated ? Value == other.Value : Value != other.Value;
        }
    }

    internal static Claim? Parse(string? text) => ParseAll(text).FirstOrDefault();

    internal static IReadOnlyList<Claim> ParseAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 ||
            RecallFreshness.IsSuperseded(text)) return [];
        // Normalize only grammatical possessives and supported contractions before
        // rejecting quotation. Entity/environment words remain part of every key.
        string normalized = Possessive().Replace(text, "$1");
        normalized = DoesNot().Replace(normalized, "does not");
        normalized = IsNot().Replace(normalized, "is not");
        if (RecordQualifier().IsMatch(normalized)) return [];

        var statements = Sentences().Split(normalized)
            .Select(s => NormalizeTimeout(Whitespace().Replace(s.Trim(), " ").ToLowerInvariant())).ToArray();
        // A conditional heading can qualify following sentences. Do not discard its
        // clause and then accidentally promote the rest of that paragraph to fact.
        if (statements.Any(s => ClauseQualifier().IsMatch(s))) return [];

        var claims = new List<Claim>();
        // Keep document-wide qualification conservative; splitting must not turn a
        // proposed/example/historical paragraph into unqualified factual claims.
        foreach (string statement in statements.Take(8))
        {
            if (statement.Length > 256) continue;
            var claim = ParseStatement(statement);
            if (claim is not null && !claims.Contains(claim)) claims.Add(claim);
            if (claims.Count == 4) break;
        }
        return claims;
    }

    private static Claim? ParseStatement(string statement)
    {
        // Structural paraphrases map to the same typed property. This is a closed
        // set of aliases, not similarity-based entity merging or general inference.
        var owner = ActiveOwner().Match(statement);
        if (owner.Success) statement = $"{OwnerSubject(owner.Groups[2].Value)} is {owner.Groups[1].Value}";
        var passiveOwner = PassiveOwner().Match(statement);
        if (passiveOwner.Success) statement = $"{OwnerSubject(passiveOwner.Groups[1].Value)} is {passiveOwner.Groups[2].Value}";
        var location = Location().Match(statement);
        if (location.Success) statement = $"{location.Groups[1].Value} region is {location.Groups[2].Value}";
        var setting = UsesSetting().Match(statement);
        if (setting.Success) statement = $"{setting.Groups[1].Value} {setting.Groups[2].Value} is {setting.Groups[3].Value}";
        var feature = FeatureFor().Match(statement);
        if (feature.Success) statement = $"{Article(feature.Groups[3].Value)} {Article(feature.Groups[1].Value)} is {feature.Groups[2].Value}";

        var match = Assertion().Match(statement);
        if (!match.Success) return ParseProposition(statement);
        string subject = Article(match.Groups[1].Value);
        var forSubject = PropertyFor().Match(subject);
        if (forSubject.Success) subject = $"{Article(forSubject.Groups[2].Value)} {Article(forSubject.Groups[1].Value)}";
        if (!Subject().IsMatch(subject) || !subject.Contains(' ')) return null;
        string value = match.Groups[2].Value;
        bool negated = value.StartsWith("not ", StringComparison.Ordinal);
        if (negated) value = value[4..];

        if (value is "enabled" or "disabled" or "on" or "off")
            return new(subject, "boolean", (value is "enabled" or "on") != negated ? "enabled" : "disabled");

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
            return new(subject, "duration", (number * milliseconds).ToString("G29", CultureInfo.InvariantCulture) + "ms", negated);
        }
        if (CountSubject().IsMatch(subject) && long.TryParse(value, NumberStyles.None,
            CultureInfo.InvariantCulture, out long count)) return new(subject, "count", count.ToString(CultureInfo.InvariantCulture), negated);
        if (subject.EndsWith(" owner", StringComparison.Ordinal) && Owner().IsMatch(value)) return new(subject, "owner", value, negated);
        if (subject.EndsWith(" region", StringComparison.Ordinal) && Identifier().IsMatch(value)) return new(subject, "region", value, negated);
        if (subject.EndsWith(" endpoint", StringComparison.Ordinal) && Endpoint().IsMatch(value)) return new(subject, "endpoint", value, negated);
        if (subject.EndsWith(" version", StringComparison.Ordinal) && Version().IsMatch(value)) return new(subject, "version", value.TrimStart('v'), negated);
        return null;
    }

    private static Claim? ParseProposition(string statement)
    {
        var match = Proposition().Match(statement);
        if (!match.Success) return null;
        string subject = Article(match.Groups[1].Value);
        string predicate = match.Groups[3].Value.TrimEnd('s');
        if (predicate == "permit") predicate = "allow";
        string target = Article(match.Groups[4].Value);
        if (!Subject().IsMatch(subject) || !Subject().IsMatch(target)) return null;
        // Supports Linux and supports Windows can both be true. Include the object
        // and predicate in the key; only an explicit denial of that claim conflicts.
        return new($"{subject} {predicate} {target}", "proposition", match.Groups[2].Success ? "false" : "true");
    }

    private static string Article(string text) => text.StartsWith("the ", StringComparison.Ordinal) ? text[4..] : text;

    private static string NormalizeTimeout(string statement)
    {
        var match = TimeoutVerb().Match(statement);
        return match.Success ? $"{match.Groups[1].Value} timeout is {match.Groups[2].Value}" : statement;
    }

    private static string OwnerSubject(string text)
    {
        text = Article(text);
        if (text.EndsWith(" deployments", StringComparison.Ordinal)) text = text[..^1];
        return text + " owner";
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    // Reject conditional, proposed, historical, or multi-valued assertions instead of
    // flattening their qualifiers into a current single-valued claim.
    [GeneratedRegex(@"[\""'’`]|\b(?:was|were|previous|previously|formerly|history|historical|proposed|proposal|planned|draft|expected|example|for instance|hypothetical|suppose|assuming|imagine|possibly|maybe|may|might|should|could|would|will)\b", Options)]
    private static partial Regex RecordQualifier();

    [GeneratedRegex(@"\b(?:if|unless|when|before|after|since|until|during|as of|in \d{4}|and|or|except|only|always|never|sometimes|usually|necessarily)\b", Options)]
    private static partial Regex ClauseQualifier();

    [GeneratedRegex(@"[;\r\n]+|[.!?](?:\s+|$)", Options)]
    private static partial Regex Sentences();

    [GeneratedRegex(@"([\p{L}\p{N}])['’]s\b", Options)]
    private static partial Regex Possessive();

    [GeneratedRegex(@"\bdoesn['’]t\b", Options)]
    private static partial Regex DoesNot();

    [GeneratedRegex(@"\bisn['’]t\b", Options)]
    private static partial Regex IsNot();

    [GeneratedRegex(@"\s+", Options)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(.+?)(?: (?:is set to|is configured to|is configured as|is|equals) |\s*[:=]\s*)(.+)$", Options)]
    private static partial Regex Assertion();

    [GeneratedRegex(@"^[\p{L}\p{N}_-]+(?: [\p{L}\p{N}_-]+)*$", Options)]
    private static partial Regex Subject();

    [GeneratedRegex(@"^(.+?) for (.+)$", Options)]
    private static partial Regex PropertyFor();

    [GeneratedRegex(@"^(.+?) times out after (.+)$", Options)]
    private static partial Regex TimeoutVerb();

    [GeneratedRegex(@"^(.+?) (?:owns|is responsible for) (.+)$", Options)]
    private static partial Regex ActiveOwner();

    [GeneratedRegex(@"^(.+?) (?:is|are) owned by (.+)$", Options)]
    private static partial Regex PassiveOwner();

    [GeneratedRegex(@"^(.+?) (?:is hosted|is located|runs) in ([a-z]{2}(?:-[a-z0-9]+)+-\d+)$", Options)]
    private static partial Regex Location();

    [GeneratedRegex(@"^(.+?) (?:uses|has) (?:an? )?(.+?) of (.+)$", Options)]
    private static partial Regex UsesSetting();

    [GeneratedRegex(@"^(.+?) is ((?:not )?(?:enabled|disabled|on|off)) for (.+)$", Options)]
    private static partial Regex FeatureFor();

    [GeneratedRegex(@"^(.+?) (does not )?(supports?|requires?|allows?|permits?|uses?) (.+)$", Options)]
    private static partial Regex Proposition();

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
