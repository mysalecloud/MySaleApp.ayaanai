using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

/// <summary>Conversation management. Every call is scoped to the current user and company.</summary>
public sealed class ConversationService
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly IUserContext _user;
    private readonly IConversationStateRepository? _states;

    public ConversationService(IConversationRepository conversations, IMessageRepository messages, IUserContext user,
        IConversationStateRepository? states = null)
    {
        _conversations = conversations;
        _messages = messages;
        _user = user;
        _states = states;
    }

    public async Task<List<ConversationSummaryDto>> ListAsync(string? search, bool? archived, CancellationToken ct)
    {
        var list = await _conversations.ListAsync(_user.UserId, _user.CompanyId, search?.Trim(), archived, 200, ct);
        return list.Select(MessageMapper.ToSummary).ToList();
    }

    public async Task<ConversationDetailDto> GetAsync(string id, CancellationToken ct)
    {
        var c = await Load(id, ct);
        var messages = await _messages.ListAsync(c.Id, ct);
        return new ConversationDetailDto(MessageMapper.ToSummary(c), messages.Select(MessageMapper.ToDto).ToList());
    }

    public async Task<ConversationSummaryDto> CreateAsync(string? title, CancellationToken ct)
    {
        var c = new Conversation
        {
            UserId = _user.UserId,
            CompanyId = _user.CompanyId,
            UserName = _user.DisplayName,
            DatabaseName = _user.DatabaseName,
            Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim()
        };
        await _conversations.InsertAsync(c, ct);
        return MessageMapper.ToSummary(c);
    }

    public async Task<ConversationSummaryDto> UpdateAsync(string id, UpdateConversationRequest request, CancellationToken ct)
    {
        var c = await Load(id, ct);
        if (!string.IsNullOrWhiteSpace(request.Title)) c.Title = request.Title.Trim();
        if (request.Archived.HasValue) c.Archived = request.Archived.Value;
        c.UpdatedAt = DateTime.UtcNow;
        await _conversations.UpdateAsync(c, ct);
        return MessageMapper.ToSummary(c);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var c = await Load(id, ct);
        await _messages.DeleteByConversationAsync(c.Id, ct);
        await _conversations.DeleteAsync(c.Id, ct);
        if (_states is not null) await _states.DeleteAsync(c.Id, _user.CompanyId, _user.UserId, ct);
    }

    public async Task ClearAsync(string id, CancellationToken ct)
    {
        var c = await Load(id, ct);
        await _messages.DeleteByConversationAsync(c.Id, ct);
        // Cleared history also drops the open clarification: a later "Value" is not merged into a request the user no longer sees.
        if (_states is not null) await _states.DeleteAsync(c.Id, _user.CompanyId, _user.UserId, ct);
        c.MessageCount = 0;
        c.UpdatedAt = DateTime.UtcNow;
        await _conversations.UpdateAsync(c, ct);
    }

    private async Task<Conversation> Load(string id, CancellationToken ct)
    {
        var c = await _conversations.GetAsync(id, _user.UserId, _user.CompanyId, ct);
        // A conversation created under another customer database is never shown (same user id in two databases).
        if (c is null || !BelongsToDatabase(c, _user.DatabaseName)) throw new NotFoundException("Conversation not found.");
        return c;
    }

    /// <summary>Older conversations have no database recorded; new ones must match the database of the token.</summary>
    public static bool BelongsToDatabase(Conversation c, string? databaseName)
        => c.DatabaseName is null || string.Equals(c.DatabaseName, databaseName, StringComparison.Ordinal);

    public static string TitleFrom(string question)
    {
        var t = question.Trim().Replace('\n', ' ');
        if (t.Length > 60) t = t[..57].TrimEnd() + "…";
        return t.Length == 0 ? "New conversation" : char.ToUpperInvariant(t[0]) + t[1..];
    }
}
