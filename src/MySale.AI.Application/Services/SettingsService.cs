using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

public sealed class SettingsService
{
    private readonly ISettingsRepository _repository;
    private readonly AuditService _audit;

    public SettingsService(ISettingsRepository repository, AuditService audit)
    {
        _repository = repository;
        _audit = audit;
    }

    public async Task<AppSettings> GetAsync(CancellationToken ct)
        => await _repository.GetAsync(ct) ?? new AppSettings();

    public async Task<SettingsDto> GetDtoAsync(CancellationToken ct)
    {
        var s = await GetAsync(ct);
        return new SettingsDto { Ai = s.Ai, Query = s.Query, Chat = s.Chat, Speech = s.Speech, Attachments = s.Attachments };
    }

    public async Task<SettingsDto> UpdateAsync(SettingsDto dto, CancellationToken ct)
    {
        var s = await GetAsync(ct);

        s.Ai = dto.Ai ?? new AiSettings();
        s.Ai.Temperature = Math.Clamp(s.Ai.Temperature, 0, 2);
        s.Ai.MaxTokens = Math.Clamp(s.Ai.MaxTokens, 64, 32768);
        s.Ai.TimeoutSeconds = Math.Clamp(s.Ai.TimeoutSeconds, 10, 600);
        s.Ai.MaxRepairAttempts = Math.Clamp(s.Ai.MaxRepairAttempts, 0, 3);
        if (string.IsNullOrWhiteSpace(s.Ai.DefaultProviderId)) s.Ai.DefaultProviderId = null;
        if (string.IsNullOrWhiteSpace(s.Ai.DefaultModel)) s.Ai.DefaultModel = null;

        s.Query = dto.Query ?? new QuerySettings();
        s.Query.MaxRecords = Math.Clamp(s.Query.MaxRecords, 1, 5000);
        s.Query.QueryTimeoutMs = Math.Clamp(s.Query.QueryTimeoutMs, 100, 60_000);
        s.Query.MaxPipelineStages = Math.Clamp(s.Query.MaxPipelineStages, 1, 30);
        s.Query.MaxRowsForAnswer = Math.Clamp(s.Query.MaxRowsForAnswer, 1, 500);
        s.Query.AllowedCollections = (s.Query.AllowedCollections ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        s.Chat = dto.Chat ?? new ChatSettings();
        s.Chat.HistoryMessages = Math.Clamp(s.Chat.HistoryMessages, 0, 20);

        if (dto.Speech is not null)
        {
            s.Speech = dto.Speech;
            s.Speech.MaxSeconds = Math.Clamp(s.Speech.MaxSeconds, 5, 600);
            s.Speech.Model = string.IsNullOrWhiteSpace(s.Speech.Model) ? "whisper-1" : s.Speech.Model.Trim();
            if (string.IsNullOrWhiteSpace(s.Speech.ProviderId)) s.Speech.ProviderId = null;
            s.Speech.DefaultLanguage = string.IsNullOrWhiteSpace(s.Speech.DefaultLanguage) ? null : s.Speech.DefaultLanguage.Trim();
        }
        if (dto.Attachments is not null)
        {
            s.Attachments = dto.Attachments;
            s.Attachments.MaxFileSizeMb = Math.Clamp(s.Attachments.MaxFileSizeMb, 1, 50);
            s.Attachments.MaxFilesPerMessage = Math.Clamp(s.Attachments.MaxFilesPerMessage, 1, 10);
            s.Attachments.RetentionDays = Math.Clamp(s.Attachments.RetentionDays, 1, 90);
            s.Attachments.MaxContextChars = Math.Clamp(s.Attachments.MaxContextChars, 2000, 60_000);
            if (string.IsNullOrWhiteSpace(s.Attachments.VisionProviderId)) s.Attachments.VisionProviderId = null;
            if (string.IsNullOrWhiteSpace(s.Attachments.VisionModel)) s.Attachments.VisionModel = null;
        }

        s.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAsync(s, ct);
        await _audit.LogAsync("SettingsUpdated", "Application settings updated", ct);
        return new SettingsDto { Ai = s.Ai, Query = s.Query, Chat = s.Chat, Speech = s.Speech, Attachments = s.Attachments };
    }
}

public sealed class AuditService
{
    private readonly IAuditLogRepository _repository;
    private readonly IUserContext _user;

    public AuditService(IAuditLogRepository repository, IUserContext user)
    {
        _repository = repository;
        _user = user;
    }

    public string? IpAddress { get; set; }

    public Task LogAsync(string action, string? details, CancellationToken ct)
        => _repository.InsertAsync(new AuditLog
        {
            Action = action,
            Details = details,
            UserId = _user.IsAuthenticated ? _user.UserId : null,
            UserName = _user.IsAuthenticated ? _user.UserName : null,
            CompanyId = _user.IsAuthenticated ? _user.CompanyId : null,
            IpAddress = IpAddress
        }, ct);

    public Task LogAsAsync(string action, string? details, AppUser? user, string? ip, CancellationToken ct)
        => _repository.InsertAsync(new AuditLog
        {
            Action = action,
            Details = details,
            UserId = user?.Id,
            UserName = user?.UserName,
            CompanyId = user?.CompanyId,
            IpAddress = ip
        }, ct);
}
