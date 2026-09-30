using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>Outcome of a deterministic MySaleBooks report (ledger statement / stock movement).</summary>
public sealed class ReportOutcome
{
    /// <summary>ok | clarify | notfound | invalid | blocked</summary>
    public string Kind { get; init; } = "ok";
    public string? Message { get; init; }
    public List<JsonObject> Rows { get; init; } = new();
    public List<string> Columns { get; init; } = new();
    public string Explanation { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    /// <summary>Every query that ran (validated, tenant- and store-scoped MQL) — for the activity / query log.</summary>
    public List<string> Queries { get; init; } = new();
    public long ElapsedMs { get; init; }
    public bool StoreContextMissing { get; init; }
    /// <summary>Clarify: the choices (matching ledgers / products) shown as buttons.</summary>
    public List<string>? Options { get; init; }
    /// <summary>Clarify: verified record ids of the choices (same order) — a chosen option re-runs the report with its id.</summary>
    public List<string>? OptionIds { get; init; }
    /// <summary>Clarify: report argument that the customer's answer fills ("item", "ledger") — the report then runs again directly.</summary>
    public string? AnswerArgument { get; init; }
    /// <summary>References the report could not name (orphan ids / unreadable master) — logged for data-integrity review.</summary>
    public List<string> Integrity { get; } = new();
}

/// <summary>
/// Server-side reports whose logic cannot be expressed safely by a generated query (running balances with an opening
/// balance). They reproduce the MySaleBooks rules (Ledger Book: ledger opening (credit wins) + earlier lines, cancelled and
/// unposted PDC lines excluded, lines merged per voucher, running balance Dr positive; stock: Σ IN stockIn − Σ OUT stockOut,
/// opening stock rows always in the opening). Every database read goes through <see cref="QueryEngine.Prepare"/>: the same
/// validation, tenant scope, selected-store filter and cancelled-document filter as any other query.
/// </summary>
public sealed partial class MySaleBooksReports
{
    private readonly QueryEngine _engine;
    public MySaleBooksReports(QueryEngine engine) => _engine = engine;

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ledgerStatement", "stockMovement", ItemStockReport, ItemDetailsReport, StockSummaryReport };

    private sealed class Run
    {
        public List<string> Queries { get; } = new();
        public long Ms { get; set; }
        public bool StoreMissing { get; set; }
        public string? Error { get; set; }
        public List<string> Integrity { get; } = new();
    }

    public async Task<ReportOutcome> RunAsync(MqlQuery plan, MqlValidationContext context, DateAnchors anchors, AppSettings settings,
        int decimals, CancellationToken ct, string? comment = null, string? currency = null)
    {
        var args = plan.Arguments ?? new JsonObject();
        var report = Str(args["report"]);
        if (report is null || !Supported.Contains(report))
            return new ReportOutcome { Kind = "invalid", Message = "Unknown report." };

        var (from, to, periodError) = Period(args, anchors);
        if (periodError is not null) return new ReportOutcome { Kind = "invalid", Message = periodError };

        var run = new Run();
        var outcome = report.ToLowerInvariant() switch
        {
            "ledgerstatement" => await LedgerStatementAsync(args, from, to, context, anchors, settings, decimals, run, ct, comment),
            "itemstock" => await ItemStockAsync(args, anchors, context, settings, decimals, currency, run, ct, comment),
            "itemdetails" => await ItemDetailsAsync(args, context, settings, decimals, currency, run, ct, comment),
            "stocksummary" => await StockSummaryAsync(args, anchors, context, settings, decimals, currency, run, ct, comment),
            _ => await StockMovementAsync(Str(args["item"]), Str(args["itemId"]), from, to, context, anchors, settings, run, ct, comment)
        };
        if (run.StoreMissing) return new ReportOutcome { Kind = "blocked", StoreContextMissing = true, Queries = run.Queries };
        if (run.Error is not null) return new ReportOutcome { Kind = "blocked", Message = run.Error, Queries = run.Queries, ElapsedMs = run.Ms };
        outcome.Integrity.AddRange(run.Integrity);
        return outcome;
    }

    // ------------------------------------------------------------------ ledger statement

    private async Task<ReportOutcome> LedgerStatementAsync(JsonObject args, DateOnly from, DateOnly to, MqlValidationContext context,
        DateAnchors anchors, AppSettings settings, int decimals, Run run, CancellationToken ct, string? comment)
    {
        var ledgerText = Str(args["ledger"]);
        if (string.IsNullOrWhiteSpace(ledgerText) && Str(args["ledgerId"]) is null)
            return Clarify("Which ledger (customer, supplier, cash, bank or other account) should I show the statement for?", run, answerArgument: "ledger");

        var pick = await PickEntityAsync("ledger", "ledger", args,
            new JsonObject { ["ledgerName"] = 1, ["groupName"] = 1, ["opBalanceDebit"] = 1, ["opBalanceCredit"] = 1 }, context, settings, run, ct, comment);
        if (pick.Outcome is not null) return pick.Outcome;
        var ledger = pick.Record!;
        ledgerText ??= Str(ledger["ledgerName"]) ?? string.Empty;
        var ledgerId = Str(ledger["_id"])!;
        var ledgerName = Str(ledger["ledgerName"]) ?? ledgerText.Trim();
        var range = DateAnchors.ForLocalDays(from, to, anchors.BoundaryTimeZoneId);

        // Ledger Book convention: the credit opening wins when both are set (opBalanceCredit != 0 ? -credit : debit).
        var opCr = Num(ledger["opBalanceCredit"]);
        var ledgerOpening = opCr != 0 ? -opCr : Num(ledger["opBalanceDebit"]);

        JsonObject LineFilter(JsonObject dateCondition) => new()
        {
            ["ledgerId"] = ledgerId,
            ["voucherDate"] = dateCondition,
            ["isPosted"] = new JsonObject { ["$ne"] = false }
        };
        JsonObject Totals() => new()
        {
            ["$group"] = new JsonObject
            {
                ["_id"] = null,
                ["debit"] = new JsonObject { ["$sum"] = "$debit" },
                ["credit"] = new JsonObject { ["$sum"] = "$credit" },
                ["vouchers"] = new JsonObject { ["$addToSet"] = "$voucherGuId" }
            }
        };

        var before = await AggregateAsync("AccountVoucher", new JsonArray
        {
            new JsonObject { ["$match"] = LineFilter(new JsonObject { ["$lt"] = DateNode(range.StartUtc) }) },
            Totals(),
            new JsonObject { ["$project"] = new JsonObject { ["_id"] = 0, ["debit"] = 1, ["credit"] = 1 } }
        }, context, settings, run, ct, comment);
        var period = await AggregateAsync("AccountVoucher", new JsonArray
        {
            new JsonObject { ["$match"] = LineFilter(Between(range)) },
            Totals(),
            new JsonObject { ["$project"] = new JsonObject { ["_id"] = 0, ["debit"] = 1, ["credit"] = 1, ["voucherCount"] = new JsonObject { ["$size"] = "$vouchers" } } }
        }, context, settings, run, ct, comment);
        var lines = await AggregateAsync("AccountVoucher", new JsonArray
        {
            new JsonObject { ["$match"] = LineFilter(Between(range)) },
            new JsonObject
            {
                ["$group"] = new JsonObject
                {
                    ["_id"] = "$voucherGuId",
                    ["voucherDate"] = new JsonObject { ["$first"] = "$voucherDate" },
                    ["voucherNo"] = new JsonObject { ["$first"] = "$voucherNo" },
                    ["voucherType"] = new JsonObject { ["$first"] = "$voucherType" },
                    ["narration"] = new JsonObject { ["$first"] = "$narration" },
                    ["debit"] = new JsonObject { ["$sum"] = "$debit" },
                    ["credit"] = new JsonObject { ["$sum"] = "$credit" },
                    ["firstLineId"] = new JsonObject { ["$min"] = "$_id" }
                }
            },
            // Business order: voucher date, then the voucher's first line (stable tie-breaker for the same date).
            new JsonObject { ["$sort"] = new JsonObject { ["voucherDate"] = 1, ["firstLineId"] = 1 } }
        }, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();

        var b = before.Rows.FirstOrDefault();
        var p = period.Rows.FirstOrDefault();
        var opening = ledgerOpening + Num(b?["debit"]) - Num(b?["credit"]);
        var periodDebit = Num(p?["debit"]);
        var periodCredit = Num(p?["credit"]);
        var closing = opening + periodDebit - periodCredit;

        var rows = new List<JsonObject> { StatementRow(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, "Opening balance", null, null, null, opening, decimals) };
        var balance = opening;
        foreach (var line in lines.Rows)
        {
            var debit = Num(line["debit"]);
            var credit = Num(line["credit"]);
            balance += debit - credit;
            rows.Add(StatementRow(DateText(line["voucherDate"], anchors), Str(line["voucherNo"]), Label(Str(line["voucherType"])),
                Str(line["narration"]), debit, credit, balance, decimals));
        }
        rows.Add(StatementRow(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, "Closing balance", null, periodDebit, periodCredit, closing, decimals));

        return new ReportOutcome
        {
            Rows = rows,
            Columns = new() { "date", "voucherNo", "type", "narration", "debit", "credit", "balance", "drCr" },
            Explanation = $"Ledger statement of \"{ledgerName}\" ({Str(ledger["groupName"]) ?? "ledger"}) from {QuestionDates.Describe(from)} to {QuestionDates.Describe(to)}: " +
                          $"opening balance (ledger opening + earlier vouchers), {Num(p?["voucherCount"]):0} voucher(s) in the period with the running balance (Dr positive), closing balance. " +
                          "Cancelled vouchers and unposted post-dated cheques are excluded, like the MySaleBooks Ledger Book." +
                          (lines.Truncated ? " Only the first vouchers are listed; opening, period totals and closing cover the whole period." : string.Empty),
            Truncated = lines.Truncated,
            Queries = run.Queries,
            ElapsedMs = run.Ms
        };
    }

    private static JsonObject StatementRow(string date, string? voucherNo, string type, string? narration, decimal? debit, decimal? credit, decimal balance, int decimals) => new()
    {
        ["date"] = date,
        ["voucherNo"] = voucherNo,
        ["type"] = type,
        ["narration"] = narration,
        ["debit"] = debit is { } d ? Math.Round(d, decimals) : null,
        ["credit"] = credit is { } c ? Math.Round(c, decimals) : null,
        ["balance"] = Math.Round(Math.Abs(balance), decimals),
        ["drCr"] = balance > 0 ? "Dr" : balance < 0 ? "Cr" : ""
    };

    // ------------------------------------------------------------------ stock movement

    private async Task<ReportOutcome> StockMovementAsync(string? itemText, string? chosenItemId, DateOnly from, DateOnly to, MqlValidationContext context,
        DateAnchors anchors, AppSettings settings, Run run, CancellationToken ct, string? comment)
    {
        if (string.IsNullOrWhiteSpace(itemText) && chosenItemId is null)
            return Clarify("Which product should I show the stock movement for? You can type its name, code or barcode.", run, answerArgument: "item");

        // Same item resolution as the stock report: codes / barcodes, exact name, never a silent partial match.
        var match = await ResolveItemAsync(itemText, chosenItemId, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
        var (item, stop) = Pick(match, itemText, run, "item");
        if (stop is not null) return stop;
        if (!IsStockTracked(item!))
            return new ReportOutcome
            {
                Kind = "ok",
                Message = $"“{Str(item!["itemName"]) ?? CleanItemText(itemText)}” is a {KindOf(item!)} that is not stock-tracked, so it has no stock movements.",
                Queries = run.Queries,
                ElapsedMs = run.Ms
            };

        var itemId = Str(item!["_id"])!;
        var itemName = Str(item["itemName"]) ?? CleanItemText(itemText);
        var unit = (await UnitInfoAsync(Str(item["unitId"]), context, settings, run, ct, comment)).Name;
        var range = DateAnchors.ForLocalDays(from, to, anchors.BoundaryTimeZoneId);

        JsonObject Qty(string pipe, string field) => new()
        {
            ["$sum"] = new JsonObject { ["$cond"] = new JsonArray(new JsonObject { ["$eq"] = new JsonArray("$transactionPipe", pipe) }, "$" + field, 0) }
        };
        JsonObject InOut() => new()
        {
            ["$group"] = new JsonObject { ["_id"] = null, ["received"] = Qty("IN", "stockIn"), ["issued"] = Qty("OUT", "stockOut") }
        };

        // Opening: every OPSTOCK row (the Stock Register counts opening stock whatever its date) + other rows before the start.
        var opening = await AggregateAsync("StockMaster", new JsonArray
        {
            new JsonObject
            {
                ["$match"] = new JsonObject
                {
                    ["itemId"] = itemId,
                    ["$or"] = new JsonArray(
                        new JsonObject { ["transactionType"] = "OPSTOCK" },
                        new JsonObject { ["transactionDate"] = new JsonObject { ["$lt"] = DateNode(range.StartUtc) } })
                }
            },
            InOut(),
            new JsonObject { ["$project"] = new JsonObject { ["_id"] = 0, ["received"] = 1, ["issued"] = 1 } }
        }, context, settings, run, ct, comment);

        JsonObject PeriodMatch() => new()
        {
            ["itemId"] = itemId,
            ["transactionType"] = new JsonObject { ["$ne"] = "OPSTOCK" },
            ["transactionDate"] = Between(range)
        };
        var totals = await AggregateAsync("StockMaster", new JsonArray
        {
            new JsonObject { ["$match"] = PeriodMatch() },
            InOut(),
            new JsonObject { ["$project"] = new JsonObject { ["_id"] = 0, ["received"] = 1, ["issued"] = 1 } }
        }, context, settings, run, ct, comment);
        var movements = await AggregateAsync("StockMaster", new JsonArray
        {
            new JsonObject { ["$match"] = PeriodMatch() },
            new JsonObject
            {
                ["$project"] = new JsonObject
                {
                    ["transactionDate"] = 1,
                    ["transactionType"] = 1,
                    ["stockLocationId"] = 1,
                    ["ledgerName"] = 1,
                    ["received"] = new JsonObject { ["$cond"] = new JsonArray(new JsonObject { ["$eq"] = new JsonArray("$transactionPipe", "IN") }, "$stockIn", 0) },
                    ["issued"] = new JsonObject { ["$cond"] = new JsonArray(new JsonObject { ["$eq"] = new JsonArray("$transactionPipe", "OUT") }, "$stockOut", 0) }
                }
            },
            new JsonObject { ["$sort"] = new JsonObject { ["transactionDate"] = 1, ["_id"] = 1 } }
        }, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();

        var o = opening.Rows.FirstOrDefault();
        var t = totals.Rows.FirstOrDefault();
        var openingQty = Num(o?["received"]) - Num(o?["issued"]);
        var received = Num(t?["received"]);
        var issued = Num(t?["issued"]);
        var closingQty = openingQty + received - issued;

        var locations = await LocationNamesAsync(movements.Rows.Select(r => Str(r["stockLocationId"])).Where(x => x is not null).Distinct().ToList()!,
            context, settings, run, ct, comment);

        var rows = new List<JsonObject> { MovementRow(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Opening stock", null, null, null, null, openingQty) };
        var balance = openingQty;
        foreach (var m in movements.Rows)
        {
            var inQty = Num(m["received"]);
            var outQty = Num(m["issued"]);
            balance += inQty - outQty;
            var loc = Str(m["stockLocationId"]);
            rows.Add(MovementRow(DateText(m["transactionDate"], anchors), Label(Str(m["transactionType"])),
                locations.Label(loc), Str(m["ledgerName"]), inQty, outQty, balance));
        }
        rows.Add(MovementRow(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Closing stock", null, null, received, issued, closingQty));

        return new ReportOutcome
        {
            Rows = rows,
            Columns = new() { "date", "type", "warehouse", "party", "received", "issued", "balance" },
            Explanation = $"Stock movement of \"{itemName}\" from {QuestionDates.Describe(from)} to {QuestionDates.Describe(to)} in {unit ?? "the item's stock unit"}: " +
                          "opening stock (opening entries + earlier movements), each receipt/issue with the running balance, closing stock. " +
                          "Cancelled movements are excluded; transfers are shown as movements between warehouses." +
                          (movements.Truncated ? " Only the first movements are listed; opening, totals and closing cover the whole period." : string.Empty),
            Truncated = movements.Truncated,
            Queries = run.Queries,
            ElapsedMs = run.Ms
        };
    }

    private static JsonObject MovementRow(string date, string type, string? warehouse, string? party, decimal? received, decimal? issued, decimal balance) => new()
    {
        ["date"] = date,
        ["type"] = type,
        ["warehouse"] = warehouse,
        ["party"] = party,
        ["received"] = received,
        ["issued"] = issued,
        ["balance"] = balance
    };

    // ------------------------------------------------------------------ helpers

    private sealed record EntityPick(JsonObject? Record, ReportOutcome? Outcome);

    /// <summary>
    /// The one record the user means (reverse resolution): the id chosen in an earlier clarification
    /// (<c>{argument}Id</c>), else the name / code typed. One exact match is used; several records with the same or a
    /// similar name are offered as choices (told apart by code or number, each with its verified id) — never picked
    /// silently; none → "not found".
    /// </summary>
    private async Task<EntityPick> PickEntityAsync(string entityKey, string argument, JsonObject args, JsonObject projection, MqlValidationContext context,
        AppSettings settings, Run run, CancellationToken ct, string? comment)
    {
        var entity = EntityCatalog.Types[entityKey];
        var text = Str(args[argument]);
        if (Str(args[argument + "Id"]) is { } chosenId && QueryEngine.IsObjectIdText(chosenId))
        {
            var byId = await AggregateAsync(entity.Collection!, new JsonArray
            {
                new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$oid"] = chosenId.ToLowerInvariant() } } },
                new JsonObject { ["$project"] = (JsonObject)projection.DeepClone() },
                new JsonObject { ["$limit"] = 1 }
            }, context, settings, run, ct, comment);
            if (run.Error is not null || run.StoreMissing) return new EntityPick(null, new ReportOutcome());
            if (byId.Rows.Count == 1) return new EntityPick(byId.Rows[0], null);
        }
        if (string.IsNullOrWhiteSpace(text))
            return new EntityPick(null, new ReportOutcome { Kind = "notfound", Message = $"I couldn't find that {entity.Singular}.", Queries = run.Queries });

        var found = await FindEntityAsync(entityKey, text, projection, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new EntityPick(null, new ReportOutcome());
        var shownText = CleanItemText(text);
        if (found.Count == 0)
            return new EntityPick(null, new ReportOutcome { Kind = "notfound", Message = $"I couldn't find a {entity.Singular} named “{shownText}”.", Queries = run.Queries });
        var exact = found.Where(f => string.Equals(Str(f[entity.NameField!])?.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = exact.Count > 0 ? exact : found;
        if (candidates.Count == 1) return new EntityPick(candidates[0], null);

        var choices = candidates.Take(MaxChoices).ToList();
        var labels = ChoiceLabels(choices, entity);
        return new EntityPick(null, new ReportOutcome
        {
            Kind = "clarify",
            Message = $"Several {entity.Plural} match “{shownText}”: {string.Join(", ", labels)}. Which one do you mean?",
            Options = labels,
            OptionIds = choices.Select(c => Str(c["_id"]) ?? string.Empty).ToList(),
            AnswerArgument = argument,
            Queries = run.Queries
        });
    }

    /// <summary>Choice labels that tell records with the same name apart: "ABC Trading (SUP-01)", else "ABC Trading (2)".</summary>
    private static List<string> ChoiceLabels(List<JsonObject> records, EntityType entity)
    {
        var names = records.Select(r => Str(r[entity.NameField!])?.Trim() ?? "?").ToList();
        var labels = new List<string>();
        for (var i = 0; i < records.Count; i++)
        {
            var same = names.Count(n => string.Equals(n, names[i], StringComparison.OrdinalIgnoreCase));
            if (same == 1) { labels.Add(names[i]); continue; }
            var code = entity.CodeFields.Select(c => Str(records[i][c])).FirstOrDefault(c => c is not null);
            var index = names.Take(i + 1).Count(n => string.Equals(n, names[i], StringComparison.OrdinalIgnoreCase));
            labels.Add(code is not null ? $"{names[i]} ({code})" : $"{names[i]} ({index})");
        }
        return labels;
    }

    /// <summary>
    /// Name / code → record (reverse resolution) of a catalog entity: its verified master, name field and code fields
    /// (<see cref="EntityCatalog"/>). Several matches are returned so the caller asks which one — never picks silently.
    /// </summary>
    private Task<List<JsonObject>> FindEntityAsync(string entityKey, string text, JsonObject projection, MqlValidationContext context, AppSettings settings,
        Run run, CancellationToken ct, string? comment)
    {
        var entity = EntityCatalog.Types[entityKey];
        var projected = (JsonObject)projection.DeepClone();
        foreach (var code in entity.CodeFields) projected[code] = 1;
        return FindAsync(entity.Collection!, entity.NameField!, text, projected, context, settings, run, ct, comment,
            entity.CodeFields.Where(c => context.Collections.FirstOrDefault(x => x.Name == entity.Collection)?.Fields.Any(f => f.Name == c) == true).ToArray());
    }

    /// <summary>Finds master records by exact name (case/space-insensitive), then by code, then by partial name.</summary>
    private async Task<List<JsonObject>> FindAsync(string collection, string nameField, string text, JsonObject projection,
        MqlValidationContext context, AppSettings settings, Run run, CancellationToken ct, string? comment, string[]? codeFields = null)
    {
        var value = text.Trim();
        // Keep the escaped, anchored pattern within the validator's $regex limit (MaxRegexLength, 200 characters).
        if (value.Length > 60) value = value[..60].TrimEnd();
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
        var exact = "^\\s*" + string.Join("\\s+", words) + "\\s*$";

        async Task<List<JsonObject>> Query(JsonObject match)
        {
            var r = await AggregateAsync(collection, new JsonArray
            {
                new JsonObject { ["$match"] = match },
                new JsonObject { ["$project"] = (JsonObject)projection.DeepClone() },
                new JsonObject { ["$limit"] = 6 }
            }, context, settings, run, ct, comment);
            return r.Rows;
        }

        var found = await Query(new JsonObject { [nameField] = new JsonObject { ["$regex"] = exact, ["$options"] = "i" } });
        if (found.Count > 0 || run.Error is not null || run.StoreMissing) return found;
        foreach (var code in codeFields ?? Array.Empty<string>())
        {
            found = await Query(new JsonObject { [code] = new JsonObject { ["$regex"] = exact, ["$options"] = "i" } });
            if (found.Count > 0 || run.Error is not null) return found;
        }
        return await Query(new JsonObject { [nameField] = new JsonObject { ["$regex"] = Regex.Escape(value), ["$options"] = "i" } });
    }

    private async Task<string?> UnitNameAsync(string? unitId, MqlValidationContext context, AppSettings settings, Run run, CancellationToken ct, string? comment)
    {
        if (unitId is null || !Regex.IsMatch(unitId, "^[0-9a-fA-F]{24}$") || !context.Collections.Any(c => c.Name == "Unit")) return null;
        var r = await AggregateAsync("Unit", new JsonArray
        {
            new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$oid"] = unitId.ToLowerInvariant() } } },
            new JsonObject { ["$project"] = new JsonObject { ["unitName"] = 1 } }
        }, context, settings, run, ct, comment, optional: true);
        return Str(r.Rows.FirstOrDefault()?["unitName"]);
    }

    private Task<EntityNames> LocationNamesAsync(List<string> ids, MqlValidationContext context, AppSettings settings, Run run,
        CancellationToken ct, string? comment)
        => EntityNamesAsync("warehouse", ids, context, settings, run, ct, comment);

    /// <summary>
    /// Names of entity ids through the central resolver (<see cref="EntityCatalog"/>): the verified master and name field,
    /// "No …" for empty / "0", "Unknown …" for ids without a record. Orphans are noted for the data-integrity log.
    /// </summary>
    private async Task<EntityNames> EntityNamesAsync(string entity, IEnumerable<string?> ids, MqlValidationContext context, AppSettings settings, Run run,
        CancellationToken ct, string? comment)
    {
        var names = await _engine.ResolveEntityNamesAsync(entity, ids, context.Collections, settings, ct, comment);
        if (names.Orphans.Count > 0)
            run.Integrity.Add($"{entity}: {names.Orphans.Count} id(s) without a master record [{string.Join(", ", names.Orphans.Take(10))}]");
        if (names.Failed)
            run.Integrity.Add($"{entity}: master could not be read");
        return names;
    }

    /// <summary>Runs a server-built pipeline through the normal validation, tenant scope, store filter and status filter.</summary>
    private async Task<ExecutedQuery> AggregateAsync(string collection, JsonArray pipeline, MqlValidationContext context, AppSettings settings,
        Run run, CancellationToken ct, string? comment, bool optional = false)
    {
        if (run.Error is not null || run.StoreMissing) return new ExecutedQuery();
        var prepared = _engine.Prepare(new MqlQuery { Type = "query", Operation = "aggregate", Collection = collection, Pipeline = pipeline }, context, settings);
        if (prepared.StoreContextMissing) { run.StoreMissing = true; return new ExecutedQuery(); }
        if (!prepared.IsExecutable)
        {
            if (!optional) run.Error = "The report could not be prepared: " + string.Join("; ", prepared.Validation.Errors);
            return new ExecutedQuery();
        }
        run.Queries.Add(prepared.Mql ?? string.Empty);
        var result = await _engine.ExecuteAsync(prepared, settings, ct, comment);
        run.Ms += result.ElapsedMs;
        return result;
    }

    private static ReportOutcome Clarify(string question, Run run, List<string>? options = null, string? answerArgument = null)
        => new() { Kind = "clarify", Message = question, Queries = run.Queries, Options = options, AnswerArgument = answerArgument };

    private static (DateOnly From, DateOnly To, string? Error) Period(JsonObject args, DateAnchors anchors)
    {
        DateOnly? Parse(string? s) => DateOnly.TryParseExact(s ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var from = Parse(Str(args["from"]));
        var to = Parse(Str(args["to"]));
        var today = anchors.LocalToday;
        var start = from ?? new DateOnly(today.Year, today.Month, 1);          // default: this month to date
        var end = to ?? today;
        if (end < start) return (start, end, "The end date of the period is before the start date.");
        return (start, end, null);
    }

    private static JsonObject Between(DateRange r) => new() { ["$gte"] = DateNode(r.StartUtc), ["$lt"] = DateNode(r.EndUtc) };
    private static JsonObject DateNode(DateTime utc) => new() { ["$date"] = DateAnchors.Iso(utc) };

    /// <summary>Date of a stored document as the business date (wall-clock storage: the date as written).</summary>
    private static string? DateText(JsonNode? node, DateAnchors anchors)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) return s;
        var local = anchors.WallClockStorage ? utc : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), DateAnchors.ResolveTimeZone(anchors.TimeZoneId));
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OPSTOCK"] = "Opening stock", ["PURCHASE"] = "Purchase", ["SALE"] = "Sale", ["SALE_RETURN"] = "Sales return",
        ["PURCHASE_RETURN"] = "Purchase return", ["DELIVERY_NOTE"] = "Delivery note", ["DELIVERY_NOTE_RECEIPT"] = "Delivery note receipt",
        ["STOCK_ADJUSTMENT"] = "Stock adjustment", ["STOCK_TRANSFER_INTERNAL"] = "Stock transfer", ["STOCK_TRANSFER_EXTERNAL"] = "Branch transfer",
        ["SERVICE"] = "Service", ["RECEIPT"] = "Receipt", ["PAYMENT"] = "Payment", ["JOURNAL"] = "Journal", ["CONTRA"] = "Contra",
        ["DEBIT_NOTE"] = "Debit note", ["CREDIT_NOTE"] = "Credit note"
    };

    private static string Label(string? type) => type is null ? "" : Labels.TryGetValue(type, out var l) ? l : type;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static decimal Num(JsonNode? node)
    {
        if (node is not JsonValue v) return 0m;
        if (v.TryGetValue<decimal>(out var m)) return m;
        if (v.TryGetValue<double>(out var d)) return (decimal)d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return 0m;
    }
}
