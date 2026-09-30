using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Stores;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>
/// Item / product / service questions answered by the server instead of a generated query:
/// <list type="bullet">
/// <item><c>itemStock</c>: current (or as-of) stock of ONE item by warehouse, optionally its value.</item>
/// <item><c>itemDetails</c>: code, type, unit, selling price / service rate.</item>
/// </list>
/// The item is resolved first: exact code / barcode / EAN / part number / alternate-unit barcode or code, then the
/// exact (normalised) name, alias or alternate-unit name, and only then a partial match — which is never used
/// silently. Several matches → a short list to choose from (the chosen option re-runs the report with the verified
/// item id). No match → "not found", never "stock 0". Services and non-tracked items have no stock.
/// Stock follows the MySaleBooks Stock screen: Σ stockIn of IN rows − Σ stockOut of OUT rows over all non-cancelled
/// movements of the item, in the item's stock unit (Item.unitId), shown as main + sub unit like the Stock screen.
/// </summary>
public sealed partial class MySaleBooksReports
{
    public const string ItemStockReport = "itemStock";
    public const string ItemDetailsReport = "itemDetails";

    /// <summary>Resolution result: the matching items and how they were found (id | code | name | partial).</summary>
    internal sealed record ItemMatch(List<JsonObject> Items, string How);

    private const int MaxChoices = 6;

    private static JsonObject ItemProjection() => new()
    {
        ["itemName"] = 1, ["itemLocalName"] = 1, ["itemCode"] = 1, ["barcode"] = 1, ["itemType"] = 1, ["unitId"] = 1,
        ["categoryId"] = 1, ["costingType"] = 1, ["landingCost"] = 1, ["taxExcAmount"] = 1, ["taxIncAmount"] = 1,
        ["saleTax"] = 1, ["minimumSellingRate"] = 1, ["maximumSellingRate"] = 1, ["isTrackInventory"] = 1, ["isKotItem"] = 1,
        ["isCombo"] = 1, ["reOrderLevel"] = 1, ["minimumStockQty"] = 1, ["alternateUnits"] = 1
    };

    // ------------------------------------------------------------------ resolution

    /// <summary>
    /// Finds the item the customer means. A verified id (from an earlier selection) wins; then exact identifiers; then
    /// the exact name (case, spacing and "500ml" / "500 ml" differences ignored — variant numbers are kept); then a
    /// partial match on all words. Cancelled / deleted items are not offered.
    /// </summary>
    private async Task<ItemMatch> ResolveItemAsync(string? text, string? itemId, MqlValidationContext context, AppSettings settings, Run run,
        CancellationToken ct, string? comment)
    {
        JsonObject Active(JsonObject match)
        {
            match["isCanceled"] = new JsonObject { ["$ne"] = true };
            match["isDeleted"] = new JsonObject { ["$ne"] = true };
            return match;
        }

        async Task<List<JsonObject>> Query(JsonObject match) => (await AggregateAsync("Item", new JsonArray
        {
            new JsonObject { ["$match"] = Active(match) },
            new JsonObject { ["$project"] = ItemProjection() },
            new JsonObject { ["$sort"] = new JsonObject { ["itemName"] = 1, ["_id"] = 1 } },
            new JsonObject { ["$limit"] = MaxChoices + 5 }
        }, context, settings, run, ct, comment)).Rows;

        if (itemId is not null && Regex.IsMatch(itemId, "^[0-9a-fA-F]{24}$"))
        {
            var byId = await Query(new JsonObject { ["_id"] = new JsonObject { ["$oid"] = itemId.ToLowerInvariant() } });
            return new ItemMatch(byId, "id");
        }

        var cleaned = CleanItemText(text);
        if (cleaned.Length == 0) return new ItemMatch(new List<JsonObject>(), "none");

        // A choice label written back ("Pepsi 500 ML (code P-500)") → try the code inside the brackets first.
        var labelled = Regex.Match(cleaned, @"^(?<name>.+?)\s*\((?:code\s+)?(?<code>[^(),]+)(?:,[^()]*)?\)\s*$", RegexOptions.IgnoreCase);
        var candidates = labelled.Success
            ? new[] { labelled.Groups["code"].Value.Trim(), cleaned, labelled.Groups["name"].Value.Trim() }
            : new[] { cleaned };

        foreach (var value in candidates.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // 1. Exact identifiers (codes, SKUs, barcodes): case-insensitive, whole value.
            var id = ExactPattern(value);
            var byCode = await Query(new JsonObject
            {
                ["$or"] = new JsonArray(IdentifierFields.Select(f => (JsonNode)new JsonObject { [f] = new JsonObject { ["$regex"] = id, ["$options"] = "i" } }).ToArray())
            });
            if (run.Error is not null || run.StoreMissing) return new ItemMatch(new List<JsonObject>(), "none");
            if (byCode.Count > 0) return new ItemMatch(byCode, "code");

            // 2. Exact name (normalised), local name, alias or alternate-unit name.
            var name = NamePattern(value);
            var byName = await Query(new JsonObject
            {
                ["$or"] = new JsonArray(NameFields.Select(f => (JsonNode)new JsonObject { [f] = new JsonObject { ["$regex"] = name, ["$options"] = "i" } }).ToArray())
            });
            if (run.Error is not null || run.StoreMissing) return new ItemMatch(new List<JsonObject>(), "none");
            if (byName.Count > 0) return new ItemMatch(byName, "name");
        }

        // 3. Fallback: every word appears in the name, local name or alias (never chosen without the customer).
        var words = Words(cleaned).Take(5).ToList();
        if (words.Count == 0) return new ItemMatch(new List<JsonObject>(), "none");
        var partial = await Query(new JsonObject
        {
            ["$and"] = new JsonArray(words.Select(w => (JsonNode)new JsonObject
            {
                ["$or"] = new JsonArray(new[] { "itemName", "itemLocalName", "aliasDetails.aliasName" }
                    .Select(f => (JsonNode)new JsonObject { [f] = new JsonObject { ["$regex"] = Regex.Escape(w), ["$options"] = "i" } }).ToArray())
            }).ToArray())
        });
        return new ItemMatch(partial, partial.Count > 0 ? "partial" : "none");
    }

    private static readonly string[] IdentifierFields =
    {
        "itemCode", "barcode", "eancode", "partNumber", "partNumberDetails.partNumber", "alternateUnits.barcode", "alternateUnits.alternateItemCode"
    };

    private static readonly string[] NameFields = { "itemName", "itemLocalName", "aliasDetails.aliasName", "alternateUnits.alternateItemName" };

    /// <summary>Removes quotes, "the item", trailing punctuation; collapses spaces; keeps variant details. Max 50 characters.</summary>
    internal static string CleanItemText(string? text)
    {
        var t = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim().Trim('"', '\'', '“', '”', '‘', '’', '`').Trim();
        t = Regex.Replace(t, @"^(the\s+)?(item|product|service)\s+(named|called|code|with\s+code)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        t = t.TrimEnd('?', '.', '!', ',').Trim();
        return t.Length > 50 ? t[..50].TrimEnd() : t;
    }

    /// <summary>Whole value, case-insensitive, surrounding spaces ignored.</summary>
    internal static string ExactPattern(string value) => "^\\s*" + Regex.Escape(value.Trim()) + "\\s*$";

    /// <summary>
    /// Whole name with flexible spacing: words may be separated by spaces, "-", "_", "/" or "."; a number and its unit may
    /// be written together or apart ("500ml" = "500 ml" = "500 ML"). Different numbers never match (variants stay distinct).
    /// </summary>
    internal static string NamePattern(string value)
    {
        var tokens = Regex.Matches(value, @"\d+(?:[.,]\d+)?|[\p{L}\p{M}]+|[^\s\p{L}\p{M}\p{N}]", RegexOptions.CultureInvariant)
            .Select(m => m.Value)
            .Where(t => !Regex.IsMatch(t, @"^[\-_/.]$"))
            .ToList();
        if (tokens.Count == 0) return ExactPattern(value);
        var sb = new System.Text.StringBuilder("^\\s*");
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0) sb.Append("[\\s\\-_/.]*");
            sb.Append(Regex.Escape(tokens[i]).Replace(",", "[.,]"));
        }
        sb.Append("\\s*$");
        return sb.ToString();
    }

    private static IEnumerable<string> Words(string text)
        => Regex.Matches(text, @"[\p{L}\p{M}\p{N}]+").Select(m => m.Value).Where(w => w.Length >= 2 || char.IsDigit(w[0]));

    internal static string KindOf(JsonObject item) => (Str(item["itemType"]) ?? "product").ToLowerInvariant() switch
    {
        "service" => "service",
        "rawmaterial" => "raw material",
        _ => "product"
    };

    /// <summary>Stock-tracked like the MySaleBooks stock reports: product / raw material, isTrackInventory not false, not a KOT item.</summary>
    internal static bool IsStockTracked(JsonObject item)
        => KindOf(item) != "service" && Bool(item["isTrackInventory"]) != false && Bool(item["isKotItem"]) != true;

    /// <summary>"Pepsi 500 ML (code P-500)", "Car wash (service)" — at most 60 characters.</summary>
    internal static string ChoiceLabel(JsonObject item, bool withKind)
    {
        var name = Str(item["itemName"]) ?? Str(item["itemLocalName"]) ?? "(no name)";
        var code = Str(item["itemCode"]) ?? Str(item["barcode"]);
        var parts = new List<string>();
        if (code is not null) parts.Add("code " + code);
        if (withKind) parts.Add(KindOf(item));
        var suffix = parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : string.Empty;
        var room = Math.Max(10, 60 - suffix.Length);
        if (name.Length > room) name = name[..(room - 1)].TrimEnd() + "…";
        return (name + suffix).Length <= 60 ? name + suffix : (name + suffix)[..60];
    }

    /// <summary>One item, or the outcome to return instead (not found / choose one / confirm a partial match).</summary>
    private static (JsonObject? Item, ReportOutcome? Stop) Pick(ItemMatch match, string? text, Run run, string noun = "item or service")
    {
        var asked = CleanItemText(text);
        if (match.Items.Count == 0)
            return (null, new ReportOutcome
            {
                Kind = "notfound",
                Message = string.IsNullOrEmpty(asked)
                    ? $"I couldn't find that {noun}."
                    : $"I couldn't find an {noun} matching “{asked}”. Please check the name, item code or barcode.",
                Queries = run.Queries
            });

        var items = match.Items;
        var kinds = items.Select(KindOf).Distinct().ToList();
        var withKind = kinds.Count > 1 || kinds.Contains("service");
        var shown = items.Take(MaxChoices).ToList();
        var labels = shown.Select(i => ChoiceLabel(i, withKind)).ToList();
        for (var i = 0; i < labels.Count; i++)                                 // identical labels: add a number
            if (labels.Count(l => l == labels[i]) > 1) labels[i] = labels[i].Length > 56 ? labels[i][..56] + $" #{i + 1}" : labels[i] + $" #{i + 1}";
        var ids = shown.Select(i => Str(i["_id"]) ?? string.Empty).ToList();

        if (match.How == "partial" && items.Count == 1)
            return (null, new ReportOutcome
            {
                Kind = "clarify",
                Message = $"I couldn't find an item named exactly “{asked}”. Did you mean {labels[0]}?",
                Options = labels, OptionIds = ids, AnswerArgument = "item", Queries = run.Queries
            });

        if (items.Count > 1)
        {
            string question;
            if (kinds.Contains("service") && kinds.Count > 1)
                question = $"“{asked}” matches both a product and a service. Which one do you mean?";
            else if (items.Count > MaxChoices)
                question = $"More than {MaxChoices} items match “{asked}”. Which one do you mean? You can also type the item code or barcode.";
            else if (match.How == "partial")
                question = $"I couldn't find an item named exactly “{asked}”. Which of these do you mean?";
            else
                question = $"Several items match “{asked}”. Which one do you mean?";
            return (null, new ReportOutcome { Kind = "clarify", Message = question, Options = labels, OptionIds = ids, AnswerArgument = "item", Queries = run.Queries });
        }
        return (items[0], null);
    }

    // ------------------------------------------------------------------ item stock

    private async Task<ReportOutcome> ItemStockAsync(JsonObject args, DateAnchors anchors, MqlValidationContext context, AppSettings settings,
        int decimals, string? currency, Run run, CancellationToken ct, string? comment)
    {
        var measure = (Str(args["measure"]) ?? "quantity").ToLowerInvariant();
        if (measure is "details" or "price" or "rate")
            return await ItemDetailsAsync(args, context, settings, decimals, currency, run, ct, comment);

        var itemText = Str(args["item"]);
        if (itemText is null && Str(args["itemId"]) is null)
            return Clarify("Which item or product should I show the stock for? You can type its name, code or barcode.", run, answerArgument: "item");

        DateOnly? asOf = DateOnly.TryParseExact(Str(args["asOf"]) ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        if (asOf is { } future && future > anchors.LocalToday) asOf = null;           // "as of" a future day = current stock

        var match = await ResolveItemAsync(itemText, Str(args["itemId"]), context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
        var (item, stop) = Pick(match, itemText, run);
        if (stop is not null) return stop;

        var itemId = Str(item!["_id"])!;
        var name = Str(item["itemName"]) ?? Str(item["itemLocalName"]) ?? CleanItemText(itemText);
        var code = Str(item["itemCode"]);
        var title = $"“{name}”" + (code is null ? string.Empty : $" ({code})");
        var where = StoreText(context.Store);

        if (!IsStockTracked(item))
        {
            var reason = KindOf(item) == "service" ? "is a service" : Bool(item["isKotItem"]) == true ? "is a kitchen (KOT) item" : "is not stock-tracked";
            var rate = SellingPrice(item, decimals, currency);
            return new ReportOutcome
            {
                Kind = "ok",
                Message = $"{title} {reason}, so it has no stock." + (rate is null ? string.Empty : $" Its {(KindOf(item) == "service" ? "rate" : "selling price")} is {rate}."),
                Rows = await DetailRowsAsync(item, context, settings, decimals, currency, run, ct, comment),
                Columns = new() { "detail", "value" },
                Explanation = $"{title} {reason}: MySaleBooks does not keep stock for it.",
                Queries = run.Queries,
                ElapsedMs = run.Ms
            };
        }

        var unit = await UnitInfoAsync(Str(item["unitId"]), context, settings, run, ct, comment);
        var match0 = new JsonObject { ["itemId"] = itemId };
        if (asOf is { } day)
        {
            var end = DateAnchors.ForLocalDays(day, day, anchors.BoundaryTimeZoneId).EndUtc;
            match0["$or"] = new JsonArray(
                new JsonObject { ["transactionType"] = "OPSTOCK" },               // opening stock counts whatever its date
                new JsonObject { ["transactionDate"] = new JsonObject { ["$lt"] = DateNode(end) } });
        }
        var byLocation = await AggregateAsync("StockMaster", new JsonArray
        {
            new JsonObject { ["$match"] = match0 },
            new JsonObject
            {
                ["$group"] = new JsonObject
                {
                    ["_id"] = "$stockLocationId",
                    ["received"] = PipeSum("IN", "stockIn"),
                    ["issued"] = PipeSum("OUT", "stockOut"),
                    ["movements"] = new JsonObject { ["$sum"] = 1 }
                }
            },
            new JsonObject { ["$sort"] = new JsonObject { ["_id"] = 1 } }
        }, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();

        var movements = byLocation.Rows.Sum(r => Num(r["movements"]));
        var total = byLocation.Rows.Sum(r => Num(r["received"]) - Num(r["issued"]));
        var locations = await LocationNamesAsync(byLocation.Rows.Select(r => Str(r["_id"])).Where(x => x is not null).Distinct().ToList()!,
            context, settings, run, ct, comment);
        var asOfText = asOf is { } a ? $" on {QuestionDates.Describe(a)}" : string.Empty;

        if (movements == 0)
            return new ReportOutcome
            {
                Kind = "ok",
                Message = $"{title} exists, but it has no stock movements{where}{(asOf is null ? " yet" : " up to" + asOfText)}, so its stock is 0 {unit.Name ?? "units"}.",
                Rows = new List<JsonObject> { StockRow("All warehouses", 0m, unit) },
                Columns = new() { "warehouse", "quantity", "unit", "stock" },
                Explanation = $"Stock of {title}{where}{asOfText}: no stock movements (the item exists in the item master).",
                Queries = run.Queries,
                ElapsedMs = run.Ms
            };

        var rows = byLocation.Rows
            .Select(r =>
            {
                var loc = Str(r["_id"]);
                var label = locations.Label(loc);
                return StockRow(label, Num(r["received"]) - Num(r["issued"]), unit);
            })
            .ToList();
        if (rows.Count > 1) rows.Add(StockRow("Total", total, unit));

        var message = $"{(asOf is null ? "Current stock" : "Stock")} of {title}{where}{asOfText}: {FormatQuantity(total, unit)}"
                      + (rows.Count > 2 ? $" across {rows.Count - 1} warehouses." : ".")
                      + (total < 0 ? " The stock is negative: more has been issued than received." : string.Empty);

        if (measure is "value" or "cost" or "worth")
        {
            if (asOf is not null)
                return new ReportOutcome
                {
                    Kind = "ok",
                    Message = message + " Stock value on a past date depends on the MySaleBooks costing method and is not recalculated here — please use the MySaleBooks Stock Register for it.",
                    Rows = rows, Columns = new() { "warehouse", "quantity", "unit", "stock" },
                    Explanation = "Historical stock quantity; historical value not recalculated.", Queries = run.Queries, ElapsedMs = run.Ms
                };
            var cost = await UnitCostAsync(item, itemId, context, settings, run, ct, comment);
            if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
            var value = Math.Round(total * cost, decimals);
            foreach (var r in rows) r["value"] = Math.Round(Num(r["quantity"]) * cost, decimals);
            return new ReportOutcome
            {
                Kind = "ok",
                Message = message + $" Stock value: {Money(value, decimals, currency)} at {Money(Math.Round(cost, decimals), decimals, currency)} per {unit.Name ?? "unit"} "
                          + (IsFifo(item) ? "(FIFO cost, as the MySaleBooks Stock screen)." : "(item landing cost, as the MySaleBooks Stock screen)."),
                Rows = rows,
                Columns = new() { "warehouse", "quantity", "unit", "stock", "value" },
                Explanation = $"Current stock and stock value of {title}{where}.",
                Queries = run.Queries,
                ElapsedMs = run.Ms
            };
        }

        return new ReportOutcome
        {
            Kind = "ok",
            Message = message,
            Rows = rows,
            Columns = new() { "warehouse", "quantity", "unit", "stock" },
            Explanation = $"{(asOf is null ? "Current stock" : "Stock")} of {title}{where}{asOfText} by warehouse, in {unit.Name ?? "the item's stock unit"} "
                          + "(all non-cancelled movements: opening stock, purchases, sales, returns, adjustments and transfers).",
            Queries = run.Queries,
            ElapsedMs = run.Ms
        };
    }

    private static JsonObject PipeSum(string pipe, string field) => new()
    {
        ["$sum"] = new JsonObject { ["$cond"] = new JsonArray(new JsonObject { ["$eq"] = new JsonArray("$transactionPipe", pipe) }, "$" + field, 0) }
    };

    private static JsonObject StockRow(string warehouse, decimal quantity, UnitInfo unit) => new()
    {
        ["warehouse"] = warehouse,
        ["quantity"] = quantity,
        ["unit"] = unit.Name,
        ["stock"] = FormatQuantity(quantity, unit)
    };

    private static bool IsFifo(JsonObject item) => string.Equals(Str(item["costingType"]) ?? "FIFO", "FIFO", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Unit cost as the MySaleBooks Stock screen: FIFO items → Σ(balanceStock × landedCost) ÷ Σ balanceStock of the
    /// unconsumed receipt layers (IN rows), falling back to Item.landingCost; other costing types → Item.landingCost.
    /// </summary>
    private async Task<decimal> UnitCostAsync(JsonObject item, string itemId, MqlValidationContext context, AppSettings settings, Run run,
        CancellationToken ct, string? comment)
    {
        var landing = Num(item["landingCost"]);
        if (!IsFifo(item)) return landing;
        var layers = await AggregateAsync("StockMaster", new JsonArray
        {
            new JsonObject { ["$match"] = new JsonObject { ["itemId"] = itemId, ["transactionPipe"] = "IN", ["balanceStock"] = new JsonObject { ["$gt"] = 0 } } },
            new JsonObject
            {
                ["$group"] = new JsonObject
                {
                    ["_id"] = null,
                    ["amount"] = new JsonObject { ["$sum"] = new JsonObject { ["$multiply"] = new JsonArray("$balanceStock", "$landedCost") } },
                    ["layers"] = new JsonObject { ["$sum"] = "$balanceStock" }
                }
            }
        }, context, settings, run, ct, comment);
        var row = layers.Rows.FirstOrDefault();
        var qty = Num(row?["layers"]);
        var cost = qty > 0 ? Num(row?["amount"]) / qty : 0m;
        return cost > 0 ? cost : landing;
    }

    // ------------------------------------------------------------------ item details / price

    private async Task<ReportOutcome> ItemDetailsAsync(JsonObject args, MqlValidationContext context, AppSettings settings, int decimals, string? currency,
        Run run, CancellationToken ct, string? comment)
    {
        var itemText = Str(args["item"]);
        if (itemText is null && Str(args["itemId"]) is null)
            return Clarify("Which item or service do you mean? You can type its name, code or barcode.", run, answerArgument: "item");
        var match = await ResolveItemAsync(itemText, Str(args["itemId"]), context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
        var (item, stop) = Pick(match, itemText, run);
        if (stop is not null) return stop;

        var name = Str(item!["itemName"]) ?? CleanItemText(itemText);
        var code = Str(item["itemCode"]);
        var title = $"“{name}”" + (code is null ? string.Empty : $" ({code})");
        var service = KindOf(item) == "service";
        var price = SellingPrice(item, decimals, currency);
        var rows = await DetailRowsAsync(item, context, settings, decimals, currency, run, ct, comment);
        var unit = rows.FirstOrDefault(r => Str(r["detail"]) == "Unit") is { } u ? Str(u["value"]) : null;
        return new ReportOutcome
        {
            Kind = "ok",
            Message = price is null
                ? $"{title} is a {KindOf(item)}. No {(service ? "rate" : "selling price")} is set for it."
                : $"{title} is a {KindOf(item)}. {(service ? "Rate" : "Selling price")}: {price}{(unit is null || service ? string.Empty : " per " + unit)}.",
            Rows = rows,
            Columns = new() { "detail", "value" },
            Explanation = $"Item master details of {title}.",
            Queries = run.Queries,
            ElapsedMs = run.Ms
        };
    }

    /// <summary>"OMR 1.250 incl. tax (OMR 1.190 excl. tax)" — null when no price is set.</summary>
    private static string? SellingPrice(JsonObject item, int decimals, string? currency)
    {
        var inc = Num(item["taxIncAmount"]);
        var exc = Num(item["taxExcAmount"]);
        if (inc <= 0 && exc <= 0) return null;
        if (inc > 0 && exc > 0 && inc != exc) return $"{Money(inc, decimals, currency)} incl. tax ({Money(exc, decimals, currency)} excl. tax)";
        return Money(inc > 0 ? inc : exc, decimals, currency);
    }

    private async Task<List<JsonObject>> DetailRowsAsync(JsonObject item, MqlValidationContext context, AppSettings settings, int decimals, string? currency,
        Run run, CancellationToken ct, string? comment)
    {
        var rows = new List<JsonObject>();
        void Add(string detail, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add(new JsonObject { ["detail"] = detail, ["value"] = value }); }

        Add("Name", Str(item["itemName"]));
        Add("Local name", Str(item["itemLocalName"]));
        Add("Item code", Str(item["itemCode"]));
        Add("Barcode", Str(item["barcode"]));
        Add("Type", KindOf(item) + (IsStockTracked(item) ? string.Empty : " (not stock-tracked)"));
        var unit = await UnitInfoAsync(Str(item["unitId"]), context, settings, run, ct, comment);
        Add("Unit", unit.Name);
        Add("Category", await NameByIdAsync("Category", "categoryName", Str(item["categoryId"]), context, settings, run, ct, comment));
        Add(KindOf(item) == "service" ? "Rate" : "Selling price", SellingPrice(item, decimals, currency));
        if (Num(item["saleTax"]) > 0) Add("Sales tax", Num(item["saleTax"]).ToString("0.##", CultureInfo.InvariantCulture) + "%");
        if (Num(item["minimumSellingRate"]) > 0) Add("Minimum selling rate", Money(Num(item["minimumSellingRate"]), decimals, currency));
        if (Num(item["maximumSellingRate"]) > 0) Add("Maximum selling rate", Money(Num(item["maximumSellingRate"]), decimals, currency));
        if (item["alternateUnits"] is JsonArray alternates)
            foreach (var alt in alternates.OfType<JsonObject>().Take(5))
            {
                var altPrice = SellingPrice(alt, decimals, currency);
                var altName = Str(alt["unitName"]) ?? "Alternate unit";
                if (altPrice is not null) Add($"Price per {altName}", altPrice);
                Add($"Barcode ({altName})", Str(alt["barcode"]));
            }
        if (IsStockTracked(item) && Num(item["reOrderLevel"]) > 0) Add("Reorder level", FormatQuantity(Num(item["reOrderLevel"]), unit));
        return rows;
    }

    // ------------------------------------------------------------------ units

    internal sealed record UnitInfo(string? Name, decimal Conversion, string? SubName);

    /// <summary>Stock unit of the item with its sub unit (Unit[parentId]) and conversion (sub units in one unit).</summary>
    private async Task<UnitInfo> UnitInfoAsync(string? unitId, MqlValidationContext context, AppSettings settings, Run run, CancellationToken ct, string? comment)
    {
        if (unitId is null || !Regex.IsMatch(unitId, "^[0-9a-fA-F]{24}$") || !context.Collections.Any(c => c.Name == "Unit")) return new UnitInfo(null, 0, null);
        var r = await AggregateAsync("Unit", new JsonArray
        {
            new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$oid"] = unitId.ToLowerInvariant() } } },
            new JsonObject { ["$project"] = new JsonObject { ["unitName"] = 1, ["unitShortName"] = 1, ["parentId"] = 1, ["conversion"] = 1 } }
        }, context, settings, run, ct, comment, optional: true);
        var u = r.Rows.FirstOrDefault();
        if (u is null) return new UnitInfo(null, 0, null);
        var name = Str(u["unitShortName"]) ?? Str(u["unitName"]);
        var conversion = Num(u["conversion"]);
        string? sub = null;
        var parent = Str(u["parentId"]);
        if (conversion > 0 && parent is not null && parent != unitId && Regex.IsMatch(parent, "^[0-9a-fA-F]{24}$"))
            sub = await NameByIdAsync("Unit", "unitShortName", parent, context, settings, run, ct, comment)
                  ?? await NameByIdAsync("Unit", "unitName", parent, context, settings, run, ct, comment);
        return new UnitInfo(name, conversion, sub);
    }

    /// <summary>
    /// "12 Pcs"; with a sub unit like the MySaleBooks Stock screen (Common.GetMainAndSubStock): main = whole units,
    /// sub = fraction × conversion ("2 Box 6 Pcs" for 2.5 Box of 12 Pcs).
    /// </summary>
    internal static string FormatQuantity(decimal quantity, UnitInfo unit)
    {
        string N(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var name = unit.Name ?? "units";
        if (unit.SubName is null || unit.Conversion <= 0 || quantity == decimal.Truncate(quantity)) return $"{N(quantity)} {name}";
        var main = decimal.Truncate(quantity);
        var sub = Math.Round((quantity - main) * unit.Conversion, 6);
        if (Math.Abs(sub) == unit.Conversion) { main += Math.Sign(sub); sub = 0; }
        else sub %= unit.Conversion;
        return sub == 0 ? $"{N(main)} {name}" : main == 0 ? $"{N(sub)} {unit.SubName}" : $"{N(main)} {name} {N(Math.Abs(sub))} {unit.SubName}";
    }

    private async Task<string?> NameByIdAsync(string collection, string field, string? id, MqlValidationContext context, AppSettings settings, Run run,
        CancellationToken ct, string? comment)
    {
        if (id is null || !Regex.IsMatch(id, "^[0-9a-fA-F]{24}$") || !context.Collections.Any(c => c.Name == collection)) return null;
        var r = await AggregateAsync(collection, new JsonArray
        {
            new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$oid"] = id.ToLowerInvariant() } } },
            new JsonObject { ["$project"] = new JsonObject { [field] = 1 } }
        }, context, settings, run, ct, comment, optional: true);
        return Str(r.Rows.FirstOrDefault()?[field]);
    }

    // ------------------------------------------------------------------ helpers

    private static string StoreText(StoreScope? store) => store?.Mode switch
    {
        StoreMode.Selected => " in " + (string.IsNullOrWhiteSpace(store.StoreName) ? "the selected store" : store.StoreName),
        StoreMode.AllStores => " across all stores",
        _ => string.Empty
    };

    private static string Money(decimal amount, int decimals, string? currency)
    {
        var text = amount.ToString("N" + Math.Clamp(decimals, 0, 6), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(currency) ? text : currency.Trim() + " " + text;
    }

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
