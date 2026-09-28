using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;

namespace MySale.AI.Tests;

// Business terminology: user language ("customer", "who owes us", "vendor ledger") → accounting concepts
// (Sundry Debtors / Sundry Creditors …) → the collections, group fields and STORED values of the database.

public class BusinessTermMappingTests
{
    private static readonly CollectionSchema Ledger = new()
    {
        Name = "Ledger",
        Fields = new()
        {
            new FieldSchema { Name = "_id", Type = "objectId" },
            new FieldSchema { Name = "CompanyId", Type = "objectId" },
            new FieldSchema { Name = "ledgerName", Type = "string" },
            new FieldSchema { Name = "groupName", Type = "string" },
            new FieldSchema { Name = "ledgerBalance", Type = "double" }
        }
    };

    private static IReadOnlyList<CollectionSchema> Schema => TestData.Schema().Append(Ledger).ToList();

    private static ConceptMapping Single(string question, string concept)
    {
        var s = BusinessTerms.Interpret(question, Schema);
        Assert.Null(s.Clarification);
        return Assert.Single(s.Mappings, m => m.Concept.Key == concept);
    }

    [Theory]
    [InlineData("Give me the customer list")]
    [InlineData("Show customers")]
    [InlineData("Customer ledger of Al Noor")]
    [InlineData("customer outstanding")]
    [InlineData("Customer balance")]
    [InlineData("Show receivables")]
    [InlineData("Debtor list")]
    [InlineData("Show debtors")]
    [InlineData("Customer dues")]
    [InlineData("Customer amount pending")]
    [InlineData("Who owes us money?")]
    [InlineData("Parties who need to pay us")]
    [InlineData("client list")]
    [InlineData("Show sundry debtors")]
    public void Customer_words_mean_receivable_parties_under_sundry_debtors(string question)
    {
        var m = Single(question, "customer");
        Assert.Contains("Sundry Debtors", m.Concept.AccountGroups);
        Assert.Equal("receivable", m.Concept.Side);
        Assert.DoesNotContain(BusinessTerms.Interpret(question, Schema).Mappings, x => x.Concept.Key == "vendor");
    }

    [Theory]
    [InlineData("Vendor list")]
    [InlineData("Show suppliers")]
    [InlineData("Supplier ledger")]
    [InlineData("Vendor outstanding")]
    [InlineData("Show payables")]
    [InlineData("Creditor list")]
    [InlineData("Supplier balance")]
    [InlineData("Amount we need to pay")]
    [InlineData("Who do we owe money to?")]
    [InlineData("Show sundry creditors")]
    public void Vendor_words_mean_payable_parties_under_sundry_creditors(string question)
    {
        var m = Single(question, "vendor");
        Assert.Contains("Sundry Creditors", m.Concept.AccountGroups);
        Assert.Equal("payable", m.Concept.Side);
        Assert.DoesNotContain(BusinessTerms.Interpret(question, Schema).Mappings, x => x.Concept.Key == "customer");
    }

    [Theory]
    [InlineData("Show customer ledger for Gulf Star", "ledger")]
    [InlineData("Show customer sales this month", "transactions")]
    [InlineData("Show customer outstanding", "outstanding")]
    [InlineData("Give me the customer list", "list")]
    [InlineData("Supplier balance", "outstanding")]
    [InlineData("Supplier purchases last month", "transactions")]
    public void The_same_entity_is_read_in_context(string question, string aspect)
    {
        var s = BusinessTerms.Interpret(question, Schema);
        var party = Assert.Single(s.Mappings, m => m.Concept.Key is "customer" or "vendor");
        Assert.Equal(aspect, party.Aspect);
        Assert.DoesNotContain(s.Mappings, m => m.Concept.Key is "sales" or "purchase"); // "customer sales" is about the customer
    }

    [Fact]
    public void Mapping_points_to_collections_and_group_fields_that_exist()
    {
        var m = Single("Give me the customer list", "customer");
        Assert.Contains("Customers", m.Collections);
        Assert.Contains("Ledger", m.Collections);
        Assert.Contains(new GroupFieldRef("Ledger", "groupName"), m.GroupFields);
        Assert.DoesNotContain("Parties", m.Collections); // hints that are not in the schema are ignored
    }

    [Theory]
    [InlineData("Show the cash balance", "cash")]
    [InlineData("Bank balance", "bank")]
    [InlineData("Total expenses this month", "expense")]
    [InlineData("Other income this year", "income")]
    [InlineData("Net profit this year", "profit")]
    [InlineData("Stock level of rice", "stock")]
    public void Other_business_terms_are_recognised(string question, string concept)
        => Assert.Contains(BusinessTerms.Interpret(question, Schema).Mappings, m => m.Concept.Key == concept);

    [Theory]
    [InlineData("Show party list")]
    [InlineData("list all parties")]
    public void An_undetermined_party_is_asked_back(string question)
    {
        var s = BusinessTerms.Interpret(question, Schema);
        Assert.NotNull(s.Clarification);
        Assert.Contains("customers", s.Clarification);
        Assert.Contains("suppliers", s.Clarification);
    }

    [Theory]
    [InlineData("What's the weather in Dubai?")]
    [InlineData("hello")]
    public void Unrelated_questions_map_to_nothing(string question) => Assert.True(BusinessTerms.Interpret(question, Schema).IsEmpty);

    [Fact]
    public void Configuration_can_change_the_accounting_group()
    {
        var options = new BusinessTermOptions
        {
            Concepts = { ["customer"] = new BusinessConceptOptions { AccountGroups = new() { "Trade Receivables" } } }
        };
        var m = Assert.Single(BusinessTerms.Interpret("customer list", Schema, options).Mappings);
        Assert.Equal(new[] { "Trade Receivables" }, m.Concept.AccountGroups);
        Assert.Contains("client", m.Concept.Synonyms); // everything else is kept
    }

    [Theory]
    [InlineData("Sundry Debtors", "SUNDRY DEBTORS", true)]
    [InlineData("Sundry Debtors", " Sundry  Debtor ", true)]
    [InlineData("Sundry Debtors", "sundry debtors", true)]
    [InlineData("Sundry Debtors", "Sundry Debtors (Local)", false)]
    [InlineData("Sundry Debtors", "Sundry Creditors", false)]
    [InlineData("Cash-in-Hand", "Cash in Hand", true)]
    [InlineData("Bank OD A/c", "BANK OD A/C", true)]
    public void Group_patterns_match_the_stored_spelling_only(string group, string stored, bool matches)
        => Assert.Equal(matches, Regex.IsMatch(stored, BusinessTerms.GroupPattern(group), RegexOptions.IgnoreCase));
}

public class BusinessTermPromptTests
{
    [Fact]
    public void Query_prompt_lists_the_mapping_and_the_stored_values()
    {
        var ledger = new CollectionSchema { Name = "Ledger", Fields = { new FieldSchema { Name = "groupName", Type = "string" }, new FieldSchema { Name = "ledgerName", Type = "string" } } };
        var schema = TestData.Schema().Append(ledger).ToList();
        var semantics = BusinessTerms.Interpret("Give me the customer list", schema);
        semantics.Mappings[0].StoredGroupValues[new GroupFieldRef("Ledger", "groupName")] = new() { "SUNDRY DEBTORS" };

        var ctx = new PromptContext("Al Noor", "AED", "Asia/Dubai", DateAnchors.Compute(DateTime.UtcNow, "Asia/Dubai"), 200, "CompanyId") { Semantics = semantics };
        var system = new PromptBuilder().BuildQuerySystemPrompt(ctx, schema);

        Assert.Contains("Business terms in the question", system);
        Assert.Contains("\"customer\" = customers (receivable parties); accounting group: \"Sundry Debtors\"; requested: a list.", system);
        Assert.Contains("Stored in Ledger.groupName as: \"SUNDRY DEBTORS\"", system);
        Assert.Contains("never search for the user's literal word", system);
        Assert.Contains("\"customer ledger\"", system); // context rule 22
    }

    [Fact]
    public void Answer_prompt_keeps_the_users_words()
    {
        var semantics = BusinessTerms.Interpret("Show vendor outstanding", TestData.Schema());
        var ctx = new PromptContext("Al Noor", "AED", "Asia/Dubai", DateAnchors.Compute(DateTime.UtcNow, "Asia/Dubai"), 200, "CompanyId") { Semantics = semantics };
        var system = new PromptBuilder().BuildAnswerMessages(ctx, "Show vendor outstanding", null, new List<JsonObject>(), false, 50)[0].Content;
        Assert.Contains("\"vendor\"", system);
        Assert.Contains("only if it helps explain the result", system);
    }

    [Theory]
    [InlineData("Who owes us money?", "There are no customers with outstanding balances matching your criteria.")]
    [InlineData("Show debtors outstanding", "There are no customers with outstanding balances matching your criteria.")]
    [InlineData("Who do we owe money to?", "There are no suppliers with outstanding balances matching your criteria.")]
    [InlineData("Show payables", "There are no suppliers with outstanding balances matching your criteria.")]
    [InlineData("Show vendor list", "No suppliers matched your criteria.")]
    [InlineData("Creditor list", "No suppliers matched your criteria.")]
    [InlineData("Debtor list", "No customers matched your criteria.")]
    public void Zero_results_are_described_in_business_words(string question, string expected)
        => Assert.Equal(expected, ZeroResultPolicy.Describe(question, null, null, null, "AED").Message);
}

public class BusinessTermOrchestratorTests
{
    private static readonly CollectionSchema Ledger = new()
    {
        Name = "Ledger",
        Description = "Account ledgers with their accounting group",
        Fields = new()
        {
            new FieldSchema { Name = "_id", Type = "objectId" },
            new FieldSchema { Name = "CompanyId", Type = "objectId" },
            new FieldSchema { Name = "ledgerName", Type = "string" },
            new FieldSchema { Name = "groupName", Type = "string" },
            new FieldSchema { Name = "ledgerBalance", Type = "double" }
        }
    };

    private static Harness WithLedger()
    {
        var h = new Harness();
        h.Schema.Extra.Add(Ledger);
        h.Store.Settings.Query.AllowedCollections.Add("Ledger");   // only allowed collections reach the AI and the validator
        h.Executor.Handler = (collection, pipeline) =>
        {
            var json = pipeline.ToJsonString();
            if (collection == "Ledger" && json.Contains("\"$group\"") && json.Contains("$regex"))
                return new List<JsonObject> { new() { ["_id"] = "SUNDRY DEBTORS" } };           // stored spelling
            if (collection == "Ledger" && json.Contains("SUNDRY DEBTORS"))
                return new List<JsonObject>
                {
                    new() { ["ledgerName"] = "Al Noor Trading", ["ledgerBalance"] = 1200.0 },
                    new() { ["ledgerName"] = "Gulf Star Supplies", ["ledgerBalance"] = 800.0 }
                };
            return new List<JsonObject>();
        };
        return h;
    }

    private const string CustomersQuery = """{"type":"query","operation":"find","collection":"Customers","filter":{},"limit":20,"explanation":"Customers","visualization":"table"}""";
    private const string DebtorsQuery = """
        {"type":"query","operation":"aggregate","collection":"Ledger","pipeline":[
          {"$match":{"groupName":"SUNDRY DEBTORS"}},
          {"$project":{"_id":0,"ledgerName":1,"ledgerBalance":1}},
          {"$sort":{"ledgerBalance":-1}}],"explanation":"Customers (Sundry Debtors ledgers)","visualization":"table"}
        """;

    [Fact]
    public async Task Stored_group_values_are_read_before_the_query_is_generated()
    {
        var h = WithLedger();
        h.Provider.QueryResponses.Enqueue(_ => DebtorsQuery);

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Give me the customer list" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        var system = h.Provider.Requests[0].Messages[0].Content;
        Assert.Contains("Stored in Ledger.groupName as: \"SUNDRY DEBTORS\"", system);
        Assert.Equal(2, r.Data!.Count);
    }

    [Fact]
    public async Task An_empty_literal_customer_query_is_rechecked_against_sundry_debtors()
    {
        var h = WithLedger();
        h.Provider.QueryResponses.Enqueue(_ => CustomersQuery);   // 1st: literal "customers" → nothing
        h.Provider.QueryResponses.Enqueue(req =>                  // 2nd: re-check → Sundry Debtors ledgers
        {
            Assert.Contains("returned 0 records", req.Messages[^1].Content);
            Assert.Contains("Sundry Debtors", req.Messages[^1].Content);
            return DebtorsQuery;
        });
        h.Provider.Answer = req =>
        {
            Assert.Contains("Al Noor Trading", req.Messages[^1].Content);
            return "Here are your customers: Al Noor Trading and Gulf Star Supplies.";
        };

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Give me the customer list" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        Assert.Equal(2, r.Data!.Count);
        Assert.StartsWith("Here are your customers", r.Answer);
        Assert.Equal(3, h.Provider.Requests.Count);              // query, re-check, answer
        var log = Assert.Single(h.Store.Logs);
        Assert.Equal("Ledger", log.Collection);
        Assert.Contains("SUNDRY DEBTORS", log.FinalMql);
    }

    [Fact]
    public async Task No_records_is_concluded_only_after_the_recheck()
    {
        var h = WithLedger();
        h.Executor.Handler = (collection, pipeline) =>
            collection == "Ledger" && pipeline.ToJsonString().Contains("\"$group\"")
                ? new List<JsonObject> { new() { ["_id"] = "SUNDRY DEBTORS" } }
                : new List<JsonObject>();
        h.Provider.QueryResponses.Enqueue(_ => DebtorsQuery);
        h.Provider.QueryResponses.Enqueue(_ => DebtorsQuery);     // re-check: mapping was already right

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Give me the customer list" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.NoResults, r.Status);
        Assert.Equal("No customers matched your criteria.", r.Answer);
        Assert.Equal(2, h.Provider.Requests.Count);              // query + re-check, no answer call
    }

    [Fact]
    public async Task Sales_questions_do_not_trigger_a_recheck()
    {
        var h = new Harness();
        h.Executor.Handler = (_, _) => new List<JsonObject>();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"query","operation":"find","collection":"Sales","filter":{},"limit":20}""");
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Show sales invoices" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.NoResults, r.Status);
        Assert.Single(h.Provider.Requests);
    }

    [Fact]
    public async Task An_ambiguous_party_is_asked_back_without_calling_the_model()
    {
        var h = new Harness();
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Show party list" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Unsupported, r.Status);
        Assert.Contains("customers", r.Answer);
        Assert.Empty(h.Provider.Requests);
        Assert.Null(h.Executor.LastCall);
    }

    [Fact]
    public async Task The_model_can_ask_a_short_business_question()
    {
        var h = new Harness();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Do you mean the sales target or the purchase budget?"}""");
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Show the plan figures" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Unsupported, r.Status);
        Assert.Equal("Do you mean the sales target or the purchase budget?", r.Answer);
        Assert.Null(h.Executor.LastCall);
    }

    [Theory]
    [InlineData("Which groupName in Ledger do you mean?")]
    [InlineData("Filter {\"$match\":1}?")]
    [InlineData("Do you mean SaleItems or PurchaseItems?")]
    public async Task Technical_clarifications_are_replaced(string question)
    {
        var h = new Harness();
        h.Provider.QueryResponses.Enqueue(_ => new JsonObject { ["type"] = "clarify", ["question"] = question }.ToJsonString());
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Show the plan figures" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Unsupported, r.Status);
        Assert.DoesNotContain("groupName", r.Answer);
        Assert.DoesNotContain("$match", r.Answer);
        Assert.DoesNotContain("SaleItems", r.Answer);
        Assert.StartsWith("Could you tell me a little more", r.Answer);
    }
}
