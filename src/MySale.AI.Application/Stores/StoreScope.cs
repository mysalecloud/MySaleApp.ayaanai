using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Application.Stores;

/// <summary>
/// Configuration section "StoreFilter": every operational report is limited to the store / branch / location selected
/// in MySaleBooks ("Store Location" selector, sent by the web app as the X-Store-Id header). Trial Balance, Balance
/// Sheet and Profit &amp; Loss are company-level accounting reports and are not restricted automatically.
/// </summary>
public sealed class StoreFilterOptions
{
    public const string Section = "StoreFilter";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Field that holds the store on transaction/master documents, in order of preference. A collection is store-scoped
    /// when it has one of these fields (exact name, case-insensitive). Only list fields that really mean "the store"
    /// (MySaleBooks: branchId). Do not add StoreId / LocationId unless they are the same concept in the data.
    /// </summary>
    public List<string> StoreFields { get; set; } = new() { "branchId" };

    /// <summary>Explicit per-collection store field (overrides <see cref="StoreFields"/>); value "" = never store-filtered.</summary>
    public Dictionary<string, string> CollectionFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collections never store-filtered (the store master itself, company/user/settings data).</summary>
    public List<string> ExemptCollections { get; set; } = new() { "Branches", "Branch", "Companies", "Company", "Users", "User" };

    /// <summary>Store master used to verify the selected store and read its name. Empty = the collection named Branch/Branches.</summary>
    public string? StoreCollection { get; set; }

    /// <summary>Values of the MySaleBooks selector that mean "All" stores.</summary>
    public List<string> AllStoresValues { get; set; } = new() { "0", "all" };

    /// <summary>
    /// When the user's branch access cannot be verified with MySaleBooks (permission API not configured / unavailable),
    /// allow "All stores" anyway. Default false: company-wide operational reports need verified unrestricted access.
    /// </summary>
    public bool AllowAllStoresWithoutVerification { get; set; }

    /// <summary>Also apply to AI-dashboard (non-MySaleBooks) users when they send a store. Default: MySaleBooks users only.</summary>
    public bool ApplyToDashboardUsers { get; set; }
}

public enum StoreMode
{
    /// <summary>No store filtering (feature off, accounting statement, or non-MySaleBooks user).</summary>
    None,
    /// <summary>Filter every store-scoped collection by the selected store.</summary>
    Selected,
    /// <summary>Verified company-wide request: no store filter.</summary>
    AllStores,
    /// <summary>A store is required but could not be determined / verified.</summary>
    Missing
}

/// <summary>Store context of one request, resolved on the server from the MySaleBooks selection.</summary>
public sealed class StoreScope
{
    public const string MissingMessage = "I couldn't determine the selected store. Please select a store and try again.";

    public StoreMode Mode { get; init; }
    public string? StoreId { get; init; }
    public string? StoreName { get; init; }
    /// <summary>Short machine reason, e.g. "selected", "accounting-statement", "all-stores-verified", "store-not-found".</summary>
    public string Reason { get; init; } = string.Empty;
    /// <summary>Shown before the answer when the user asked for all stores but only the selected store was used.</summary>
    public string? Note { get; init; }
    public bool AccountingStatement { get; init; }

    private readonly StoreFilterOptions _options = new();
    public StoreFilterOptions Options { get => _options; init => _options = value ?? new StoreFilterOptions(); }

    public static StoreScope NoFilter(string reason, bool accounting = false) => new() { Mode = StoreMode.None, Reason = reason, AccountingStatement = accounting };

    /// <summary>Store field of a collection, or null when the collection is not store-scoped.</summary>
    public string? FieldFor(CollectionSchema? collection)
    {
        if (collection is null) return null;
        if (_options.CollectionFields.TryGetValue(collection.Name, out var explicitField))
            return string.IsNullOrWhiteSpace(explicitField) ? null : collection.Fields.FirstOrDefault(f => string.Equals(f.Name, explicitField, StringComparison.OrdinalIgnoreCase))?.Name ?? explicitField;
        if (_options.ExemptCollections.Contains(collection.Name, StringComparer.OrdinalIgnoreCase)) return null;
        foreach (var candidate in _options.StoreFields)
            if (collection.Fields.FirstOrDefault(f => !f.Hidden && string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase)) is { } field)
                return field.Name;
        return null;
    }

    /// <summary>{field: value} for the selected store, typed like the field ({$oid} for ObjectId fields).</summary>
    public JsonObject Filter(string field, CollectionSchema? collection)
    {
        var id = StoreId ?? string.Empty;
        var type = collection?.Fields.FirstOrDefault(f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase))?.Type;
        var looksObjectId = Regex.IsMatch(id, "^[0-9a-fA-F]{24}$");
        JsonNode value = type switch
        {
            "objectId" when looksObjectId => new JsonObject { ["$oid"] = id.ToLowerInvariant() },
            "string" => JsonValue.Create(id),
            _ when looksObjectId => new JsonObject { ["$in"] = new JsonArray(new JsonObject { ["$oid"] = id.ToLowerInvariant() }, JsonValue.Create(id)) },
            _ => JsonValue.Create(id)
        };
        return new JsonObject { [field] = value };
    }
}

/// <summary>Deterministic reading of the question (the server decides, not the model).</summary>
public static class StoreQuestionPolicy
{
    // Trial Balance, Balance Sheet, Profit & Loss (English, Malayalam, Arabic).
    private static readonly Regex Accounting = new(
        @"\b(trial\s*balance|balance\s*sheet|profit\s*(and|&)\s*loss|p\s*&\s*l|p\s*and\s*l|income\s*statement|statement\s+of\s+(financial\s+position|profit))\b|ട്രയൽ\s*ബാലൻസ്|ബാലൻസ്\s*ഷീറ്റ്|ലാഭ\s*നഷ്ട|ميزان\s*المراجعة|الميزانية\s*العمومية|الأرباح\s*والخسائر|قائمة\s*الدخل",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Explicit company-wide / multi-store requests.
    private static readonly Regex AllStores = new(
        @"\b(all|every|each|other)\s+(stores?|branch(es)?|locations?|outlets?|shops?)\b|\b(company[\s-]*wide|whole\s+company|entire\s+company|all\s+company|across\s+(all\s+)?(stores|branches|locations))\b|\b(store|branch|location)[\s-]*wise\b|\bby\s+(store|branch|location)\b|\bcompare\s+(the\s+)?(stores|branches|locations)\b|\b(stores|branches)\s+comparison\b|എല്ലാ\s*(ബ്രാഞ്ച്|സ്റ്റോർ)|جميع\s*الفروع|كل\s*الفروع",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "for this store / selected branch": explicit store-specific version of an accounting report.
    private static readonly Regex ThisStore = new(
        @"\b(this|selected|current|my)\s+(store|branch|location|outlet)\b|\b(store|branch)[\s-]*(level|specific)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsAccountingStatement(string? question) => !string.IsNullOrWhiteSpace(question) && Accounting.IsMatch(question);
    public static bool RequestsAllStores(string? question) => !string.IsNullOrWhiteSpace(question) && AllStores.IsMatch(question);
    public static bool RequestsSelectedStore(string? question) => !string.IsNullOrWhiteSpace(question) && ThisStore.IsMatch(question);
}

/// <summary>The store selected in the client (MySaleBooks "Store Location"), read from the request. Not an authorization value.</summary>
public interface IStoreSelection
{
    string? SelectedStoreId { get; }
}

public sealed class NoStoreSelection : IStoreSelection
{
    public string? SelectedStoreId => null;
}

/// <summary>Which stores the user may use, as reported by MySaleBooks (user branch mappings).</summary>
public sealed class StoreAccess
{
    /// <summary>False = MySaleBooks could not be asked (not configured / unavailable).</summary>
    public bool Verified { get; init; }
    /// <summary>Verified and no branch restriction (MySaleBooks offers "All" to these users).</summary>
    public bool Unrestricted { get; init; }
    public IReadOnlySet<string> AllowedStoreIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static readonly StoreAccess Unknown = new() { Verified = false };
}

public interface IStoreAccessProvider
{
    Task<StoreAccess> GetAsync(CancellationToken ct);
}

public sealed class UnknownStoreAccessProvider : IStoreAccessProvider
{
    public Task<StoreAccess> GetAsync(CancellationToken ct) => Task.FromResult(StoreAccess.Unknown);
}
