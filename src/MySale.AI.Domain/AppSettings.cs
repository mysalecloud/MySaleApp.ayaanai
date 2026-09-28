namespace MySale.AI.Domain;

/// <summary>Runtime-editable settings (Settings page). Stored as a single document.</summary>
public sealed class AppSettings
{
    public string Id { get; set; } = "global";
    public AiSettings Ai { get; set; } = new();
    public QuerySettings Query { get; set; } = new();
    public ChatSettings Chat { get; set; } = new();
    public SpeechSettings Speech { get; set; } = new();
    public AttachmentSettings Attachments { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AiSettings
{
    public string? DefaultProviderId { get; set; }
    public string? DefaultModel { get; set; }
    public double Temperature { get; set; } = 0.1;
    public int MaxTokens { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>How many times the model may fix a query that failed validation.</summary>
    public int MaxRepairAttempts { get; set; } = 1;
}

public sealed class QuerySettings
{
    public int MaxRecords { get; set; } = 200;
    public int QueryTimeoutMs { get; set; } = 5000;
    public int MaxPipelineStages { get; set; } = 12;
    /// <summary>Rows (max) sent to the model when generating the answer.</summary>
    public int MaxRowsForAnswer { get; set; } = 50;
    public List<string> AllowedCollections { get; set; } = new()
    {
        "Sales", "SaleItems", "Customers", "Items", "Branches", "Purchases", "PurchaseItems", "Payments"
    };
}

public sealed class ChatSettings
{
    public bool ShowMql { get; set; } = true;
    public bool ShowExecutionTime { get; set; } = true;
    public bool EnableStreaming { get; set; } = true;
    public bool SaveConversations { get; set; } = true;
    public bool DeveloperMode { get; set; } = true;
    /// <summary>Previous messages included as context for follow-up questions.</summary>
    public int HistoryMessages { get; set; } = 6;
    /// <summary>Minutes an unanswered clarification question stays open (then the customer is asked for the full request).</summary>
    public int ClarificationMinutes { get; set; } = 30;
    /// <summary>Follow-up questions for one request before AYAAN asks for the complete request in one message.</summary>
    public int MaxClarificationSteps { get; set; } = 4;
    /// <summary>Characters of earlier turns sent to the model (oldest turns are dropped first; the open request never is).</summary>
    public int HistoryCharBudget { get; set; } = 12000;
}

/// <summary>Speech-to-text. Uses an OpenAI-compatible /audio/transcriptions endpoint (OpenAI, or a local Whisper server).</summary>
public sealed class SpeechSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>AI provider whose base URL + API key are used (kind openai / openai-compatible).</summary>
    public string? ProviderId { get; set; }
    public string Model { get; set; } = "whisper-1";
    /// <summary>ISO-639-1 hint (ml, en, ar). Null = auto-detect.</summary>
    public string? DefaultLanguage { get; set; }
    public int MaxSeconds { get; set; } = 120;
}

public sealed class AttachmentSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxFileSizeMb { get; set; } = 20;
    public int MaxFilesPerMessage { get; set; } = 5;
    /// <summary>Uploaded files and extracted content are deleted after this many days.</summary>
    public int RetentionDays { get; set; } = 7;
    /// <summary>Vision-capable provider/model used for images when the chat model has no vision.</summary>
    public string? VisionProviderId { get; set; }
    public string? VisionModel { get; set; }
    /// <summary>Characters of attachment content sent to the model per question.</summary>
    public int MaxContextChars { get; set; } = 12_000;
}
