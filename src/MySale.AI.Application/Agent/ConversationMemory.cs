using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>How a message relates to the open clarification question.</summary>
public enum ReplyKind
{
    /// <summary>Answers (or corrects the answer to) the open question → merged into the original request.</summary>
    Answer,
    /// <summary>"Cancel", "never mind", "വേണ്ട" → the open request is dropped.</summary>
    Cancel,
    /// <summary>A different question ("Show my sales" while AYAAN asked about stock) → the open request is dropped.</summary>
    NewTopic
}

/// <summary>The request planned for this turn after merging the answers of the open clarification.</summary>
public sealed record MergedRequest(string Analysis, string Prompt);

/// <summary>
/// Deterministic part of the clarification flow: which options a question offers, whether a reply answers it,
/// cancels it or starts a new topic, what a short reply means ("Value" → "stock value", "ഇന്നലെ" → "yesterday",
/// "Both" → every option) and how the answers are merged into the original request. No model call, no guessing:
/// when a reply cannot be matched to an option it is passed on as written, next to the question it answers.
/// </summary>
public static class ClarificationFlow
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Words = new(@"[\p{L}\p{M}\p{N}]+(?:['’][\p{L}]+)?", RegexOptions.Compiled);

    // Canonical meaning ← English and Malayalam words. The canonical word is what the model and the matcher see.
    private static readonly (string Canonical, string[] Words)[] Synonyms =
    {
        ("value", new[] { "value", "values", "amount", "amounts", "worth", "valuation", "price", "cost", "വില", "മൂല്യം", "തുക", "വാല്യൂ", "വാല്യു" }),
        ("quantity", new[] { "quantity", "quantities", "qty", "count", "units", "unit", "number", "pieces", "pcs", "nos", "എണ്ണം", "അളവ്", "ക്വാണ്ടിറ്റി" }),
        ("customer", new[] { "customer", "customers", "debtor", "debtors", "കസ്റ്റമർ", "കസ്റ്റമേഴ്സ്", "ഉപഭോക്താവ്", "ഉപഭോക്താക്കൾ" }),
        ("supplier", new[] { "supplier", "suppliers", "vendor", "vendors", "creditor", "creditors", "സപ്ലയർ", "സപ്ലയേഴ്സ്", "വിതരണക്കാർ" }),
        ("yes", new[] { "yes", "yeah", "yep", "yup", "ok", "okay", "sure", "correct", "right", "please", "അതെ", "ശരി", "ഉവ്വ്", "ആം", "ഓക്കെ" }),
        ("no", new[] { "no", "nope", "അല്ല", "ഇല്ല" }),
        ("both", new[] { "both", "all", "everything", "രണ്ടും", "എല്ലാം" }),
        ("today", new[] { "today", "ഇന്ന്", "ഇന്നത്തെ" }),
        ("yesterday", new[] { "yesterday", "ഇന്നലെ", "ഇന്നലത്തെ" }),
        ("first", new[] { "first", "1st", "former", "ആദ്യത്തേത്", "ആദ്യത്തെ", "ഒന്നാമത്തെ" }),
        ("second", new[] { "second", "2nd", "latter", "രണ്ടാമത്തേത്", "രണ്ടാമത്തെ" }),
        ("third", new[] { "third", "3rd", "മൂന്നാമത്തെ" })
    };

    // Malayalam / mixed period phrases → English (the date anchors and the planner understand the English words).
    private static readonly (Regex Pattern, string English)[] Phrases =
    {
        (new Regex(@"ഈ\s*മാസ(?:ം|ത്തെ)", Opt), "this month"),
        (new Regex(@"കഴിഞ്ഞ\s*മാസ(?:ം|ത്തെ)", Opt), "last month"),
        (new Regex(@"ഈ\s*ആഴ്ച(?:യിലെ)?", Opt), "this week"),
        (new Regex(@"കഴിഞ്ഞ\s*ആഴ്ച(?:യിലെ)?", Opt), "last week"),
        (new Regex(@"ഈ\s*വർഷ(?:ം|ത്തെ)", Opt), "this year"),
        (new Regex(@"കഴിഞ്ഞ\s*വർഷ(?:ം|ത്തെ)", Opt), "last year"),
        (new Regex(@"(?:രണ്ടും|both)\s+(?:വേണം|venam|please)", Opt), "both")
    };

    private static readonly Dictionary<string, string> Canonical = BuildCanonical();

    private static Dictionary<string, string> BuildCanonical()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, words) in Synonyms)
            foreach (var w in words) map.TryAdd(w, canonical);
        return map;
    }

    /// <summary>Filler words that carry no meaning for matching a reply to an option.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "i", "want", "wanted", "need", "meant", "mean", "show", "me", "my", "please", "only", "just", "in", "of",
        "for", "it", "that", "this", "one", "is", "was", "actually", "sorry", "give", "see", "like", "would", "to", "and", "by",
        "മതി", "mathi", "വേണം", "venam", "കാണിക്കൂ", "കാണിക്കുക", "തരൂ", "ആണ്", "aanu", "ok"
    };

    // Topic words: a reply that brings a subject the open request never mentioned is a new question.
    private static readonly (string Topic, Regex Pattern)[] Topics =
    {
        ("sales", new Regex(@"\b(sales?|sold|selling|invoices?|revenue|turnover)\b|വിൽപ(?:്പ)?ന|സെയിൽസ്", Opt)),
        ("purchase", new Regex(@"\b(purchases?|purchased|bought|buying)\b|വാങ്ങ|പർച്ചേസ്", Opt)),
        ("stock", new Regex(@"\b(stocks?|inventory)\b|സ്റ്റോക്ക", Opt)),
        ("customer", new Regex(@"\b(customers?|debtors?|receivables?)\b|കസ്റ്റമ|ഉപഭോക്ത", Opt)),
        ("supplier", new Regex(@"\b(suppliers?|vendors?|creditors?|payables?)\b|സപ്ലയ|വിതരണക്കാ", Opt)),
        ("expense", new Regex(@"\b(expenses?|expenditure|spending|spent)\b|ചെലവ", Opt)),
        ("income", new Regex(@"\bincomes?\b|വരുമാന", Opt)),
        ("cash", new Regex(@"\bcash\b|ക്യാഷ്", Opt)),
        ("bank", new Regex(@"\bbanks?\b|ബാങ്ക്", Opt)),
        ("profit", new Regex(@"\b(profits?|loss|margin)\b|ലാഭ", Opt)),
        ("payment", new Regex(@"\b(payments?|receipts?|collections?)\b", Opt)),
        ("ledger", new Regex(@"\b(ledgers?|statement)\b|ലെഡ്ജ", Opt)),
        ("voucher", new Regex(@"\bvouchers?\b", Opt)),
        ("order", new Regex(@"\borders?\b", Opt))
    };

    private static readonly Regex RequestLead = new(
        @"^\s*(show|list|what|what's|whats|how|give|display|get|find|tell|compare|which|who|when|where|total|top|എത്ര|കാണിക്കുക|കാണിക്കൂ|പറയൂ|പറയുക|തരൂ|ലിസ്റ്റ്|ഏത്|ആര്)\b" +
        @"|\?\s*$|(എത്ര|കാണിക്കുക|കാണിക്കൂ|പറയൂ|തരൂ)\s*[?.!]?\s*$", Opt);

    private static readonly Regex CancelWords = new(
        @"^\s*(please\s+)?(cancel(\s+(it|that|this|the\s+request|the\s+question))?|stop|never\s*mind|nevermind|forget\s+(it|that|about\s+it)|leave\s+it|skip(\s+it)?|no\s+thanks|abort|don'?t\s+bother|" +
        @"വേണ്ട|റദ്ദാക്കുക|റദ്ദാക്കൂ|ഒഴിവാക്കൂ|വിട്ടേക്കൂ|വിട്ടേക്ക്|cancel\s+cheyyu|venda)\s*(please)?\s*[.!]*\s*$", Opt);

    private static readonly Regex Parenthetical = new(@"\s*\([^)]*\)", RegexOptions.Compiled);

    // --------------------------------------------------------------------------------------------- options

    /// <summary>
    /// Choices offered by a question: "Do you want stock quantity or stock value?" → [stock quantity, stock value];
    /// "Which ledger do you mean: A, B, C?" → [A, B, C]. Open questions return no options.
    /// </summary>
    public static List<string> OptionsFrom(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return new List<string>();
        var q = question.Trim();
        string? list = null;
        var colon = Regex.Match(q, @":\s*(?<list>[^:?]+?)\s*\??\s*$", Opt);
        if (colon.Success) list = colon.Groups["list"].Value;
        else
        {
            var ask = Regex.Match(q,
                @"\b(?:do\s+you\s+(?:want|mean|need)|did\s+you\s+mean|would\s+you\s+like|should\s+i\s+(?:show|use)|is\s+it|are\s+you\s+asking\s+(?:about|for))\s+(?:to\s+see\s+)?(?<list>[^?]+?)\s*\?",
                Opt);
            if (ask.Success) list = ask.Groups["list"].Value;
        }
        if (list is null) return new List<string>();
        list = Parenthetical.Replace(list, string.Empty);
        list = Regex.Replace(list, @"\s+for\s+""[^""]*""\s*$", string.Empty, Opt);
        var parts = Regex.Split(list, @"\s*,\s*(?:or\s+)?|\s+or\s+", Opt)
            .Select(p => Regex.Replace(p.Trim().Trim('"', '“', '”', '.'), @"^(the|a|an)\s+", string.Empty, Opt).Trim())
            .Where(p => p.Length is > 0 and <= 60)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count is >= 2 and <= 6 ? parts : new List<string>();
    }

    /// <summary>Options supplied with the question (by the model or the server), cleaned; falls back to <see cref="OptionsFrom"/>.</summary>
    public static List<string> CleanOptions(IEnumerable<string?>? options, string question, Func<string, bool>? isSafe = null)
    {
        var list = (options ?? Array.Empty<string?>())
            .Select(o => o?.Trim())
            .Where(o => !string.IsNullOrEmpty(o) && o!.Length <= 60 && o.IndexOfAny(new[] { '{', '}', '$', '[', ']' }) < 0 && (isSafe?.Invoke(o) ?? true))
            .Select(o => o!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        return list.Count >= 2 ? list : OptionsFrom(question);
    }

    // --------------------------------------------------------------------------------------------- classification

    public static bool IsCancel(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.Length <= 60 && CancelWords.IsMatch(text) && TopicsOf(text).Count == 0;

    /// <summary>Does <paramref name="reply"/> answer the open question, cancel it, or start a new topic?</summary>
    public static ReplyKind Classify(string reply, PendingClarification pending)
    {
        if (IsCancel(reply)) return ReplyKind.Cancel;
        var context = string.Join(' ', new[] { pending.OriginalQuestion, pending.Question, pending.Missing ?? string.Empty }
            .Concat(pending.Options).Concat(pending.Answers.Select(a => a.Reply + " " + a.Resolved)));
        var newTopics = TopicsOf(reply).Except(TopicsOf(context)).ToList();
        var words = WordCount(reply);
        // A subject the open request never mentioned ("Show my sales" while AYAAN asked about stock) is a new question.
        // Short open answers without a question form ("Bank" to "which account?") stay answers.
        if (newTopics.Count > 0 && (RequestLead.IsMatch(reply) || words >= 3 || pending.Options.Count > 0))
            return ReplyKind.NewTopic;
        return ReplyKind.Answer;
    }

    /// <summary>
    /// A message that only makes sense as the answer to a question ("Value", "Yes", "Both", "Yesterday", "ഇന്നലെ").
    /// Without an open question or earlier turns it is not guessed: the customer is asked for the full request.
    /// </summary>
    public static bool IsBareReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        var normalized = NormalizePhrases(text);
        if (Regex.IsMatch(normalized, @"^\s*(this|last)\s+(month|week|year)\s*[.!?]?\s*$", Opt)) return true;
        var tokens = Tokens(normalized).ToList();
        if (tokens.Count is 0 or > 3) return false;
        var bare = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "yes", "no", "both", "value", "quantity", "today", "yesterday", "first", "second", "third", "total" };
        var content = tokens.Where(t => !Filler.Contains(t.Raw) || Canon(t.Raw) is "yes").ToList();
        return content.Count > 0 && content.All(t => bare.Contains(Canon(t.Raw)));
    }

    // --------------------------------------------------------------------------------------------- resolution

    /// <summary>
    /// Meaning of a short reply for the open question: the matched option ("Value" → "stock value", "the first one" →
    /// option 1), every option for "Both", or a normalised English phrase ("ഇന്നലെ" → "yesterday"). Null = use as written.
    /// </summary>
    public static string? Resolve(string reply, PendingClarification pending)
    {
        var normalized = NormalizePhrases(reply);
        var tokens = Tokens(normalized).ToList();
        if (tokens.Count == 0) return null;
        var canon = tokens.Select(t => Canon(t.Raw)).ToList();
        var options = pending.Options;

        if (options.Count > 0)
        {
            // Exact option (ignoring case, punctuation and explanations in brackets).
            var exact = options.FirstOrDefault(o => Same(o, reply) || Same(o, normalized));
            if (exact is not null) return exact;

            if (canon.Count <= 3 && canon.Contains("both") && !canon.Contains("no"))
                return string.Join(" and ", options);

            if (canon.Count <= 3)
            {
                var ordinal = canon.Contains("first") ? 0 : canon.Contains("second") ? 1 : canon.Contains("third") ? 2 : -1;
                if (ordinal >= 0 && ordinal < options.Count) return options[ordinal];
            }

            var content = tokens.Where(t => !Filler.Contains(t.Raw) && Canon(t.Raw) is not ("yes" or "no")).Select(t => Stem(Canon(t.Raw))).ToList();
            if (content.Count > 0)
            {
                var scored = options
                    .Select(o => (Option: o, Hits: content.Count(c => OptionStems(o).Contains(c))))
                    .Where(x => x.Hits > 0)
                    .OrderByDescending(x => x.Hits)
                    .ToList();
                if (scored.Count == 1 || (scored.Count > 1 && scored[0].Hits > scored[1].Hits)) return scored[0].Option;
            }

            if (canon.Count == 1 && canon[0] == "yes" && options.Count == 1) return options[0];
        }

        // No options (or no match): normalised meaning of yes/no, periods and Malayalam words.
        if (canon.All(c => c is "yes")) return "yes";
        if (canon.All(c => c is "no")) return "no";
        // Only a translation (Malayalam word, period phrase) is a new meaning; English replies are passed on as written.
        var translated = tokens.Any(t => IsNonLatin(t.Raw) && Canonical.ContainsKey(t.Raw))
                         || !string.Equals(normalized, reply.Trim(), StringComparison.Ordinal);
        if (!translated) return null;
        return string.Join(' ', tokens.Select(t => IsNonLatin(t.Raw) && Canonical.TryGetValue(t.Raw, out var c) ? c : t.Raw));
    }

    /// <summary>Date clarification: the chosen date for the ambiguous / invalid text, or null when the reply does not settle it.</summary>
    public static string? ResolveDate(string reply, PendingClarification pending, DateOnly today, string dateOrder)
    {
        var option = pending.Options.Count > 0 ? Resolve(reply, pending) : null;
        if (option is not null && pending.Options.Contains(option, StringComparer.OrdinalIgnoreCase)) return option;
        var parsed = QuestionDates.Parse(reply, today, dateOrder);
        if (parsed.Invalid.Count > 0 || parsed.Ambiguous.Count > 0 || !parsed.Spans.Any()) return null;
        return reply.Trim().TrimEnd('.', '!', '?');
    }

    // --------------------------------------------------------------------------------------------- merging

    /// <summary>
    /// The request for this turn: the original request plus every answer. <c>Analysis</c> is plain text for the
    /// deterministic steps and the answer ("Show my stock — stock value"); <c>Prompt</c> tells the planner which
    /// question each answer belongs to.
    /// </summary>
    public static MergedRequest Merge(string original, IReadOnlyList<ClarificationAnswer> answers)
    {
        if (answers.Count == 0) return new MergedRequest(original, original);
        var analysis = original.Trim().TrimEnd('?', '.', '!', ' ') + " — " + string.Join("; ", answers.Select(a => a.Resolved ?? a.Reply));
        var sb = new StringBuilder(original.Trim());
        sb.AppendLine().AppendLine();
        sb.AppendLine("Answers the user gave to your clarification questions for THIS request. Combine them with the request above into one complete request; a later answer replaces an earlier one; never ask again for something answered here:");
        foreach (var a in answers)
        {
            sb.Append("- You asked: \"").Append(a.Question).Append("\" → the user answered: \"").Append(a.Reply).Append('"');
            if (!string.IsNullOrWhiteSpace(a.Resolved) && !string.Equals(a.Resolved, a.Reply, StringComparison.OrdinalIgnoreCase))
                sb.Append(" (meaning: ").Append(a.Resolved).Append(')');
            sb.AppendLine();
        }
        return new MergedRequest(analysis, sb.ToString().TrimEnd());
    }

    /// <summary>Replaces the ambiguous / invalid date text of the original request with the chosen date.</summary>
    public static string ReplaceDate(string original, string? replaceText, string chosen)
    {
        if (string.IsNullOrEmpty(replaceText)) return original.TrimEnd() + " (" + chosen + ")";
        var index = original.IndexOf(replaceText, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? original.TrimEnd() + " (" + chosen + ")" : original[..index] + chosen + original[(index + replaceText.Length)..];
    }

    /// <summary>
    /// Applies the chosen date to the request: the ambiguous / invalid text is replaced where it was written (original
    /// request or an earlier answer); when it cannot be found the date is appended to the request.
    /// </summary>
    public static (string Original, List<ClarificationAnswer> Answers) ApplyDate(string original, IReadOnlyList<ClarificationAnswer> answers,
        string? replaceText, string chosen)
    {
        var found = false;
        string Swap(string text)
        {
            if (string.IsNullOrEmpty(replaceText) || text.IndexOf(replaceText, StringComparison.OrdinalIgnoreCase) < 0) return text;
            found = true;
            return ReplaceDate(text, replaceText, chosen);
        }
        var newOriginal = Swap(original);
        var newAnswers = answers.Select(a => new ClarificationAnswer
        {
            Question = a.Question,
            Reply = Swap(a.Reply),
            Resolved = a.Resolved is null ? null : Swap(a.Resolved),
            At = a.At
        }).ToList();
        if (!found) newOriginal = ReplaceDate(original, null, chosen);
        return (newOriginal, newAnswers);
    }

    public static PendingClarification Copy(PendingClarification p) => new()
    {
        OriginalQuestion = p.OriginalQuestion,
        Question = p.Question,
        Options = p.Options.ToList(),
        OptionIds = p.OptionIds.ToList(),
        Plan = p.Plan,
        PlanArgument = p.PlanArgument,
        Missing = p.Missing,
        Source = p.Source,
        ReplaceText = p.ReplaceText,
        Answers = p.Answers.Select(a => new ClarificationAnswer { Question = a.Question, Reply = a.Reply, Resolved = a.Resolved, At = a.At }).ToList(),
        Step = p.Step,
        StoreId = p.StoreId,
        StoreName = p.StoreName,
        AssistantMessageId = p.AssistantMessageId,
        CreatedAt = p.CreatedAt,
        AskedAt = p.AskedAt
    };

    /// <summary>Short quote of a customer text for a reply ("Show my stock").</summary>
    public static string Short(string? text, int max = 80)
    {
        var t = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    public static bool IsExpired(PendingClarification pending, DateTime now, int minutes)
        => minutes > 0 && now - pending.AskedAt > TimeSpan.FromMinutes(minutes);

    // --------------------------------------------------------------------------------------------- helpers

    public static IReadOnlyCollection<string> TopicsOf(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : Topics.Where(t => t.Pattern.IsMatch(text)).Select(t => t.Topic).ToHashSet();

    public static int WordCount(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Words.Matches(text).Count;

    private static string NormalizePhrases(string text)
    {
        var s = text.Trim();
        foreach (var (pattern, english) in Phrases) s = pattern.Replace(s, english);
        return s;
    }

    private readonly record struct Token(string Raw);

    private static IEnumerable<Token> Tokens(string text)
        => Words.Matches(text).Select(m => new Token(m.Value.ToLowerInvariant()));

    private static string Canon(string word) => Canonical.TryGetValue(word, out var c) ? c : word;

    private static string Stem(string word)
        => word.Length > 3 && IsLatin(word) && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word;

    private static HashSet<string> OptionStems(string option)
        => Tokens(Parenthetical.Replace(option, string.Empty)).Select(t => Stem(Canon(t.Raw))).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Same(string a, string b)
    {
        static string N(string s) => Regex.Replace(Parenthetical.Replace(s, string.Empty).ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}]+", " ").Trim();
        return N(a) == N(b) && N(a).Length > 0;
    }

    private static bool IsLatin(string s) => s.All(c => c < 0x250);
    private static bool IsNonLatin(string s) => !IsLatin(s);
}

/// <summary>
/// In-process state store: used by the tests and as a fallback when no persistent store is registered (state then
/// lives only as long as the orchestrator instance). Production registers the MongoDB repository.
/// </summary>
public sealed class InMemoryConversationStateRepository : IConversationStateRepository
{
    private readonly ConcurrentDictionary<string, ConversationState> _states = new();
    private readonly object _lock = new();

    public Task<(TurnStart Result, ConversationState? State)> TryBeginTurnAsync(string conversationId, string companyId, string userId,
        string? databaseName, string turnId, DateTime now, TimeSpan staleAfter, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(conversationId, out var state))
            {
                state = new ConversationState { Id = conversationId, CompanyId = companyId, UserId = userId, DatabaseName = databaseName, CreatedAt = now };
                _states[conversationId] = state;
            }
            if (state.CompanyId != companyId || state.UserId != userId || !string.Equals(state.DatabaseName, databaseName, StringComparison.Ordinal))
                return Task.FromResult<(TurnStart, ConversationState?)>((TurnStart.NotOwner, null));
            if (state.ActiveTurnId is not null && state.ActiveTurnStartedAt is { } at && now - at < staleAfter)
                return Task.FromResult<(TurnStart, ConversationState?)>((TurnStart.Busy, null));
            state.ActiveTurnId = turnId;
            state.ActiveTurnStartedAt = now;
            return Task.FromResult<(TurnStart, ConversationState?)>((TurnStart.Started, Copy(state)));
        }
    }

    public Task<bool> SaveAsync(ConversationState state, string turnId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(state.Id, out var current) || current.ActiveTurnId != turnId) return Task.FromResult(false);
            var saved = Copy(state);
            saved.ActiveTurnId = null;
            saved.ActiveTurnStartedAt = null;
            saved.Version = current.Version + 1;
            _states[state.Id] = saved;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(string conversationId, string turnId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(conversationId, out var s) && s.ActiveTurnId == turnId)
            {
                s.ActiveTurnId = null;
                s.ActiveTurnStartedAt = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string conversationId, string companyId, string userId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(conversationId, out var s) && s.CompanyId == companyId && s.UserId == userId)
                _states.TryRemove(conversationId, out _);
        }
        return Task.CompletedTask;
    }

    /// <summary>Test helper: changes the stored state (e.g. makes an open question older).</summary>
    public void Mutate(string conversationId, Action<ConversationState> change)
    {
        lock (_lock) if (_states.TryGetValue(conversationId, out var s)) change(s);
    }

    /// <summary>Test helper: current stored state (a copy).</summary>
    public ConversationState? Peek(string conversationId)
    {
        lock (_lock) return _states.TryGetValue(conversationId, out var s) ? Copy(s) : null;
    }

    private static ConversationState Copy(ConversationState s)
        => System.Text.Json.JsonSerializer.Deserialize<ConversationState>(System.Text.Json.JsonSerializer.Serialize(s))!;
}
