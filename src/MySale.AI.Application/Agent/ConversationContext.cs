using System.Text;
using System.Text.RegularExpressions;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>
/// Business intent of a request, detected deterministically from the customer's words (no model call). The intent
/// travels with the conversation state and with every open question, so a short reply ("age wise", "yes", "Main
/// Warehouse") always continues the request it belongs to — e.g. "Debtors report" → DEBTORS_REPORT → the
/// clarification "unpaid invoice age or ledger balance?" → "age wise" continues DEBTORS_REPORT.
/// </summary>
public static class ConversationIntents
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Most specific first: "debtors report" before "customers", "sales return" before "sales".
    private static readonly (string Intent, Regex Pattern)[] Rules =
    {
        ("DEBTORS_REPORT", new Regex(@"\bdebtors?\b|\breceivables?\b|\bowes?\s+(us|me)\b|\bcustomers?\s+(outstanding|dues?|ageing|aging)\b|\bsundry\s+debtors?\b", Opt)),
        ("CREDITORS_REPORT", new Regex(@"\bcreditors?\b|\bpayables?\b|\bwe\s+owe\b|\bsuppliers?\s+(outstanding|dues?|ageing|aging)\b|\bsundry\s+creditors?\b", Opt)),
        ("OUTSTANDING_REPORT", new Regex(@"\boutstanding\b|\boverdue\b|\bunpaid\b|\bpending\s+(bills?|invoices?|payments?)\b|\bageing\b|\baging\b", Opt)),
        ("CUSTOMER_BALANCE", new Regex(@"\bcustomers?\b.*\bbalances?\b|\bbalances?\b.*\bcustomers?\b", Opt)),
        ("SUPPLIER_BALANCE", new Regex(@"\b(suppliers?|vendors?)\b.*\bbalances?\b|\bbalances?\b.*\b(suppliers?|vendors?)\b", Opt)),
        ("SALES_RETURN", new Regex(@"\bsales?\s+returns?\b|\bcredit\s+notes?\b", Opt)),
        ("PURCHASE_RETURN", new Regex(@"\bpurchase\s+returns?\b|\bdebit\s+notes?\b", Opt)),
        ("SALES_ORDER", new Regex(@"\bsales?\s+orders?\b|\bquotations?\b|\bestimates?\b", Opt)),
        ("PURCHASE_ORDER", new Regex(@"\bpurchase\s+orders?\b|\bPO\b", RegexOptions.CultureInvariant)),
        ("SALES", new Regex(@"\bsales?\b|\bsold\b|\bselling\b|\brevenue\b|\bturnover\b|\binvoices?\b|വിൽപ(?:്പ)?ന|സെയിൽസ്", Opt)),
        ("PURCHASES", new Regex(@"\bpurchases?\b|\bpurchased\b|\bbought\b|വാങ്ങ|പർച്ചേസ്", Opt)),
        ("SERVICE_ITEMS", new Regex(@"\bservices?\s+(items?|charges?|list)\b|\bservice\b", Opt)),
        ("STOCK", new Regex(@"\bstocks?\b|\binventory\b|\bwarehouses?\b|\bgodowns?\b|\bon\s+hand\b|സ്റ്റോക്ക", Opt)),
        ("ITEMS", new Regex(@"\bitems?\b|\bproducts?\b|\bbarcodes?\b|\bsku\b", Opt)),
        ("CUSTOMERS", new Regex(@"\bcustomers?\b|\bclients?\b|\bbuyers?\b|കസ്റ്റമ|ഉപഭോക്ത", Opt)),
        ("SUPPLIERS", new Regex(@"\bsuppliers?\b|\bvendors?\b|സപ്ലയ|വിതരണക്കാ", Opt)),
        ("VOUCHERS", new Regex(@"\bvouchers?\b|\breceipts?\b|\bpayments?\b|\bjournals?\b|\bcontra\b", Opt)),
        ("LEDGER", new Regex(@"\bledgers?\b|\bstatement\b|ലെഡ്ജ", Opt)),
        ("EXPENSES", new Regex(@"\bexpenses?\b|\bexpenditure\b|\bspent\b|ചെലവ", Opt)),
        ("INCOME", new Regex(@"\bincomes?\b|വരുമാന", Opt)),
        ("PROFIT", new Regex(@"\bprofits?\b|\bloss\b|\bmargin\b|ലാഭ", Opt)),
        ("ACCOUNTS", new Regex(@"\baccounts?\b|\bcash\b|\bbanks?\b|\btrial\s+balance\b|\bbalance\s+sheet\b", Opt))
    };

    /// <summary>The first (most specific) intent the text mentions, or null.</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (intent, pattern) in Rules)
            if (pattern.IsMatch(text)) return intent;
        return null;
    }

    /// <summary>Human wording of an intent for customer-facing sentences ("the debtors report").</summary>
    public static string Describe(string? intent) => intent switch
    {
        "DEBTORS_REPORT" => "the debtors report",
        "CREDITORS_REPORT" => "the creditors report",
        "OUTSTANDING_REPORT" => "the outstanding report",
        "CUSTOMER_BALANCE" => "the customer balance",
        "SUPPLIER_BALANCE" => "the supplier balance",
        "SALES_RETURN" => "the sales returns",
        "PURCHASE_RETURN" => "the purchase returns",
        "SALES_ORDER" => "the sales orders",
        "PURCHASE_ORDER" => "the purchase orders",
        "SALES" => "the sales",
        "PURCHASES" => "the purchases",
        "SERVICE_ITEMS" => "the service items",
        "STOCK" => "the stock",
        "ITEMS" => "the items",
        "CUSTOMERS" => "the customers",
        "SUPPLIERS" => "the suppliers",
        "VOUCHERS" => "the vouchers",
        "LEDGER" => "the ledger",
        "EXPENSES" => "the expenses",
        "INCOME" => "the income",
        "PROFIT" => "the profit",
        "ACCOUNTS" => "the accounts",
        _ => "your request"
    };
}

/// <summary>
/// What kind of answer a clarification question expects, and which parameter it fills. A finite question gets
/// selectable options with structured values ("Unpaid invoice age" = UNPAID_INVOICE_AGE); a yes/no question gets Yes
/// / No; a period, warehouse, customer … question is open (the reply is the value).
/// </summary>
public static class ClarificationTypes
{
    public const string Choice = "choice";
    public const string YesNo = "yesNo";
    public const string Open = "open";
    public const string Date = "date";

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // "Do you want me to include inactive customers?" — a yes/no question: it starts with an auxiliary verb and does
    // not offer alternatives ("… A or B?" is a choice, not yes/no).
    private static readonly Regex YesNoLead = new(
        @"^\s*(?:(?:ok|okay|also|and|so)[,\s]+)?(do|does|did|should|shall|would|will|can|could|is|are|was|were|may|want\s+me\s+to|include)\b", Opt);
    private static readonly Regex Alternatives = new(@"\bor\b|/|\beither\b", Opt);

    private static readonly (string Parameter, Regex Pattern)[] ParameterRules =
    {
        ("reportType", new Regex(@"\b(age|ageing|aging|ledger\s+balance|unpaid\s+invoices?|bill[\s-]*wise|report\s+type|detailed|summary)\b", Opt)),
        ("measure", new Regex(@"\b(quantity|qty|value|amount|count)\b", Opt)),
        ("period", new Regex(@"\b(period|date|dates|month|week|year|when|from|between|start|end|range|day)\b", Opt)),
        ("warehouse", new Regex(@"\b(warehouses?|godowns?|locations?)\b", Opt)),
        ("branch", new Regex(@"\b(branch(es)?|stores?|outlets?)\b", Opt)),
        ("customer", new Regex(@"\b(customers?|clients?|debtors?)\b", Opt)),
        ("supplier", new Regex(@"\b(suppliers?|vendors?|creditors?)\b", Opt)),
        ("item", new Regex(@"\b(items?|products?|barcodes?|item\s+codes?)\b", Opt)),
        ("account", new Regex(@"\b(accounts?|ledgers?|bank|cash)\b", Opt)),
        ("voucherType", new Regex(@"\b(vouchers?|receipts?|payments?|journals?)\b", Opt))
    };

    /// <summary>Kind of question: date sources are dates; 2+ options = choice; an auxiliary-verb question without alternatives = yes/no.</summary>
    public static string KindOf(string question, IReadOnlyCollection<string> options, string source)
    {
        if (source is "ambiguousDate" or "invalidDate") return Date;
        if (options.Count >= 2 && !IsYesNoPair(options)) return Choice;
        if (IsYesNoPair(options)) return YesNo;
        var q = question.Trim();
        // Only the last sentence counts ("I found 3 customers. Do you want me to include inactive ones?").
        var last = Regex.Split(q, @"(?<=[.!])\s+").LastOrDefault(s => s.Contains('?')) ?? q;
        if (YesNoLead.IsMatch(last) && !Alternatives.IsMatch(last)) return YesNo;
        return Open;
    }

    public static bool IsYesNoPair(IReadOnlyCollection<string> options)
        => options.Count == 2
           && options.Any(o => Regex.IsMatch(o, @"^\s*yes\b", Opt))
           && options.Any(o => Regex.IsMatch(o, @"^\s*no\b", Opt));

    /// <summary>The parameter a question asks for (null = not recognised): options first, then the question words.</summary>
    public static string? ParameterOf(string question, IReadOnlyCollection<string> options, string kind, string? planArgument)
    {
        if (!string.IsNullOrWhiteSpace(planArgument)) return planArgument;
        if (kind == Date) return "period";
        if (kind == YesNo) return "confirm";
        var optionText = string.Join(' ', options);
        foreach (var (parameter, pattern) in ParameterRules)
            if (optionText.Length > 0 && pattern.IsMatch(optionText)) return parameter;
        foreach (var (parameter, pattern) in ParameterRules)
            if (pattern.IsMatch(question)) return parameter;
        return null;
    }

    /// <summary>
    /// Structured value of each option: the label in UPPER_SNAKE ("Unpaid invoice age" → UNPAID_INVOICE_AGE), or
    /// OPTION_n for record choices and labels that are not plain Latin text. Values are unique per question.
    /// </summary>
    public static List<string> ValuesFor(IReadOnlyList<string> options, bool records)
    {
        var values = new List<string>();
        for (var i = 0; i < options.Count; i++)
        {
            var value = records ? $"OPTION_{i + 1}" : Code(options[i]) ?? $"OPTION_{i + 1}";
            if (values.Contains(value, StringComparer.Ordinal)) value = $"OPTION_{i + 1}";
            values.Add(value);
        }
        return values;
    }

    /// <summary>"Unpaid invoice age" → UNPAID_INVOICE_AGE; null for text without Latin letters or longer than 40 characters.</summary>
    public static string? Code(string label)
    {
        var withoutNotes = Regex.Replace(label, @"\s*\([^)]*\)", string.Empty);
        var words = Regex.Matches(withoutNotes.ToUpperInvariant(), @"[A-Z0-9]+").Select(m => m.Value).ToList();
        if (words.Count == 0) return null;
        var code = string.Join('_', words);
        return code.Length is > 0 and <= 40 ? code : null;
    }
}

/// <summary>Clarification state machine (stored as ConversationState.Stage and shown in the trace).</summary>
public static class ConversationStages
{
    public const string NewRequest = "NEW_REQUEST";
    public const string IntentDetected = "INTENT_DETECTED";
    public const string ParametersChecked = "PARAMETERS_CHECKED";
    public const string ClarificationRequired = "CLARIFICATION_REQUIRED";
    public const string WaitingForUser = "WAITING_FOR_USER";
    public const string ClarificationResolved = "CLARIFICATION_RESOLVED";
    public const string ExecuteQuery = "EXECUTE_QUERY";
    public const string PresentResult = "PRESENT_RESULT";
}

/// <summary>How a reply relates to the open question after interpretation.</summary>
public enum ReplyOutcome
{
    /// <summary>The reply chose an option / answered yes or no / gave the value.</summary>
    Resolved,
    /// <summary>A finite question answered with "yes", "ok", "that one", "same" … — no option is chosen: ask again, never guess.</summary>
    NeedsChoice,
    /// <summary>The reply is related but does not match an option exactly — passed on as written next to the question.</summary>
    AsWritten
}

public sealed record ReplyInterpretation(ReplyOutcome Outcome, string? Resolved, string? Value);

/// <summary>Structured choice sent by the client when an option chip is clicked (never parsed from the label).</summary>
public sealed record StructuredChoice(string Value, string? DisplayText);

/// <summary>Interprets a reply to the open question (structured choice first, then the typed text).</summary>
public static class ReplyInterpreter
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Affirmations and deictic words that choose nothing on their own when a question offers several options.
    private static readonly Regex Affirmative = new(
        @"^\s*(yes|yeah|yep|yup|ya|ok|okay|k|sure|correct|right|fine|alright|all\s+right|please|continue|proceed|go\s+ahead|do\s+it|show\s+it|show\s+me|" +
        @"that'?s?\s+right|that\s+one|this\s+one|that|this|same|the\s+same|same\s+one|previous|previous\s+one|the\s+previous\s+one|as\s+before|" +
        @"അതെ|ശരി|ഉവ്വ്|ഓക്കെ|ആം|sheri|athe)\s*(please)?\s*[.!]*\s*$", Opt);

    private static readonly Regex Yes = new(
        @"^\s*(yes|yeah|yep|yup|ya|ok|okay|sure|correct|right|fine|alright|please|please\s+do|continue|proceed|go\s+ahead|do\s+it|show\s+it|show\s+me|include(\s+(them|it))?|" +
        @"yes\s+please|of\s+course|അതെ|ശരി|ഉവ്വ്|ഓക്കെ|ആം|sheri|athe)\s*(please)?\s*[.!]*\s*$", Opt);

    private static readonly Regex No = new(
        @"^\s*(no|nope|nah|not\s+needed|no\s+thanks|no\s+thank\s+you|don'?t|do\s+not|exclude(\s+(them|it))?|skip(\s+(them|it))?|without(\s+(them|it))?|" +
        @"അല്ല|ഇല്ല|venda|വേണ്ട)\s*(please)?\s*[.!]*\s*$", Opt);

    private static readonly Regex Neither = new(@"^\s*(no|neither|none|nope)\b", Opt);

    private static readonly Regex Resume = new(
        @"^\s*(continue|go\s+back|back|resume|previous(\s+(one|request|question))?|the\s+previous\s+(one|request|question)|(go\s+)?back\s+to\s+(that|it|the\s+previous(\s+(one|request|question))?|the\s+earlier\s+(one|request|question)))\s*(please)?\s*[.!]*\s*$", Opt);

    public static bool IsAffirmativeOnly(string reply) => Affirmative.IsMatch(reply);
    /// <summary>"continue", "go back", "back to that", "previous request" — resume a suspended request.</summary>
    public static bool IsResume(string reply) => Resume.IsMatch(reply);
    public static bool IsYes(string reply) => Yes.IsMatch(reply);
    public static bool IsNo(string reply) => No.IsMatch(reply);

    /// <summary>
    /// A reply that only makes sense in relation to the active request ("yes", "first one", "age wise", "that one",
    /// "continue", "last month"): short, no question form, no new subject.
    /// </summary>
    public static bool IsContextual(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        if (Affirmative.IsMatch(reply) || Yes.IsMatch(reply) || No.IsMatch(reply) || ClarificationFlow.IsBareReply(reply)) return true;
        return ClarificationFlow.WordCount(reply) <= 3 && !reply.TrimEnd().EndsWith('?');
    }

    private static readonly Regex SameAsBefore = new(@"^\s*(same|the\s+same|same\s+one|same\s+as\s+before|as\s+before|previous|previous\s+one|the\s+previous\s+one|last\s+one)\s*[.!]*\s*$", Opt);

    /// <param name="previousValue">Value of the same parameter in the previous request ("same" → that value).</param>
    public static ReplyInterpretation Interpret(string reply, PendingClarification pending, StructuredChoice? choice, string? previousValue = null)
    {
        // 1. A clicked option: its structured value decides (the visible label is never parsed).
        if (choice is { Value.Length: > 0 })
        {
            var index = pending.OptionValues.FindIndex(v => string.Equals(v, choice.Value, StringComparison.Ordinal));
            if (index >= 0 && index < pending.Options.Count)
                return new ReplyInterpretation(ReplyOutcome.Resolved, pending.Kind == ClarificationTypes.YesNo ? YesNoWord(choice.Value) : pending.Options[index], choice.Value);
        }

        var text = reply.Trim();
        switch (pending.Kind)
        {
            case ClarificationTypes.YesNo:
                if (Yes.IsMatch(text)) return new ReplyInterpretation(ReplyOutcome.Resolved, "yes", "YES");
                if (No.IsMatch(text)) return new ReplyInterpretation(ReplyOutcome.Resolved, "no", "NO");
                // "only active ones", "include them but not blocked ones": meaningful, passed on with the question.
                return new ReplyInterpretation(ReplyOutcome.AsWritten, ClarificationFlow.Resolve(text, pending), null);

            case ClarificationTypes.Choice:
            {
                var resolved = ClarificationFlow.Resolve(text, pending);
                var index = resolved is null ? -1 : pending.Options.FindIndex(o => string.Equals(o, resolved, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    return new ReplyInterpretation(ReplyOutcome.Resolved, pending.Options[index], index < pending.OptionValues.Count ? pending.OptionValues[index] : null);
                if (resolved is not null && resolved.Contains(" and ", StringComparison.Ordinal) && pending.Options.All(o => resolved.Contains(o, StringComparison.Ordinal)))
                    return new ReplyInterpretation(ReplyOutcome.Resolved, resolved, "ALL");
                // "same" / "previous" = the option chosen for the previous request, when there was one.
                if (previousValue is not null && SameAsBefore.IsMatch(text))
                {
                    var previous = pending.OptionValues.FindIndex(v => string.Equals(v, previousValue, StringComparison.Ordinal));
                    if (previous >= 0 && previous < pending.Options.Count)
                        return new ReplyInterpretation(ReplyOutcome.Resolved, pending.Options[previous], previousValue);
                }
                // "yes", "ok", "that one", "same", "previous", "no", "neither" choose nothing: ask again with the options.
                if (Affirmative.IsMatch(text) || No.IsMatch(text) || (Neither.IsMatch(text) && ClarificationFlow.WordCount(text) <= 2))
                    return new ReplyInterpretation(ReplyOutcome.NeedsChoice, null, null);
                // A short reply that matches no option ("monthly") is not guessed either. Record choices (items, ledgers)
                // accept a typed name or code instead: the report looks it up.
                if (pending.OptionIds.Count == 0 && ClarificationFlow.WordCount(text) <= 2 && !Regex.IsMatch(text, @"\d"))
                    return new ReplyInterpretation(ReplyOutcome.NeedsChoice, null, null);
                return new ReplyInterpretation(ReplyOutcome.AsWritten, resolved, null);
            }

            default:
            {
                // Open question (period, warehouse, customer …): "same" / "previous" = the value used for the previous
                // request; "yes" / "ok" / "that one" give no value → asked again.
                if (previousValue is not null && SameAsBefore.IsMatch(text))
                    return new ReplyInterpretation(ReplyOutcome.Resolved, previousValue, previousValue);
                if (Affirmative.IsMatch(text))
                    return new ReplyInterpretation(ReplyOutcome.NeedsChoice, null, null);
                var resolved = ClarificationFlow.Resolve(text, pending);
                return new ReplyInterpretation(ReplyOutcome.Resolved, resolved ?? text.TrimEnd('.', '!'), null);
            }
        }
    }

    private static string YesNoWord(string value) => value == "NO" ? "no" : "yes";

    /// <summary>"Please choose one: Unpaid invoice age or Ledger balance." (also for "yes" to a choice question).</summary>
    public static string ChooseAgain(PendingClarification pending)
    {
        if (pending.Kind == ClarificationTypes.Choice && pending.Options.Count >= 2)
            return "Please choose one: " + JoinOr(pending.Options) + ".";
        if (pending.Kind == ClarificationTypes.YesNo) return "Please answer Yes or No: " + pending.Question;
        return pending.Parameter switch
        {
            "period" => "Which period should I use? For example “this month”, “last month” or “1 September to 20 September”.",
            "warehouse" => "Which warehouse should I use? Please type its name.",
            "branch" => "Which branch or store should I use? Please type its name.",
            "customer" => "Which customer do you mean? Please type the customer's name.",
            "supplier" => "Which supplier do you mean? Please type the supplier's name.",
            "item" => "Which item do you mean? Please type its name, item code or barcode.",
            "account" => "Which account do you mean? Please type the ledger (account) name.",
            _ => pending.Question
        };
    }

    public static string JoinOr(IReadOnlyList<string> items)
        => items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            2 => items[0] + " or " + items[1],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1]
        };
}

/// <summary>Follow-up questions at the end of an answer ("… Do you want it by ledger balance instead?").</summary>
public static class AnswerFollowUps
{
    private static readonly Regex Offer = new(
        @"\b(do\s+you\s+want|would\s+you\s+like|shall\s+i|should\s+i|do\s+you\s+need|want\s+me\s+to|can\s+i\s+show|may\s+i)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The question the answer ends with (its last sentence, ending with "?"), or null.</summary>
    public static string? TrailingQuestion(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var text = answer.Trim();
        if (!text.EndsWith('?')) return null;
        // Last sentence / line only.
        var lastLine = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? text;
        var sentences = Regex.Split(lastLine, @"(?<=[.!?])\s+");
        var question = sentences.LastOrDefault(s => s.EndsWith('?'))?.Trim().TrimStart('-', '*', ' ');
        if (string.IsNullOrWhiteSpace(question) || question.Length > 300) return null;
        return Offer.IsMatch(question) || ClarificationFlow.OptionsFrom(question).Count >= 2 ? question : null;
    }
}

/// <summary>Compact, structured conversation context for the planner (instead of an unlimited transcript).</summary>
public static class ConversationStatePrompt
{
    public static string? Build(ConversationState state, string? intent, string stage, IReadOnlyDictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        if (intent is not null) sb.Append("Current intent: ").AppendLine(intent);
        sb.Append("Stage: ").AppendLine(stage);
        if (parameters.Count > 0)
            sb.Append("Collected parameters: ").AppendLine(string.Join("; ", parameters.Select(p => p.Key + " = " + p.Value)));
        if (state.Unresolved.Count > 0 && stage != "CLARIFICATION_RESOLVED")
            sb.Append("Unresolved parameters: ").AppendLine(string.Join(", ", state.Unresolved));
        if (state.Summary.Count > 0)
        {
            sb.AppendLine("Earlier requests in this conversation (oldest first):");
            foreach (var s in state.Summary) sb.Append("- ").AppendLine(s);
        }
        if (sb.Length == 0) return null;
        return "CONVERSATION STATE (kept by the server — authoritative; use it instead of guessing):\n" + sb.ToString().TrimEnd() +
               "\nWhen the question contains answers to your clarification questions, they belong to the current intent: continue that request with the collected parameters, never start over and never say there was no earlier question.";
    }
}
