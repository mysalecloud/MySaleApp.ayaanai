using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Stores;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>
/// Company settings of the signed-in MySaleBooks company, read from its own database: base currency (code, symbol,
/// decimal places), financial year and costing method. Same resolution as the MySaleBooks reports:
/// Currency = Currency[_id = Company.currencyId]. Never guessed: when something is missing, <see cref="Missing"/> says what.
/// </summary>
public sealed class CompanyContext
{
    public string? CompanyId { get; init; }
    public string? CurrencyId { get; init; }
    public string? CurrencyCode { get; init; }
    public string? CurrencySymbol { get; init; }
    public string? CurrencyName { get; init; }
    public int? Decimals { get; init; }
    public DateTime? FinancialYearFrom { get; init; }
    public DateTime? FinancialYearTo { get; init; }
    public string? CostingType { get; init; }
    /// <summary>What could not be verified (e.g. "Company.currencyId is empty"). Null = base currency verified.</summary>
    public string? Missing { get; init; }

    public bool CurrencyResolved => Missing is null && !string.IsNullOrWhiteSpace(CurrencyCode);

    public string Describe()
        => (CurrencyResolved ? $"Base currency {CurrencyCode} ({Decimals ?? 2} decimals{(string.IsNullOrWhiteSpace(CurrencySymbol) ? "" : ", symbol " + CurrencySymbol)})" : "Base currency not verified: " + Missing)
           + (FinancialYearFrom is { } f ? $" · financial year from {f:yyyy-MM-dd}" + (FinancialYearTo is { } t ? $" to {t:yyyy-MM-dd}" : "") : "")
           + (string.IsNullOrWhiteSpace(CostingType) ? "" : $" · costing {CostingType}");
}

public sealed class CompanyContextService
{
    private static readonly ConcurrentDictionary<string, (DateTime At, CompanyContext Value)> Cache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly Regex ObjectIdText = new("^[0-9a-fA-F]{24}$", RegexOptions.Compiled);

    private readonly QueryEngine _engine;
    private readonly IUserContext _user;

    public CompanyContextService(QueryEngine engine, IUserContext user)
    {
        _engine = engine;
        _user = user;
    }

    /// <summary>
    /// Company context for MySaleBooks users (null for other users or databases without a Company collection).
    /// The company is the one of the selected store (Branch.companyId), else the only company of the database.
    /// </summary>
    public async Task<CompanyContext?> ResolveAsync(IReadOnlyList<CollectionSchema> schema, StoreScope? store, AppSettings settings, CancellationToken ct)
    {
        if (!_user.IsMySaleBooksUser || !schema.Any(c => c.Name == "Company")) return null;
        var storeId = store is { Mode: StoreMode.Selected } ? store.StoreId : null;
        var key = $"{_user.DatabaseName}|{_user.CompanyId}|{storeId}";
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Value;

        CompanyContext result;
        try
        {
            result = await LoadAsync(schema, storeId, settings, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not cached: a temporary failure must not hide the currency for 5 minutes.
            return new CompanyContext { Missing = "the company settings could not be read" };
        }
        Cache[key] = (DateTime.UtcNow, result);
        return result;
    }

    private async Task<CompanyContext> LoadAsync(IReadOnlyList<CollectionSchema> schema, string? storeId, AppSettings settings, CancellationToken ct)
    {
        string? companyId = null;
        if (storeId is not null && ObjectIdText.IsMatch(storeId) && schema.Any(c => c.Name == "Branch"))
        {
            var branch = await _engine.ReadMasterAsync("Branch", new JsonArray
            {
                new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$oid"] = storeId.ToLowerInvariant() } } },
                new JsonObject { ["$project"] = new JsonObject { ["companyId"] = 1 } }
            }, schema, settings, 1, ct);
            companyId = Text(branch.FirstOrDefault()?["companyId"]);
        }

        var match = companyId is not null && ObjectIdText.IsMatch(companyId)
            ? new JsonObject { ["_id"] = new JsonObject { ["$oid"] = companyId.ToLowerInvariant() } }
            : new JsonObject();
        var companies = await _engine.ReadMasterAsync("Company", new JsonArray
        {
            new JsonObject { ["$match"] = match },
            new JsonObject { ["$project"] = new JsonObject { ["currencyId"] = 1, ["financialYearFrom"] = 1, ["financialYearTo"] = 1, ["costingType"] = 1 } },
            new JsonObject { ["$limit"] = 2 }
        }, schema, settings, 2, ct);

        if (companies.Count == 0) return new CompanyContext { Missing = "no company record was found" };
        if (companies.Count > 1)
            return new CompanyContext { Missing = "the database has several companies and the company of this request could not be determined (select a store)" };

        var company = companies[0];
        var ctx = new CompanyContext
        {
            CompanyId = Text(company["_id"]),
            FinancialYearFrom = Date(company["financialYearFrom"]),
            FinancialYearTo = Date(company["financialYearTo"]),
            CostingType = Text(company["costingType"]) ?? "FIFO"
        };
        var currencyId = Text(company["currencyId"]);
        if (currencyId is null) return With(ctx, missing: "Company.currencyId is empty");
        if (!ObjectIdText.IsMatch(currencyId) || !schema.Any(c => c.Name == "Currency"))
            return With(ctx, currencyId, missing: "the company currency record is not available");

        var currency = (await _engine.ReadMasterAsync("Currency", new JsonArray
        {
            new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$oid"] = currencyId.ToLowerInvariant() } } },
            new JsonObject { ["$project"] = new JsonObject { ["currencyCode"] = 1, ["symbol"] = 1, ["currencyName"] = 1, ["decimals"] = 1 } }
        }, schema, settings, 1, ct)).FirstOrDefault();
        if (currency is null) return With(ctx, currencyId, missing: "Company.currencyId does not match a Currency record");
        var code = Text(currency["currencyCode"]);
        if (code is null) return With(ctx, currencyId, missing: "the company currency has no currency code");

        return new CompanyContext
        {
            CompanyId = ctx.CompanyId,
            CurrencyId = currencyId,
            CurrencyCode = code.Trim().ToUpperInvariant(),
            CurrencySymbol = Text(currency["symbol"]),
            CurrencyName = Text(currency["currencyName"]),
            Decimals = Int(currency["decimals"]) is { } d && d is >= 0 and <= 6 ? d : null,
            FinancialYearFrom = ctx.FinancialYearFrom,
            FinancialYearTo = ctx.FinancialYearTo,
            CostingType = ctx.CostingType
        };
    }

    private static CompanyContext With(CompanyContext c, string? currencyId = null, string? missing = null) => new()
    {
        CompanyId = c.CompanyId,
        CurrencyId = currencyId,
        FinancialYearFrom = c.FinancialYearFrom,
        FinancialYearTo = c.FinancialYearTo,
        CostingType = c.CostingType,
        Missing = missing
    };

    private static string? Text(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && s != "0" ? s.Trim() : null;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<decimal>(out var m)) return (int)m;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }

    private static DateTime? Date(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return null;
        return d.Year <= 1 ? null : d;
    }
}
