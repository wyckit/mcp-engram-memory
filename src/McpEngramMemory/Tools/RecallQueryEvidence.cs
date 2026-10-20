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
    private static readonly HashSet<string> ProcedureWords = Terms("step steps sequence order");
    private static readonly HashSet<string> ProposalWords = Terms("proposal propose proposed suggestion suggest suggested recommendation recommend");
    private static readonly HashSet<string> ProcedureSyntax = new(
        "restore step steps procedure sequence order and".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ScopeNoise = new((
        "the a an current primary previous old production prod staging stage development dev testing test " +
        "database gateway payment timeout endpoint path port queue backup backups token lifetime cloud budget " +
        "daily monthly routine disaster recovery time objective readiness health security review reviews encryption algorithm " +
        "tools tool profile context block cache caching graph traversal duplicate memory memories ids namespace default child agents " +
        "deployment deployments release releases rollback command region snapshot snapshots incident " +
        "retention retained responsible responsibility for of to from with in at by its his her it this that " +
        "fields included period quickly wait take keep nightly night rto url http qa uat currency jitter deadline " +
        "what which who where when why how is are do does did must should can could would will may i we you our their " +
        "set get use uses return returns run runs restart archived after before since during " +
        "own owns approve approves cover covers " +
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
    private readonly bool _observedRecovery;
    private readonly bool _explicitValue;
    private readonly bool _proposalQuestion;
    private readonly bool _restoreProcedure;
    private readonly bool _rejectNonAnswers;
    private readonly bool _referenceQuestion;
    private readonly bool _scopeOnly;

    private RecallQueryEvidence(HashSet<string> names, HashSet<string> environments, HashSet<string> focus,
        bool owner, bool backupSchedule, bool recoveryDuration, bool rejectNonAnswers, bool referenceQuestion,
        bool scopeOnly = false, bool observedRecovery = false, bool restoreProcedure = false)
    {
        (_names, _environments, _focus, _owner) = (names, environments, focus, owner);
        (_backupSchedule, _recoveryDuration) = (backupSchedule, recoveryDuration);
        (_rejectNonAnswers, _referenceQuestion) = (rejectNonAnswers, referenceQuestion);
        _scopeOnly = scopeOnly;
        _observedRecovery = observedRecovery;
        _restoreProcedure = restoreProcedure;
        _explicitValue = focus.Contains("value") && !referenceQuestion;
        _proposalQuestion = focus.Overlaps(ProposalWords);
        // Shared project/resource words cannot substitute for a requested attribute.
        _attributes = focus.Intersect(Terms("budget password encryption lifetime retention timeout region endpoint jitter currency date rollback"))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static RecallQueryEvidence? Parse(string query)
        => Parse(query, scopeOnly: false);

    // Historical and broad retrieval need scope checks without factual-answer or
    // currentness requirements. Only explicit subject/resource patterns in a topic
    // query supply names; arbitrary title-cased topic words are not project names.
    public static RecallQueryEvidence? ParseScope(string query) => Parse(query, scopeOnly: true);

    private static RecallQueryEvidence? Parse(string query, bool scopeOnly)
    {
        if (query.Length > 1024) return null;
        query = NormalizeQuestion(query);
        if (scopeOnly) query = ScopePrefix().Replace(query, "");
        bool question = Question().IsMatch(query);
        if (!question && !scopeOnly) return null;
        bool referenceQuestion = ReferenceQuestion().IsMatch(query);
        var queryTerms = Terms(query);
        bool procedureRequest = !referenceQuestion && !queryTerms.Overlaps(ProposalWords) && ProcedureQuestion().IsMatch(query) &&
            queryTerms.Contains("restore") && queryTerms.Overlaps(ProcedureWords);
        var words = Words().Matches(query).Select(m => m.Value).ToArray();
        var names = words.Where(w => question && char.IsUpper(w[0]) && w.Any(char.IsLower) && !ScopeNoise.Contains(w) &&
                !(procedureRequest && ProcedureSyntax.Contains(w)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Also recognize lower-case subjects in common question forms.
        foreach (Match match in SubjectSlots().Matches(query).Concat(AuxiliarySubject().Matches(query)
            .Where(m => SubjectPredicates.Contains(m.Groups[2].Value)))
            .Concat(RestoreSubject().Matches(query).Cast<Match>())
            .Concat(scopeOnly ? HistoricalSubject().Matches(query).Where(m => SubjectPredicates.Contains(m.Groups[2].Value)) : [])
            .Concat(!question ? CandidateSubjects().Matches(query).Cast<Match>() : []))
        {
            string name = match.Groups[1].Value;
            if (!ScopeNoise.Contains(name)) names.Add(name);
        }
        var environments = EnvironmentTerms(query);
        if (scopeOnly)
            return names.Count == 0 && environments.Count == 0 ? null :
                new(names, environments, [], false, false, false, false, false, scopeOnly: true);
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
        bool observedRecovery = recoveryDuration && !RecoveryObjective().IsMatch(query) &&
            ObservedRecoveryQuestion().IsMatch(query);
        if (recoveryDuration) focus.ExceptWith(Terms("time objective"));
        bool restoreProcedure = procedureRequest &&
            focus.Contains("restore") && focus.Overlaps(ProcedureWords);
        // "Steps" and "order" request a procedure, not two additional facts.
        // Retain the operation and any resource qualifiers; evidence must also
        // contain ordered actions rather than just a duration or procedure label.
        if (restoreProcedure)
        {
            focus.ExceptWith(ProcedureWords);
            focus.ExceptWith(Terms("procedure"));
        }
        return focus.Count == 0 ? null : new(names, environments, focus,
            query.TrimStart().StartsWith("who ", StringComparison.OrdinalIgnoreCase) && focus.Contains("own"),
            query.TrimStart().StartsWith("when ", StringComparison.OrdinalIgnoreCase) && focus.Contains("backup"),
            recoveryDuration, FactualQuestion().IsMatch(query), referenceQuestion,
            observedRecovery: observedRecovery, restoreProcedure: restoreProcedure);
    }

    internal static string NormalizeQuestion(string query)
    {
        var request = RequestPrefix().Match(query);
        if (request.Success) query = query[request.Length..];
        // Keep the condition for scope extraction, but separate it from the requested
        // attribute. Dropping it would discard the only project/environment label.
        var conditional = ConditionalQuestion().Match(query);
        if (conditional.Success) query = conditional.Groups[2].Value + " when " + conditional.Groups[1].Value;
        query = LooksAfter().Replace(query, "owns");
        query = RollBack().Replace(query, "rollback");
        query = AtWhatTime().Replace(query, "When ");
        // A date request is still a factual question, including when the decision
        // happened in the past. Do not turn it into a general history request.
        query = OnWhatDate().Replace(query, "What date ");
        if (request.Success && !Question().IsMatch(query)) query = "What is " + query;
        // Preserve the requested timeout before stripping a contextual before/after tail.
        return TimingOut().Replace(query, " timeout");
    }

    public bool Matches(string? text, string ns)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384) return false;
        if (IsCompound && (!BroadRecallIntent.HasSourceEvidence(text) || !Facets.Any(f => f.Matches(text, ns))))
            return false;
        var namespaceWords = Words().Matches(ns.Replace('_', ' ')).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var precedingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var precedingEnvironments = new HashSet<string>();
        HashSet<string> precedingTerms = [];
        bool scopeMatched = false;
        var statements = Statements().Split(text);
        if (_scopeOnly && statements.Length > 32) return false;
        foreach (string statement in statements.Take(32))
        {
            var contextTerms = precedingTerms;
            precedingTerms = [];
            if (Question().IsMatch(NormalizeQuestion(statement))) continue;
            var words = Words().Matches(statement).Select(m => m.Value).ToArray();
            var literals = words.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var environments = EnvironmentTerms(statement);
            var subjects = CandidateSubjects().Matches(_scopeOnly ? ScopeStatusPrefix().Replace(statement, "") : statement)
                .Select(m => m.Groups[1].Value)
                .Where(w => !ScopeNoise.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool otherSubject = _names.Count > 0 && subjects.Any(s => !_names.Contains(s));
            if (_scopeOnly && (otherSubject ||
                (_environments.Count > 0 && environments.Except(_environments).Any()))) return false;
            bool continuation = Continuation().IsMatch(statement) && !otherSubject;
            var effectiveNames = literals.Where(_names.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (continuation) effectiveNames.UnionWith(precedingNames);
            var effectiveEnvironments = environments.Count > 0 ? environments : continuation ? precedingEnvironments : [];
            precedingNames = otherSubject ? [] : effectiveNames;
            precedingEnvironments = effectiveEnvironments;
            if (otherSubject) continue;
            // Broad/history context can omit an environment, but cannot contradict
            // one explicitly requested. Factual answers still must state it. A
            // comparison can include separate records for either requested environment.
            if ((!_scopeOnly && !_environments.IsSubsetOf(effectiveEnvironments)) ||
                (_environments.Count > 0 && effectiveEnvironments.Except(_environments).Any())) continue;
            // Context may leave its project implicit too (for example a linked
            // rollback explanation). Reject explicit contrary scopes above, without
            // turning broad/history retrieval into a factual-answer requirement.
            if (_scopeOnly) { scopeMatched = true; continue; }
            bool otherName = words.Any(w => char.IsUpper(w[0]) && w.Any(char.IsLower) &&
                !ScopeNoise.Contains(w) && !_names.Contains(w)) || subjects.Any(s => !_names.Contains(s));
            // A namespace may provide an omitted project label, but never override
            // another explicit name or supply an unstated environment.
            if (_names.Any(n => !effectiveNames.Contains(n) &&
                (!namespaceWords.Contains(n) || otherName || !ImplicitSubject().IsMatch(statement)))) continue;
            // Scope is checked against the ORIGINAL statement above. Removing an
            // unrelated non-answer clause must never remove a contrary project or
            // environment and turn its remaining value into an in-scope answer.
            string answerText = _rejectNonAnswers ? WithoutNonAnswerClauses(statement, _referenceQuestion, _proposalQuestion) : statement;
            if (CompoundFacet)
                answerText = string.Join(" ", EvidenceClauses(answerText).Where(BroadRecallIntent.HasSourceEvidence));
            var terms = Terms(answerText);
            precedingTerms = terms;
            if (_restoreProcedure && (!OrderedProcedure().IsMatch(answerText) ||
                !BroadRecallIntent.HasSourceEvidence(answerText) || !_focus.IsSubsetOf(terms))) continue;
            // Explicit date fields may use any representation. Recognize common
            // calendar literals too when the source omits the word "date".
            if (_attributes.Contains("date") && CalendarDate().IsMatch(answerText)) terms.Add("date");
            if (_explicitValue)
            {
                // A rotation/activity note is not a value. A generic continuation
                // can inherit its topic only after the original scope checks pass.
                if (!ValueBinding().Matches(answerText).Any(m =>
                    Terms(m.Groups[1].Value).Any(t => t == "value" || _focus.Contains(t)) &&
                    !ValueActivity().IsMatch(answerText[m.Groups[2].Index..]))) continue;
                terms = new(terms, StringComparer.Ordinal);
                terms.Add("value");
                if (ValueContinuation().IsMatch(answerText)) terms.UnionWith(contextTerms);
            }
            if (RegionValue().IsMatch(answerText)) terms.Add("region");
            if (CurrencyValue().IsMatch(answerText)) terms.Add("currency");
            if (!_attributes.IsSubsetOf(terms)) continue;
            if (_owner && !terms.Contains("own")) continue;
            // A restore rehearsal is not a backup schedule; a drill's frequency is
            // not a recovery duration. Shared resource words alone cannot answer either.
            if (_backupSchedule && (!terms.Contains("backup") ||
                (!Schedule().IsMatch(answerText) && !CadenceInterval().IsMatch(answerText)))) continue;
            if (_recoveryDuration && (!terms.Contains("recover") || !Duration().IsMatch(answerText) ||
                (_observedRecovery ? !ElapsedRecovery().IsMatch(answerText) :
                    !terms.Contains("objective") && !RecoverWithin().IsMatch(answerText)))) continue;
            if (_focus.Count(terms.Contains) * 2 >= _focus.Count) return true;
        }
        return scopeMatched;
    }

    internal static string WithoutNonAnswerClauses(string statement, bool referenceQuestion = false,
        bool allowProposals = false) => string.Join(" ",
        EvidenceClauses(statement).Where(clause =>
        {
            // Predicate-bound exclusions, not a blacklist of words such as
            // "unknown" anywhere in an otherwise useful explanation or value.
            if (MissingValue().IsMatch(clause) || DeliberatelyMissingValue().IsMatch(clause) ||
                DeferredValue().IsMatch(clause) || PendingRecording().IsMatch(clause) ||
                OmittedValue().IsMatch(clause) || OmissionContinuation().IsMatch(clause) || NoRecordedValue().IsMatch(clause) ||
                EmbeddedQuestion().IsMatch(clause)) return false;
            if (referenceQuestion) return true;
            if (QuestionAbout().IsMatch(clause) || (!allowProposals && ProposalPredicate().IsMatch(clause))) return false;
            if (ReferenceInstruction().IsMatch(clause)) return false;
            var reference = ReferencePredicate().Match(clause);
            // "is documented in the handbook as /ready" supplies a value;
            // "is documented in /runbooks/ready" only points to a document.
            return !reference.Success || ReferenceValue().IsMatch(clause[(reference.Index + reference.Length)..]);
        }));

    internal static IEnumerable<string> EvidenceClauses(string statement)
    {
        int start = 0, scanned = 0;
        bool hasPredicate = false;
        foreach (Match boundary in Clauses().Matches(statement))
        {
            // Scan each fragment once, even for a long subject list with no verb.
            string fragment = statement[scanned..boundary.Index];
            hasPredicate |= ClausePredicate().IsMatch(fragment) || MissingValue().IsMatch(fragment) ||
                OmissionContinuation().IsMatch(fragment);
            scanned = boundary.Index + boundary.Length;
            // Do not split a subject list ("endpoint path and port are documented")
            // into a dangling topic-only prefix that would pass the lexical guard.
            if (!hasPredicate) continue;
            yield return statement[start..boundary.Index];
            start = scanned;
            hasPredicate = false;
        }
        yield return statement[start..];
    }

    private static HashSet<string> EnvironmentTerms(string text) => EnvironmentWords().Matches(text)
        .Where(m => !(m.Value.Equals("test", StringComparison.OrdinalIgnoreCase) || m.Value.Equals("stage", StringComparison.OrdinalIgnoreCase)) ||
            !VerbPrefix().IsMatch(text[..m.Index]))
        .Select(m => Environments[m.Value]).ToHashSet();

    private static HashSet<string> Terms(string text) => BM25Index.Tokenize(
        RollBack().Replace(LooksAfter().Replace(BackUp().Replace(
            RecoveryObjective().Replace(text, "recovery time objective"), "backup"), "owns"), "rollback")).Select(t => t switch
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
    [GeneratedRegex(@"^\s*(?:(?:what|which|who|where|when)\b|how\s+(?:long|many|much|quickly|fast)\b)", Options)]
    private static partial Regex FactualQuestion();
    [GeneratedRegex(@"^\s*(?:where\b.*\b(?:documented|recorded|listed|specified)\s*[?.!]*\s*$|(?:which|what)\s+(?:document|handbook|runbook|checklist|reference)\b)", Options)]
    private static partial Regex ReferenceQuestion();
    [GeneratedRegex(@"^\s*(?:what|which)\b", Options)]
    private static partial Regex ProcedureQuestion();
    [GeneratedRegex(@"^\s*(?:what|which)\s+steps?\s+restore\s+(?:the\s+)?([\p{L}][\p{L}\p{N}_-]*)\b", Options)]
    private static partial Regex RestoreSubject();
    [GeneratedRegex(@",\s*then\s+\p{L}|\bfirst\s+\p{L}[^.!?;\r\n]*\b(?:then|next|finally)\s+\p{L}|\b1\)\s+\p{L}[^.!?;\r\n]*\b2\)\s+\p{L}|\b(?:only|single|one)\s+step\s*(?::|\bis\b)\s*\p{L}", Options)]
    private static partial Regex OrderedProcedure();
    [GeneratedRegex(@",\s+|\s+(?:and|but|however|whereas)\s+", Options)]
    private static partial Regex Clauses();
    [GeneratedRegex(@"\b(?:is|are|was|were|remains?|owns?|uses?|returns?|runs?|covers?|has|have|needs?|will|must)\s+\S+", Options)]
    private static partial Regex ClausePredicate();
    [GeneratedRegex(@"(?:\b(?:is|are|was|were|remains?|as)\s+(?:(?:currently|still)\s+)?|[:=]\s*)[<\[]?(?:TBD|TBC|unknown|undecided|unspecified|pending|not\s+(?:yet\s+)?(?:known|determined|decided|specified|documented|recorded|provided|available)|to\s+be\s+(?:determined|decided|confirmed|assigned))\b", Options)]
    private static partial Regex MissingValue();
    // Keep additional predicates separate: growing MissingValue exceeds the
    // non-backtracking engine's automaton limit on supported runtimes.
    [GeneratedRegex(@"(?:\b(?:is|are|was|were|remains?)\s+(?:(?:still|currently)\s+)?|[:=]\s*)[<\[]?(?:to\s+be\s+(?:specified|documented|recorded|provided|selected|named)|not\s+(?:yet\s+)?(?:selected|named))\b", Options)]
    private static partial Regex PendingRecording();
    [GeneratedRegex(@"\b(?:is|are|was|were)\s+(?:intentionally|deliberately)\s+not\s+(?:recorded|documented|provided|specified|available)\b", Options)]
    private static partial Regex DeliberatelyMissingValue();
    [GeneratedRegex(@"\b(?:does?|did)\s+not\s+(?:identify|name|specify|provide|record|state|include)\b", Options)]
    private static partial Regex OmittedValue();
    [GeneratedRegex(@"^\s*without\s+(?:naming|identifying|specifying|providing|recording)\b", Options)]
    private static partial Regex OmissionContinuation();
    [GeneratedRegex(@"\b(?:(?:will|must|needs?\s+to|has\s+to|have\s+to)\s+be|(?:has|have)\s+not\s+(?:yet\s+)?been)\s+(?:determined|decided|confirmed|assigned|specified|documented|recorded|provided|selected|named)\b", Options)]
    private static partial Regex DeferredValue();
    [GeneratedRegex(@"\bno\s+(?:[\p{L}]+\s+){1,5}(?:(?:has|have)\s+been|is|are|was|were)\s+(?:recorded|provided|selected|named|assigned|specified|decided)\b", Options)]
    private static partial Regex NoRecordedValue();
    [GeneratedRegex(@":\s*(?:what|which|who|where|when|why|how)\b", Options)]
    private static partial Regex EmbeddedQuestion();
    [GeneratedRegex(@"\b(?:asks?|asking|questions?)\s+(?:about|whether|what|which|who|where|when|why|how)\b", Options)]
    private static partial Regex QuestionAbout();
    [GeneratedRegex(@"\b(?:proposes?|proposed|suggests?|suggested|recommends?|recommended)\s+\S+|\b(?:proposal|suggestion|recommendation)\s*(?:\b(?:is|are|was|were)\b|[:=])", Options)]
    private static partial Regex ProposalPredicate();
    [GeneratedRegex(@"(?:^\s*|[:=]\s*)(?:see|consult|check|refer\s+to)\s+", Options)]
    private static partial Regex ReferenceInstruction();
    [GeneratedRegex(@"\b(?:is|are|was|were|remains?)\s+(?:only\s+)?(?:documented|recorded|listed|specified|described|detailed)\s+(?:in|at|under)\b", Options)]
    private static partial Regex ReferencePredicate();
    [GeneratedRegex(@"\bas\s+\S+", Options)]
    private static partial Regex ReferenceValue();
    [GeneratedRegex(@"^\s*(?:what|which|who|where|when|why|how)\b\s*", Options)]
    private static partial Regex Question();
    [GeneratedRegex(@"^\s*(?:please\s+)?(?:(?:can|could|would)\s+you\s+)?(?:tell|show)\s+me\s+", Options)]
    private static partial Regex RequestPrefix();
    [GeneratedRegex(@"^\s*(?:if|when)\s+([^,\r\n]+),\s*((?:what|which|who|where|when|why|how)\b[^\r\n]*)$", Options)]
    private static partial Regex ConditionalQuestion();
    [GeneratedRegex(@"\blooks?\s+after\b", Options)]
    private static partial Regex LooksAfter();
    [GeneratedRegex(@"\brolls?\s+back\b", Options)]
    private static partial Regex RollBack();
    [GeneratedRegex(@"\b([\p{L}][\p{L}\p{N}_-]*)(?:\s+itself)?\s*(?:\b(?:is|are|equals?)\s+|[:=]\s*)(\S+)", Options)]
    private static partial Regex ValueBinding();
    [GeneratedRegex(@"^(?:rotated|changed|updated|reviewed|checked)\s+(?:every|daily|weekly|monthly|regularly|during|after|before|by|on|at)\b", Options)]
    private static partial Regex ValueActivity();
    [GeneratedRegex(@"^\s*(?:the|its|this)\s+value(?:\s+itself)?\s*(?:\b(?:is|equals?)\s+|[:=])", Options)]
    private static partial Regex ValueContinuation();
    [GeneratedRegex(@"^\s*(?:(?:previous|historical|old|compare)\s+|(?:history|overview|summary|comparison)\s+(?:of|for)\s+)", Options)]
    private static partial Regex ScopePrefix();
    // Historical labels describe the record, not its subject. Inspect the subject
    // after stacked labels too, so archive retrieval cannot conceal a contrary scope.
    [GeneratedRegex(@"^\s*(?:\[?(?:superseded|obsolete|archived|historical|previous|former|old|original)\b\]?\s*:?\s*)+", Options)]
    private static partial Regex ScopeStatusPrefix();
    [GeneratedRegex(@"^\s*at\s+what\s+time\s+", Options)]
    private static partial Regex AtWhatTime();
    [GeneratedRegex(@"^\s*on\s+what\s+date\s+", Options)]
    private static partial Regex OnWhatDate();
    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2})\b", Options)]
    private static partial Regex CalendarDate();
    [GeneratedRegex(@"\s+before\s+timing\s+out\b", Options)]
    private static partial Regex TimingOut();
    [GeneratedRegex(@"\bbacks?\s+up\b", Options)]
    private static partial Regex BackUp();
    [GeneratedRegex(@"\b(?:rto|recovery\s+time\s+objective)\b", Options)]
    private static partial Regex RecoveryObjective();
    [GeneratedRegex(@"^\s*how\s+(?:quickly|fast|long)\b", Options)]
    private static partial Regex QuickRecovery();
    [GeneratedRegex(@"\b(?:did|took|take|taken|rehearsal|drill)\b", Options)]
    private static partial Regex ObservedRecoveryQuestion();
    [GeneratedRegex(@"\b(?:(?:recovered|restored)\s+(?:in|within|under)|(?:recovery|rehearsal|drill)\s+(?:took|lasted)|(?:completed|finished)\s+(?:\w+\s+){0,6}(?:recovery|rehearsal|drill)\s+(?:in|within|under))\s+", Options)]
    private static partial Regex ElapsedRecovery();
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
    [GeneratedRegex(@"(?:^\s*(?:who\s+owns?|(?:what|where)\s+(?:is|are)|what\s+date\s+(?:is|are|was|were)|(?:which|what)\s+command\s+rollback|when\s+(?:do|does)|how\s+long\s+(?:is|are))\s+(?:the\s+)?|\b(?:for|of)\s+(?:the\s+)?)([\p{L}][\p{L}\p{N}_-]*)", Options)]
    private static partial Regex SubjectSlots();
    [GeneratedRegex(@"\b(?:was|were)\s+(?:the\s+)?([\p{L}][\p{L}\p{N}_-]*)\s+([\p{L}]+)\b", Options)]
    private static partial Regex HistoricalSubject();
    // Require a resource or predicate after the subject. An arbitrary word after an
    // auxiliary ("does changing", "can counts", "should approve") is not a name.
    [GeneratedRegex(@"\b(?:does|do|did|should|must|may|when)\s+(?:the\s+)?([\p{L}][\p{L}\p{N}_-]*)\s+([\p{L}]+)\b", Options)]
    private static partial Regex AuxiliarySubject();
    [GeneratedRegex(@"\b(?:after|before|now that|when|works?|avoids?)\b", Options)]
    private static partial Regex ContextTail();
    [GeneratedRegex(@"[;\r\n]+|[.!?](?:\s+|$)", Options)]
    private static partial Regex Statements();
    [GeneratedRegex(@"^\s*(?:set|get|use|namespace|context|memory|graph|recall)\b", Options)]
    private static partial Regex ImplicitSubject();
    [GeneratedRegex(@"^\s*(?:the|this|it|its|before|after|namespace|context)\b", Options)]
    private static partial Regex Continuation();
    [GeneratedRegex(@"(?:^\s*(?:the\s+)?|\b(?:for|of|to|about|from|with|set|use|get|restore|and|but|however|whereas)\s+(?:the\s+)?)([\p{L}][\p{L}\p{N}_-]*)\s+(?:production|prod|staging|stage|development|dev|testing|test|qa|uat|gateway|database|backups?|deployments?|endpoint|readiness|rollback|recovery|security|payment)\b", Options)]
    private static partial Regex CandidateSubjects();
    [GeneratedRegex(@"\b(?:production|staging|development|testing|prod|stage|dev|test|qa|uat)\b", Options)]
    private static partial Regex EnvironmentWords();
    [GeneratedRegex(@"\b(?:to|i|we|you|can|should|must|will|please|how)\s+$", Options)]
    private static partial Regex VerbPrefix();
    [GeneratedRegex(@"\b[a-z]{2}(?:-[a-z0-9]+)+-\d+\b", Options)]
    private static partial Regex RegionValue();
}
