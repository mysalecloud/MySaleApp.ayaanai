using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

/// <summary>
/// What a user may see of AYAAN's internals. The complete interaction (including the exact MQL) is always stored in the
/// activity log, but customer-facing responses never contain the query, pipeline, collection or database names,
/// prompts, traces or internal errors. Only AI-dashboard developers/admins (Admin/Tester accounts, not customer
/// MySaleBooks tokens unless Activity:AllowMySaleBooksAdmins) get technical details.
/// </summary>
public static class TechnicalDetailsPolicy
{
    public const string RefusalMessage = "I can explain the result, but I can't expose internal database queries or system details.";

    public static bool CanSeeTechnicalDetails(IUserContext user, bool allowMySaleBooksAdmins)
        => user.IsAuthenticated
           && (user.Role is UserRole.Admin or UserRole.Tester)
           && (!user.IsMySaleBooksUser || allowMySaleBooksAdmins);

    // "show me the query you used", "what MQL did you run", "give me the aggregation pipeline", "what is your system prompt",
    // "which database/collection names…", "connection string", "api key" (English, Malayalam, Arabic).
    private static readonly Regex AskVerb = new(
        @"\b(show|give|display|reveal|print|share|send|tell|list|expose|dump|provide|what|which|explain)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TechnicalObject = new(
        @"\b(mql|mongo ?(db)? ?(query|queries|command|commands|shell|pipeline)|(aggregation|query|mongo) pipelines?|pipelines? (you|u|used|behind)|(the |your |that |this )?(database |db |mongo |sql |generated |exact |internal )?quer(y|ies)( you| used| that| it| behind| run| ran| executed| generated)?|sql|system prompt|your (prompt|instructions)|hidden (prompt|instructions)|database names?|db names?|collection names?|connection strings?|api keys?|access tokens?|jwt|schema (details|structure)|internal (details|structure|configuration|tools?))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OtherLanguages = new(
        "(ക്വറി|ക്വെറി|mql|പൈപ്പ്ലൈൻ|സിസ്റ്റം പ്രോംപ്റ്റ്|استعلام|الاستعلام|موجه النظام|سلسلة الاتصال)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the user asks for internal technical details instead of business data.</summary>
    public static bool IsTechnicalDetailsRequest(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var q = question.Trim();
        if (OtherLanguages.IsMatch(q)) return true;
        var obj = TechnicalObject.Match(q);
        if (!obj.Success) return false;
        // "query" alone is also a business word ("customer queries"); require a request verb and a query-like context.
        var value = obj.Value.ToLowerInvariant();
        var strong = value.Contains("mql") || value.Contains("pipeline") || value.Contains("prompt") || value.Contains("instructions")
                     || value.Contains("connection") || value.Contains("api key") || value.Contains("token") || value.Contains("jwt")
                     || value.Contains("database name") || value.Contains("db name") || value.Contains("collection name")
                     || value.Contains("mongo") || value.Contains("sql") || value.Contains("schema") || value.Contains("internal");
        if (strong) return AskVerb.IsMatch(q) || q.EndsWith('?');
        // plain "query": only when it refers to the query the assistant used ("the query you used", "your query", "query behind")
        return AskVerb.IsMatch(q)
               && Regex.IsMatch(q, @"\b(quer(y|ies)\s+(you|u|it)\b|(your|the|that|this)\s+(exact\s+|generated\s+|internal\s+)?quer(y|ies)\s+(used|behind|run|ran|executed|generated)|quer(y|ies)\s+(you|u)\s+(used|ran|run|executed|generated)|quer(y|ies)\s+(did|do|does|was|were)\s+(you|u|it)\s+(use|used|run|ran|execute|executed|generate|generated))",
                   RegexOptions.IgnoreCase);
    }

    /// <summary>Customer-safe copy of a chat response: answer, data, visualization and status only.</summary>
    public static ChatResponse ForCustomer(ChatResponse r)
    {
        r.Query = ForCustomer(r.Query);
        r.Debug = null;
        r.Tenant = null;
        r.QueryLogId = null;
        r.Provider = new ProviderRefDto(null, r.Provider.Name, null, r.Provider.Model);
        return r;
    }

    public static QueryInfoDto ForCustomer(QueryInfoDto q) => new()
    {
        Generated = q.Generated,
        Validated = q.Validated,
        Executed = q.Executed,
        ExecutionTimeMs = q.ExecutionTimeMs,
        ResultCount = q.ResultCount,
        Truncated = q.Truncated
        // Operation, Collection, Mql, Explanation, ValidationErrors, RepairAttempts: internal only
    };

    public static MessageDto ForCustomer(MessageDto m)
    {
        if (m.Query is not null) m.Query = ForCustomer(m.Query);
        m.QueryLogId = null;
        if (m.Provider is not null) m.Provider = new ProviderRefDto(null, m.Provider.Name, null, m.Provider.Model);
        return m;
    }
}
