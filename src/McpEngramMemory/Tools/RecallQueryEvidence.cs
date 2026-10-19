using System.Text.RegularExpressions;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tools;

/// <summary>
/// Bounded lexical requirements for English factual questions, not semantic entailment.
/// A passing candidate may be useful evidence; it is not certified as a complete or true answer.
/// </summary>
internal sealed partial class RecallQueryEvidence
{
    private static readonly HashSet<string> Noise = Terms("what which who where when why how is are do does did must should can could would will may I we you our their the a an current currently previous old now after before since during work works avoid avoids protect protects cause caused causes mean means use uses used each many often long stored memories memory relevant losing contain contains several first primary setting settings please tell show me about example fields included period quickly wait take keep nightly night January February March April May June July August September October November December");
    private static readonly HashSet<string> ScopeNoise = new((
        "the a an current primary previous old production prod staging stage development dev testing test " +
        "database gateway payment timeout endpoint path port queue backup backups token lifetime cloud budget " +
        "daily monthly routine disaster recovery time objective readiness health security review reviews encryption algorithm " +
        "tools tool profile context block cache caching graph traversal duplicate memory memories ids namespace default child agents " +
        "deployment deployments release releases rollback command region snapshot snapshots incident " +
        "retention retained responsible responsibility for of to from with in at by its his her it this that " +
        "fields included period quickly wait take keep nightly night rto url http qa uat currency jitter deadline " +
        "what which who where when why how is are do does did must should can could would will may i we you our their " +
        "set get use return run restart archived after before since during " +
        "january february march april may june july august september october november december").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Environments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["production"] = "production", ["prod"] = "production", ["staging"] = "staging", ["stage"] = "staging",
        ["development"] = "development", ["dev"] = "development", ["testing"] = "testing", ["test"] = "testing",
        ["qa"] = "qa", ["uat"] = "uat"
    };
    private static readonly HashSet<string> SubjectPredicates = new(
        "production prod staging stage development dev testing test qa uat gateway database backups deployments endpoint readiness rollback recovery security payment context use uses wait waits recover back take keep retry".Split(' '),
        StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _names;
    private readonly HashSet<string> _environments;
    private readonly HashSet<string> _focus;
    private readonly HashSet<string> _attributes;
    private readonly bool _owner;
    private readonly bool _backupSchedule;
    private readonly bool _recoveryDuration;

    private RecallQueryEvidence(HashSet<string> names, HashSet<string> environments, HashSet<string> focus,
        bool owner, bool backupSchedule, bool recoveryDuration)
    {
        (_names, _environments, _focus, _owner) = (names, environments, focus, owner);
        (_backupSchedule, _recoveryDuration) = (backupSchedule, recoveryDuration);
        // Shared project/resource words cannot substitute for a requested attribute.
        _attributes = focus.Intersect(Terms("budget password encryption lifetime retention timeout region endpoint jitter currency"))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static RecallQueryEvidence? Parse(string query)
    {
        if (query.Length > 1024) return null;
        query = NormalizeQuestion(query);
        if (!Question().IsMatch(query)) return null;
        var words = Words().Matches(query).Select(m => m.Value).ToArray();
        var names = words.Where(w => char.IsUpper(w[0]) && w.Any(char.IsLower) && !ScopeNoise.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Also recognize lower-case subjects in common question forms.
        foreach (Match match in SubjectSlots().Matches(query).Concat(AuxiliarySubject().Matches(query)
            .Where(m => SubjectPredicates.Contains(m.Groups[2].Value))))
        {
            string name = match.Groups[1].Value;
            if (!ScopeNoise.Contains(name)) names.Add(name);
        }
        var environments = EnvironmentTerms(query);
        string body = Question().Replace(query, "", 1);
        body = ContextTail().Split(body)[0];
        var focus = Terms(body);
        focus.ExceptWith(Noise);
        focus.ExceptWith(names.SelectMany(Terms));
        focus.ExceptWith(Terms(string.Join(' ', words.Where(Environments.ContainsKey))));
        // Probe phrasing identifies an endpoint question; these are not extra facts
        // that must occur in the same sentence as the endpoint itself.
        if (focus.Contains("endpoint")) focus.ExceptWith(Terms("http probes call"));
        bool recoveryDuration = RecoveryObjective().IsMatch(query) ||
            (QuickRecovery().IsMatch(query) && focus.Contains("recover"));
        if (recoveryDuration) focus.ExceptWith(Terms("time objective"));
        return focus.Count == 0 ? null : new(names, environments, focus,
            query.TrimStart().StartsWith("who ", StringComparison.OrdinalIgnoreCase) && focus.Contains("own"),
            query.TrimStart().StartsWith("when ", StringComparison.OrdinalIgnoreCase) && focus.Contains("backup"),
            recoveryDuration);
    }

    private static string NormalizeQuestion(string query)
    {
        var request = RequestPrefix().Match(query);
        if (request.Success) query = query[request.Length..];
        query = AtWhatTime().Replace(query, "When ");
        if (request.Success && !Question().IsMatch(query)) query = "What is " + query;
        // Preserve the requested timeout before stripping a contextual before/after tail.
        return TimingOut().Replace(query, " timeout");
    }

    public bool Matches(string? text, string ns)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384) return false;
        var namespaceWords = Words().Matches(ns.Replace('_', ' ')).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var precedingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var precedingEnvironments = new HashSet<string>();
        foreach (string statement in Statements().Split(text).Take(32))
        {
            if (Question().IsMatch(NormalizeQuestion(statement))) continue;
            var words = Words().Matches(statement).Select(m => m.Value).ToArray();
            var literals = words.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var environments = EnvironmentTerms(statement);
            var subjects = CandidateSubjects().Matches(statement).Select(m => m.Groups[1].Value)
                .Where(w => !ScopeNoise.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool otherSubject = _names.Count > 0 && subjects.Any(s => !_names.Contains(s));
            bool continuation = Continuation().IsMatch(statement) && !otherSubject;
            var effectiveNames = literals.Where(_names.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (continuation) effectiveNames.UnionWith(precedingNames);
            var effectiveEnvironments = environments.Count > 0 ? environments : continuation ? precedingEnvironments : [];
            precedingNames = otherSubject ? [] : effectiveNames;
            precedingEnvironments = effectiveEnvironments;
            if (otherSubject) continue;
            if (!_environments.IsSubsetOf(effectiveEnvironments) ||
                (_environments.Count > 0 && effectiveEnvironments.Except(_environments).Any())) continue;
            bool otherName = words.Any(w => char.IsUpper(w[0]) && w.Any(char.IsLower) &&
                !ScopeNoise.Contains(w) && !_names.Contains(w)) || subjects.Any(s => !_names.Contains(s));
            // A namespace may provide an omitted project label, but never override
            // another explicit name or supply an unstated environment.
            if (_names.Any(n => !effectiveNames.Contains(n) &&
                (!namespaceWords.Contains(n) || otherName || !ImplicitSubject().IsMatch(statement)))) continue;
            var terms = Terms(statement);
            if (RegionValue().IsMatch(statement)) terms.Add("region");
            if (CurrencyValue().IsMatch(statement)) terms.Add("currency");
            if (!_attributes.IsSubsetOf(terms)) continue;
            if (_owner && !terms.Contains("own")) continue;
            // A restore rehearsal is not a backup schedule; a drill's frequency is
            // not a recovery duration. Shared resource words alone cannot answer either.
            if (_backupSchedule && (!terms.Contains("backup") || !Schedule().IsMatch(statement))) continue;
            if (_recoveryDuration && (!terms.Contains("recover") || !Duration().IsMatch(statement) ||
                (!terms.Contains("objective") && !RecoverWithin().IsMatch(statement)))) continue;
            if (_focus.Count(terms.Contains) * 2 >= _focus.Count) return true;
        }
        return false;
    }

    private static HashSet<string> EnvironmentTerms(string text) => EnvironmentWords().Matches(text)
        .Where(m => !(m.Value.Equals("test", StringComparison.OrdinalIgnoreCase) || m.Value.Equals("stage", StringComparison.OrdinalIgnoreCase)) ||
            !VerbPrefix().IsMatch(text[..m.Index]))
        .Select(m => Environments[m.Value]).ToHashSet();

    private static HashSet<string> Terms(string text) => BM25Index.Tokenize(
        BackUp().Replace(RecoveryObjective().Replace(text, "recovery time objective"), "backup")).Select(t => t switch
    {
        "owner" or "own" or "approv" or "approve" or "approval" or "respons" or "responsible" or "responsibility" => "own",
        "deploy" or "deployment" or "releas" or "release" => "deploy",
        "health" or "readi" or "readiness" => "ready",
        "path" or "url" => "endpoint",
        "postgresql" or "database" => "databas",
        "snapshot" => "backup",
        "recovery" => "recover",
        "deadline" => "timeout",
        "retent" or "retention" or "retain" => "retain",
        "lifetim" or "lifetime" or "ttl" or "expir" or "expire" or "expiry" or "expiration" => "expiry",
        "encryption" or "encrypt" => "encrypt",
        "cach" or "cache" => "cache",
        "target" or "objective" => "objective",
        _ => t
    }).ToHashSet(StringComparer.Ordinal);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    [GeneratedRegex(@"^\s*(?:what|which|who|where|when|why|how)\b\s*", Options)]
    private static partial Regex Question();
    [GeneratedRegex(@"^\s*(?:please\s+)?(?:(?:can|could|would)\s+you\s+)?(?:tell|show)\s+me\s+", Options)]
    private static partial Regex RequestPrefix();
    [GeneratedRegex(@"^\s*at\s+what\s+time\s+", Options)]
    private static partial Regex AtWhatTime();
    [GeneratedRegex(@"\s+before\s+timing\s+out\b", Options)]
    private static partial Regex TimingOut();
    [GeneratedRegex(@"\bbacks?\s+up\b", Options)]
    private static partial Regex BackUp();
    [GeneratedRegex(@"\b(?:rto|recovery\s+time\s+objective)\b", Options)]
    private static partial Regex RecoveryObjective();
    [GeneratedRegex(@"^\s*how\s+(?:quickly|fast|long)\b", Options)]
    private static partial Regex QuickRecovery();
    [GeneratedRegex(@"\b(?:\d{1,2}:\d{2}|daily|nightly|hourly|weekly|monthly|every\s+(?:day|night|hour|week|month)|midnight|noon)\b", Options)]
    private static partial Regex Schedule();
    [GeneratedRegex(@"\b(?:\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:milliseconds?|seconds?|minutes?|hours?|days?)\b", Options)]
    private static partial Regex Duration();
    [GeneratedRegex(@"\brecover(?:s|ed)?\s+(?:in|within|under)\s+", Options)]
    private static partial Regex RecoverWithin();
    [GeneratedRegex(@"\b(?:USD|EUR|GBP|CAD|AUD|JPY|CHF|CNY|INR)\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CurrencyValue();
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Words();
    [GeneratedRegex(@"(?:^\s*(?:who\s+owns?|(?:what|where)\s+(?:is|are)|when\s+(?:do|does)|how\s+long\s+(?:is|are))\s+(?:the\s+)?|\b(?:for|of)\s+(?:the\s+)?)([\p{L}][\p{L}\p{N}_-]*)", Options)]
    private static partial Regex SubjectSlots();
    // Require a resource or predicate after the subject. An arbitrary word after an
    // auxiliary ("does changing", "can counts", "should approve") is not a name.
    [GeneratedRegex(@"\b(?:does|do|should|must|may)\s+(?:the\s+)?([\p{L}][\p{L}\p{N}_-]*)\s+([\p{L}]+)\b", Options)]
    private static partial Regex AuxiliarySubject();
    [GeneratedRegex(@"\b(?:after|before|now that|when|works?|avoids?)\b", Options)]
    private static partial Regex ContextTail();
    [GeneratedRegex(@"[;\r\n]+|[.!?](?:\s+|$)", Options)]
    private static partial Regex Statements();
    [GeneratedRegex(@"^\s*(?:set|get|use|namespace|context|memory|graph|recall)\b", Options)]
    private static partial Regex ImplicitSubject();
    [GeneratedRegex(@"^\s*(?:the|this|it|its|before|after|namespace|context)\b", Options)]
    private static partial Regex Continuation();
    [GeneratedRegex(@"(?:^\s*(?:the\s+)?|\b(?:for|of|to|about|from|with|set|use|get)\s+(?:the\s+)?)([\p{L}][\p{L}\p{N}_-]*)\s+(?:production|prod|staging|stage|development|dev|testing|test|qa|uat|gateway|database|backups?|deployments?|endpoint|readiness|rollback|recovery|security|payment)\b", Options)]
    private static partial Regex CandidateSubjects();
    [GeneratedRegex(@"\b(?:production|staging|development|testing|prod|stage|dev|test|qa|uat)\b", Options)]
    private static partial Regex EnvironmentWords();
    [GeneratedRegex(@"\b(?:to|i|we|you|can|should|must|will|please|how)\s+$", Options)]
    private static partial Regex VerbPrefix();
    [GeneratedRegex(@"\b[a-z]{2}(?:-[a-z0-9]+)+-\d+\b", Options)]
    private static partial Regex RegionValue();
}
