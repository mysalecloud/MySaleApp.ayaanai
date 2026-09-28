using Microsoft.Extensions.Logging;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Domain;

namespace MySale.AI.Application.Stores;

/// <summary>
/// Resolves the store context of a question on the server:
/// authenticated tenant → selected store (MySaleBooks selector, verified) → store scope for the query.
/// <list type="bullet">
/// <item>Operational questions use the selected store. Nothing is guessed: the store id comes from the MySaleBooks
/// selection and is verified against the tenant's store master; a missing / unknown / not-allowed store gives
/// <see cref="StoreMode.Missing"/> (the chat answers "please select a store"), never "all stores".</item>
/// <item>Trial Balance, Balance Sheet, Profit &amp; Loss: company-level (no store filter), unless the user explicitly asks
/// for the selected store's version.</item>
/// <item>"All stores" (explicit question or the "All" selector) only with verified unrestricted branch access;
/// otherwise the selected store is used and the answer says so.</item>
/// </list>
/// </summary>
public sealed class StoreContextResolver
{
    private readonly IUserContext _user;
    private readonly IStoreSelection _selection;
    private readonly IStoreAccessProvider _access;
    private readonly StoreFilterOptions _options;
    private readonly QueryEngine _engine;
    private readonly ILogger<StoreContextResolver>? _logger;

    public StoreContextResolver(IUserContext user, IStoreSelection selection, IStoreAccessProvider access, StoreFilterOptions options,
        QueryEngine engine, ILogger<StoreContextResolver>? logger = null)
    {
        _user = user;
        _selection = selection;
        _access = access;
        _options = options;
        _engine = engine;
        _logger = logger;
    }

    public async Task<StoreScope> ResolveAsync(string question, AppSettings settings, CancellationToken ct)
    {
        var selected = _selection.SelectedStoreId?.Trim();
        if (!_options.Enabled) return StoreScope.NoFilter("disabled");
        if (!_user.IsMySaleBooksUser && (!_options.ApplyToDashboardUsers || string.IsNullOrEmpty(selected)))
            return StoreScope.NoFilter("not-a-mysalebooks-user");

        var accounting = StoreQuestionPolicy.IsAccountingStatement(question);
        var explicitStore = StoreQuestionPolicy.RequestsSelectedStore(question);
        if (accounting && !explicitStore)
            return new StoreScope { Mode = StoreMode.None, Reason = "accounting-statement", AccountingStatement = true, Options = _options };

        var allSelected = !string.IsNullOrEmpty(selected) && _options.AllStoresValues.Contains(selected, StringComparer.OrdinalIgnoreCase);
        var wantsAll = allSelected || StoreQuestionPolicy.RequestsAllStores(question);
        var access = await SafeAccessAsync(ct);

        if (wantsAll && !explicitStore)
        {
            if ((access.Verified && access.Unrestricted) || (!access.Verified && _options.AllowAllStoresWithoutVerification))
                return new StoreScope { Mode = StoreMode.AllStores, Reason = access.Verified ? "all-stores-verified" : "all-stores-unverified", Options = _options };
            if (allSelected)
                return Missing("all-stores-not-permitted");
            // Asked for all stores in the question but not permitted: answer for the selected store and say so.
            var scoped = await SelectedAsync(selected, access, settings, ct);
            return scoped.Mode != StoreMode.Selected ? scoped : new StoreScope
            {
                Mode = StoreMode.Selected, StoreId = scoped.StoreId, StoreName = scoped.StoreName, Reason = "all-stores-not-permitted",
                Options = _options,
                Note = $"Showing results for {(scoped.StoreName is { Length: > 0 } n ? n : "the selected store")} only — company-wide reports are not available for your account."
            };
        }

        var result = await SelectedAsync(selected, access, settings, ct);
        return accounting ? new StoreScope
        {
            Mode = result.Mode, StoreId = result.StoreId, StoreName = result.StoreName, Reason = result.Reason + "+accounting-explicit",
            AccountingStatement = true, Options = _options
        } : result;
    }

    private async Task<StoreScope> SelectedAsync(string? selected, StoreAccess access, AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(selected) || _options.AllStoresValues.Contains(selected, StringComparer.OrdinalIgnoreCase))
            return Missing("no-store-selected");
        if (selected.Length > 64 || !selected.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return Missing("invalid-store-id");
        if (access.Verified && !access.Unrestricted && !access.AllowedStoreIds.Contains(selected))
            return Missing("store-not-allowed");

        var (verified, name) = await _engine.VerifyStoreAsync(selected, _options.StoreCollection, settings, ct);
        if (verified == false) return Missing("store-not-found");
        return new StoreScope { Mode = StoreMode.Selected, StoreId = selected, StoreName = name, Reason = verified == true ? "selected-verified" : "selected", Options = _options };
    }

    private async Task<StoreAccess> SafeAccessAsync(CancellationToken ct)
    {
        try { return await _access.GetAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogInformation("Store access could not be verified ({Error})", ex.GetType().Name);
            return StoreAccess.Unknown;
        }
    }

    private StoreScope Missing(string reason) => new() { Mode = StoreMode.Missing, Reason = reason, Options = _options };
}
