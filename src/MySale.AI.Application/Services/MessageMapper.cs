using System.Text.Json;
using System.Text.Json.Nodes;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

public static class MessageMapper
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static ConversationSummaryDto ToSummary(Conversation c)
        => new(c.Id, c.Title, c.Archived, c.MessageCount, c.LastProviderName, c.LastModel, c.CreatedAt, c.UpdatedAt);

    public static MessageDto ToDto(ChatMessage m)
    {
        var dto = new MessageDto
        {
            Id = m.Id,
            Role = m.Role,
            Content = m.Content,
            CreatedAt = m.CreatedAt,
            Status = m.Status,
            InputType = m.InputType,
            Attachments = m.Attachments,
            Voice = m.Voice
        };
        if (m.Role == MessageRole.User) return dto;

        dto.Provider = new ProviderRefDto(m.ProviderId, m.ProviderName, m.ProviderKind, m.Model);
        dto.Query = ToQueryInfo(m);
        dto.Data = ParseArray(m.DataJson);
        dto.Columns = m.Columns;
        dto.Visualization = ParseVisualization(m.VisualizationJson);
        dto.Usage = new UsageDto { InputTokens = m.InputTokens, OutputTokens = m.OutputTokens, EstimatedCost = m.EstimatedCost };
        dto.Timing = new TimingDto { TotalMs = m.ResponseTimeMs, AiQueryMs = m.AiQueryTimeMs, DbMs = m.ExecutionTimeMs, AiAnswerMs = m.AiAnswerTimeMs };
        dto.Grounding = new GroundingDto { Checked = m.GroundingChecked, Warnings = m.GroundingWarnings };
        dto.QueryLogId = m.QueryLogId;
        return dto;
    }

    public static QueryInfoDto ToQueryInfo(ChatMessage m) => new()
    {
        Generated = m.QueryGenerated,
        Validated = m.QueryValidated,
        Executed = m.QueryExecuted,
        Operation = m.Operation,
        Collection = m.Collection,
        Mql = m.Mql,
        Explanation = m.Explanation,
        ExecutionTimeMs = m.ExecutionTimeMs,
        ResultCount = m.ResultCount,
        Truncated = m.Truncated,
        ValidationErrors = m.ValidationErrors,
        RepairAttempts = m.RepairAttempts
    };

    public static ChatResponse ToChatResponse(Conversation conversation, ChatMessage user, ChatMessage assistant, DebugTrace? debug) => new()
    {
        ConversationId = conversation.Id,
        ConversationTitle = conversation.Title,
        UserMessageId = user.Id,
        MessageId = assistant.Id,
        Status = assistant.Status,
        Answer = assistant.Content,
        Data = ParseArray(assistant.DataJson),
        Columns = assistant.Columns,
        Visualization = ParseVisualization(assistant.VisualizationJson) ?? new VisualizationDto(),
        Query = ToQueryInfo(assistant),
        Provider = new ProviderRefDto(assistant.ProviderId, assistant.ProviderName, assistant.ProviderKind, assistant.Model),
        Usage = new UsageDto { InputTokens = assistant.InputTokens, OutputTokens = assistant.OutputTokens, EstimatedCost = assistant.EstimatedCost },
        Timing = new TimingDto { TotalMs = assistant.ResponseTimeMs, AiQueryMs = assistant.AiQueryTimeMs, DbMs = assistant.ExecutionTimeMs, AiAnswerMs = assistant.AiAnswerTimeMs },
        Grounding = new GroundingDto { Checked = assistant.GroundingChecked, Warnings = assistant.GroundingWarnings },
        QueryLogId = assistant.QueryLogId,
        Debug = debug,
        CreatedAt = assistant.CreatedAt
    };

    public static QueryLogSummaryDto ToLogSummary(QueryLog l) => new()
    {
        Id = l.Id,
        Question = l.Question,
        UserName = l.UserName,
        ProviderName = l.ProviderName,
        ProviderKind = l.ProviderKind,
        Model = l.Model,
        Operation = l.Operation,
        Collection = l.Collection,
        FinalMql = l.FinalMql,
        ValidationPassed = l.ValidationPassed,
        Blocked = l.Blocked,
        Executed = l.Executed,
        ExecutionSucceeded = l.ExecutionSucceeded,
        Status = l.Status,
        ExecutionTimeMs = l.ExecutionTimeMs,
        ResultCount = l.ResultCount,
        TotalTimeMs = l.TotalTimeMs,
        InputTokens = l.InputTokens,
        OutputTokens = l.OutputTokens,
        RepairAttempts = l.RepairAttempts,
        GroundingWarningCount = l.GroundingWarningCount,
        CreatedAt = l.CreatedAt
    };

    public static JsonArray? ParseArray(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json) as JsonArray; }
        catch (JsonException) { return null; }
    }

    public static VisualizationDto? ParseVisualization(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<VisualizationDto>(json, Web); }
        catch (JsonException) { return null; }
    }

    public static DebugTrace? ParseTrace(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<DebugTrace>(json, Web); }
        catch (JsonException) { return null; }
    }
}
