using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Infrastructure.Seeding;

/// <summary>
/// Generates a realistic, deterministic ERP dataset: 3 companies, 5 branches, 100 customers, 500 items,
/// 1,200 sales invoices with lines, purchases and payments. Dates run up to "today" so relative questions work.
/// </summary>
public sealed class SampleDataSeeder
{
    public static readonly string[] Collections =
        { "Companies", "Branches", "Users", "Customers", "Items", "Sales", "SaleItems", "Purchases", "PurchaseItems", "Payments" };

    private sealed record CompanyDef(
        ObjectId Id, string Code, string Letter, string Name, string Currency, string TimeZone, string Country, string Trn,
        double VatRate, int Customers, int Items, int Sales, int Purchases, string Catalog,
        (string Name, string City)[] Branches, string[] Staff, string[] Suppliers);

    public static readonly ObjectId CompanyA = ObjectId.Parse("66a000000000000000000001");
    public static readonly ObjectId CompanyB = ObjectId.Parse("66a000000000000000000002");
    public static readonly ObjectId CompanyC = ObjectId.Parse("66a000000000000000000003");

    private static readonly CompanyDef[] Companies =
    {
        new(CompanyA, "CMP-A", "A", "Al Noor Trading LLC", "AED", "Asia/Dubai", "United Arab Emirates", "100234567800003",
            0.05, 40, 200, 500, 100, "trading",
            new[] { ("Dubai - Deira", "Dubai"), ("Sharjah - Al Nahda", "Sharjah") },
            new[] { "Imran Qureshi", "Maria Santos", "Khalid Hassan" },
            new[] { "Emirates Building Supplies", "Gulf Hardware Wholesale", "Jebel Ali Tools Trading", "Desert Paints Distribution" }),
        new(CompanyB, "CMP-B", "B", "Gulf Star Electronics", "AED", "Asia/Dubai", "United Arab Emirates", "100987654300003",
            0.05, 35, 180, 400, 80, "electronics",
            new[] { ("Abu Dhabi - Khalidiya", "Abu Dhabi"), ("Al Ain - Town Centre", "Al Ain") },
            new[] { "Arjun Nair", "Sara Al Hammadi", "Tom Wilson" },
            new[] { "Techline Distribution FZE", "Digital Wave Wholesale", "Prime Gadgets Import", "Smart Home Supply Co" }),
        new(CompanyC, "CMP-C", "C", "Desert Rose Supermarket", "SAR", "Asia/Riyadh", "Saudi Arabia", "310123456700003",
            0.15, 25, 120, 300, 60, "grocery",
            new[] { ("Riyadh - Olaya", "Riyadh") },
            new[] { "Faisal Al Otaibi", "Noura Al Qahtani", "Ali Raza" },
            new[] { "Najd Fresh Foods", "Red Sea Beverages", "Kingdom Dairy Supply", "Arabian Household Goods" })
    };

    private static readonly Dictionary<string, (string Category, string[] Bases, double Min, double Max, string Unit)[]> Catalogs = new()
    {
        ["trading"] = new[]
        {
            ("Hardware", new[] { "Hex Bolt Set", "Door Hinge", "Padlock", "Wall Anchor Pack", "Steel Chain", "Cabinet Handle" }, 5.0, 120.0, "PCS"),
            ("Power Tools", new[] { "Cordless Drill", "Angle Grinder", "Impact Driver", "Circular Saw", "Jigsaw", "Rotary Hammer" }, 180.0, 1450.0, "PCS"),
            ("Hand Tools", new[] { "Claw Hammer", "Screwdriver Set", "Adjustable Wrench", "Measuring Tape", "Utility Knife", "Plier Set" }, 12.0, 160.0, "PCS"),
            ("Paints", new[] { "Interior Emulsion 18L", "Exterior Paint 18L", "Wood Varnish 4L", "Primer 4L", "Enamel Paint 1L", "Spray Paint" }, 15.0, 420.0, "TIN"),
            ("Electrical", new[] { "LED Panel 60x60", "Extension Socket", "Circuit Breaker", "Copper Cable 100m", "Switch Plate", "LED Bulb Pack" }, 8.0, 650.0, "PCS"),
            ("Plumbing", new[] { "PVC Pipe 3m", "Ball Valve", "Water Pump", "Kitchen Mixer", "Shower Set", "Pipe Fitting Kit" }, 10.0, 900.0, "PCS"),
            ("Safety", new[] { "Safety Helmet", "Hi-Vis Vest", "Safety Gloves", "Safety Shoes", "Ear Muffs", "Safety Goggles" }, 6.0, 220.0, "PCS")
        },
        ["electronics"] = new[]
        {
            ("Mobiles", new[] { "Smartphone 128GB", "Smartphone 256GB", "Smartphone Pro 256GB", "Feature Phone", "Smartphone Lite 64GB" }, 250.0, 5200.0, "PCS"),
            ("Laptops", new[] { "Laptop 14\" i5", "Laptop 15\" i7", "Ultrabook 13\"", "Gaming Laptop 16\"", "Chromebook 11\"" }, 900.0, 7800.0, "PCS"),
            ("Accessories", new[] { "USB-C Charger 65W", "Power Bank 20000mAh", "Phone Case", "HDMI Cable 2m", "Wireless Mouse", "Keyboard" }, 15.0, 260.0, "PCS"),
            ("Audio", new[] { "Wireless Earbuds", "Bluetooth Speaker", "Over-Ear Headphones", "Soundbar", "Party Speaker" }, 80.0, 1900.0, "PCS"),
            ("TV & Video", new[] { "Smart TV 43\"", "Smart TV 55\"", "Smart TV 65\"", "Streaming Stick", "Projector" }, 150.0, 6500.0, "PCS"),
            ("Home Appliances", new[] { "Air Fryer", "Microwave Oven", "Vacuum Cleaner", "Coffee Machine", "Air Purifier", "Steam Iron" }, 90.0, 2400.0, "PCS")
        },
        ["grocery"] = new[]
        {
            ("Beverages", new[] { "Orange Juice 1L", "Mineral Water 12x500ml", "Cola 6x330ml", "Green Tea 25 Bags", "Instant Coffee 200g" }, 3.0, 45.0, "PCS"),
            ("Dairy", new[] { "Fresh Milk 2L", "Laban 1L", "Greek Yogurt 500g", "Cheddar Cheese 400g", "Butter 400g" }, 4.0, 38.0, "PCS"),
            ("Bakery", new[] { "Arabic Bread", "Croissant 6pk", "Sliced Bread", "Date Cookies", "Brown Bread" }, 2.0, 22.0, "PCS"),
            ("Rice & Grains", new[] { "Basmati Rice 5kg", "Sella Rice 10kg", "Red Lentils 1kg", "Bulgur 1kg", "Oats 1kg" }, 6.0, 95.0, "BAG"),
            ("Snacks", new[] { "Potato Chips 150g", "Mixed Nuts 500g", "Chocolate Bar", "Dates 1kg", "Biscuits Pack" }, 2.0, 60.0, "PCS"),
            ("Cleaning", new[] { "Dishwashing Liquid 1L", "Laundry Detergent 3kg", "Surface Cleaner", "Tissue Box 10pk", "Garbage Bags" }, 5.0, 70.0, "PCS")
        }
    };

    private static readonly Dictionary<string, string[]> Brands = new()
    {
        ["trading"] = new[] { "Bosch", "Makita", "Stanley", "Jotun", "Schneider", "Legrand", "3M", "Pedrollo", "Ingco", "Total" },
        ["electronics"] = new[] { "Samsung", "Apple", "Lenovo", "HP", "Sony", "LG", "Anker", "Xiaomi", "JBL", "Philips" },
        ["grocery"] = new[] { "Almarai", "Al Rawabi", "Nadec", "Lipton", "Nescafe", "Tiffany", "Americana", "Al Wadi", "Fairy", "Sunbulah" }
    };

    private static readonly string[] FirstNames =
        { "Ahmed", "Fatima", "Mohammed", "Aisha", "Rahul", "Priya", "John", "Sarah", "Omar", "Layla", "Ravi", "Anjali", "Yousef", "Mariam", "David", "Hessa", "Suresh", "Noor", "Karim", "Elena" };

    private static readonly string[] LastNames =
        { "Al Mansoori", "Khan", "Al Suwaidi", "Menon", "Sharma", "Smith", "Al Zaabi", "Haddad", "Pillai", "Al Harthy", "Fernandes", "Qasim", "Rahman", "Ivanova", "Al Ketbi" };

    private static readonly string[] Businesses =
        { "Blue Horizon Contracting", "Palm Coast Builders", "Oasis Facility Services", "Crescent Interiors", "Golden Sands Hospitality",
          "Summit Engineering", "Falcon Logistics", "Pearl Retail Group", "Silverline Trading", "Harbor View Cafe", "Atlas Office Solutions",
          "Green Leaf Restaurants", "Sunrise Clinics", "Metro Maintenance", "Al Waha Catering" };

    private static readonly string[] CitiesUae = { "Dubai", "Sharjah", "Ajman", "Abu Dhabi", "Al Ain", "Ras Al Khaimah" };
    private static readonly string[] CitiesKsa = { "Riyadh", "Jeddah", "Dammam", "Khobar", "Makkah" };

    private readonly TimeProvider _time;

    public SampleDataSeeder(TimeProvider time) => _time = time;

    public async Task<bool> HasDataAsync(IMongoDatabase db, CancellationToken ct)
        => await db.GetCollection<BsonDocument>("Sales").EstimatedDocumentCountAsync(cancellationToken: ct) > 0;

    public async Task<SeedResult> SeedAsync(IMongoDatabase db, bool reset, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!reset && await HasDataAsync(db, ct))
            return new SeedResult { Collections = await CountsAsync(db, ct), ElapsedMs = sw.ElapsedMilliseconds };

        foreach (var name in Collections)
            await db.DropCollectionAsync(name, ct);

        var rng = new Random(42);
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var data = Collections.ToDictionary(c => c, _ => new List<BsonDocument>());

        foreach (var company in Companies)
            GenerateCompany(company, rng, nowUtc, data);

        foreach (var (name, docs) in data)
        {
            if (docs.Count == 0) continue;
            var coll = db.GetCollection<BsonDocument>(name);
            foreach (var batch in docs.Chunk(1000))
                await coll.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct);
        }

        await CreateIndexesAsync(db, ct);
        sw.Stop();
        return new SeedResult { Collections = data.ToDictionary(kv => kv.Key, kv => (long)kv.Value.Count), ElapsedMs = sw.ElapsedMilliseconds };
    }

    private static async Task<Dictionary<string, long>> CountsAsync(IMongoDatabase db, CancellationToken ct)
    {
        var result = new Dictionary<string, long>();
        foreach (var name in Collections)
            result[name] = await db.GetCollection<BsonDocument>(name).EstimatedDocumentCountAsync(cancellationToken: ct);
        return result;
    }

    private static async Task CreateIndexesAsync(IMongoDatabase db, CancellationToken ct)
    {
        async Task Index(string collection, BsonDocument keys)
            => await db.GetCollection<BsonDocument>(collection).Indexes
                .CreateOneAsync(new CreateIndexModel<BsonDocument>(keys), cancellationToken: ct);

        await Index("Sales", new BsonDocument { { "CompanyId", 1 }, { "InvoiceDate", -1 } });
        await Index("Sales", new BsonDocument { { "CompanyId", 1 }, { "CustomerId", 1 } });
        await Index("SaleItems", new BsonDocument { { "CompanyId", 1 }, { "InvoiceDate", -1 }, { "ItemId", 1 } });
        await Index("Customers", new BsonDocument { { "CompanyId", 1 }, { "CustomerName", 1 } });
        await Index("Items", new BsonDocument { { "CompanyId", 1 }, { "ItemCode", 1 } });
        await Index("Purchases", new BsonDocument { { "CompanyId", 1 }, { "PurchaseDate", -1 } });
        await Index("PurchaseItems", new BsonDocument { { "CompanyId", 1 }, { "PurchaseDate", -1 } });
        await Index("Payments", new BsonDocument { { "CompanyId", 1 }, { "PaymentDate", -1 } });
    }

    // ------------------------------------------------------------------ generation

    private sealed class Customer
    {
        public ObjectId Id;
        public string Name = "";
        public double Balance;
        public DateTime? LastPurchase;
        public bool Dormant;
        public BsonDocument Doc = new();
    }

    private sealed class Item
    {
        public ObjectId Id;
        public string Code = "";
        public string Name = "";
        public string Category = "";
        public double Price;
        public double Cost;
        public BsonDocument Doc = new();
    }

    private void GenerateCompany(CompanyDef c, Random rng, DateTime nowUtc, Dictionary<string, List<BsonDocument>> data)
    {
        var tz = ResolveTz(c.TimeZone);
        var created = nowUtc.AddYears(-3);

        data["Companies"].Add(new BsonDocument
        {
            { "_id", c.Id }, { "CompanyId", c.Id }, { "CompanyCode", c.Code }, { "CompanyName", c.Name },
            { "Currency", c.Currency }, { "TimeZone", c.TimeZone }, { "Country", c.Country }, { "TRN", c.Trn },
            { "CreatedAt", created }
        });

        // Branches
        var branches = new List<(ObjectId Id, string Name)>();
        for (int i = 0; i < c.Branches.Length; i++)
        {
            var id = ObjectId.GenerateNewId();
            branches.Add((id, c.Branches[i].Name));
            data["Branches"].Add(new BsonDocument
            {
                { "_id", id }, { "CompanyId", c.Id }, { "BranchCode", $"BR-{c.Letter}-{i + 1:00}" },
                { "BranchName", c.Branches[i].Name }, { "City", c.Branches[i].City }, { "IsActive", true },
                { "OpenedOn", created.AddDays(i * 120) }
            });
        }

        // Business users
        for (int i = 0; i < c.Staff.Length; i++)
        {
            data["Users"].Add(new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "CompanyId", c.Id },
                { "UserName", c.Staff[i].Split(' ')[0].ToLowerInvariant() + "." + c.Letter.ToLowerInvariant() },
                { "FullName", c.Staff[i] }, { "Role", i == 0 ? "Manager" : "Cashier" },
                { "BranchId", branches[i % branches.Count].Id }, { "IsActive", true },
                { "LastLoginAt", nowUtc.AddHours(-rng.Next(1, 72)) }
            });
        }

        // Customers (first one is the walk-in cash customer)
        var cities = c.Country == "Saudi Arabia" ? CitiesKsa : CitiesUae;
        var customers = new List<Customer>();
        for (int i = 0; i < c.Customers; i++)
        {
            string name, type;
            if (i == 0) { name = "Cash Customer"; type = "Retail"; }
            else if (i % 4 == 0) { name = Businesses[(i / 4 + c.Letter[0]) % Businesses.Length] + (i > 40 ? " " + (i / 10) : string.Empty); type = i % 8 == 0 ? "Corporate" : "Wholesale"; }
            else { name = $"{FirstNames[(i * 7 + c.Letter[0]) % FirstNames.Length]} {LastNames[(i * 3 + c.Letter[0]) % LastNames.Length]}"; type = "Retail"; }

            var cust = new Customer { Id = ObjectId.GenerateNewId(), Name = name, Dormant = i > 0 && i % 9 == 0 };
            cust.Doc = new BsonDocument
            {
                { "_id", cust.Id }, { "CompanyId", c.Id }, { "CustomerCode", $"CUS-{c.Letter}-{i + 1:0000}" },
                { "CustomerName", name },
                { "Phone", (c.Country == "Saudi Arabia" ? "+966 5" : "+971 5") + rng.Next(0, 9) + " " + rng.Next(100, 999) + " " + rng.Next(1000, 9999) },
                { "Email", i == 0 ? BsonNull.Value : (BsonValue)$"{name.ToLowerInvariant().Replace(' ', '.')}@example.com" },
                { "City", cities[rng.Next(cities.Length)] }, { "CustomerType", type },
                { "CreditLimit", type == "Retail" ? 5000.0 : type == "Wholesale" ? 50000.0 : 150000.0 },
                { "Balance", 0.0 }, { "CreatedAt", created.AddDays(rng.Next(0, 700)) },
                { "LastPurchaseDate", BsonNull.Value }, { "IsActive", !(i > 0 && i % 23 == 0) }
            };
            customers.Add(cust);
        }

        // Items
        var catalog = Catalogs[c.Catalog];
        var brands = Brands[c.Catalog];
        var items = new List<Item>();
        var names = new HashSet<string>();
        for (int i = 0; items.Count < c.Items; i++)
        {
            var cat = catalog[i % catalog.Length];
            var baseName = cat.Bases[(i / catalog.Length) % cat.Bases.Length];
            var brand = brands[(i / (catalog.Length * cat.Bases.Length) + i) % brands.Length];
            var name = $"{brand} {baseName}";
            if (names.Contains(name)) name = $"{brand} {baseName} {(char)('A' + i % 26)}{i % 7 + 1}";
            if (!names.Add(name))
            {
                if (i > c.Items * 20) break;
                continue;
            }

            var price = Math.Round(cat.Min + rng.NextDouble() * (cat.Max - cat.Min), 2);
            var cost = Math.Round(price * (0.62 + rng.NextDouble() * 0.2), 2);
            var reorder = rng.Next(5, 40);
            var roll = rng.NextDouble();
            var stock = roll < 0.05 ? 0 : roll < 0.14 ? rng.Next(1, reorder + 1) : rng.Next(reorder + 5, 400);

            var item = new Item { Id = ObjectId.GenerateNewId(), Code = $"ITM-{c.Letter}-{items.Count + 1:0000}", Name = name, Category = cat.Category, Price = price, Cost = cost };
            item.Doc = new BsonDocument
            {
                { "_id", item.Id }, { "CompanyId", c.Id }, { "ItemCode", item.Code }, { "ItemName", name },
                { "Category", cat.Category }, { "Brand", brand }, { "Unit", cat.Unit },
                { "SellingPrice", price }, { "PurchasePrice", cost }, { "Stock", stock }, { "ReorderLevel", reorder },
                { "Barcode", "62" + rng.NextInt64(10_000_000_000, 99_999_999_999).ToString() },
                { "IsActive", rng.NextDouble() > 0.03 }, { "CreatedAt", created.AddDays(rng.Next(0, 900)) }
            };
            items.Add(item);
        }
        // Popular items get more sales (skewed distribution)
        var popularity = items.Select((_, idx) => 1.0 / Math.Pow(idx % 60 + 1, 0.7) + rng.NextDouble() * 0.05).ToArray();
        var popularityTotal = popularity.Sum();

        Item PickItem()
        {
            var r = rng.NextDouble() * popularityTotal;
            for (int k = 0; k < items.Count; k++)
            {
                r -= popularity[k];
                if (r <= 0) return items[k];
            }
            return items[^1];
        }

        // Sales invoices
        var invoiceCounter = 0;
        var paymentCounter = 0;
        for (int s = 0; s < c.Sales; s++)
        {
            // First few invoices land today / yesterday so "today" questions always have data.
            int daysAgo = s < 6 ? 0 : s < 10 ? 1 : (int)Math.Floor(Math.Pow(rng.NextDouble(), 1.35) * 395);
            var localDay = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date.AddDays(-daysAgo);
            var localTime = localDay.AddHours(9 + rng.Next(0, 13)).AddMinutes(rng.Next(0, 60));
            var invoiceDate = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), tz);
            if (invoiceDate > nowUtc) invoiceDate = nowUtc.AddMinutes(-rng.Next(5, 180));

            var modeRoll = rng.NextDouble();
            var paymentMode = modeRoll < 0.45 ? "Cash" : modeRoll < 0.80 ? "Card" : modeRoll < 0.95 ? "Credit" : "BankTransfer";

            Customer customer;
            if (paymentMode == "Cash" && rng.NextDouble() < 0.35) customer = customers[0];
            else
            {
                do { customer = customers[1 + (int)(Math.Pow(rng.NextDouble(), 1.6) * (customers.Count - 1))]; }
                while (customer.Dormant && daysAgo < 120 && rng.NextDouble() < 0.97);
            }
            if (paymentMode == "Credit" && customer == customers[0]) paymentMode = "Cash";

            var branch = branches[rng.Next(branches.Count)];
            var saleId = ObjectId.GenerateNewId();
            var invoiceNo = $"INV-{c.Letter}-{++invoiceCounter:000000}";
            var lines = rng.Next(1, 7);
            double gross = 0, discount = 0, tax = 0;
            var used = new HashSet<ObjectId>();
            var lineDocs = new List<BsonDocument>();
            for (int l = 0; l < lines; l++)
            {
                var item = PickItem();
                if (!used.Add(item.Id)) continue;
                var qty = c.Catalog == "electronics" ? rng.Next(1, 3) : c.Catalog == "grocery" ? rng.Next(1, 12) : rng.Next(1, 10);
                if (customer.Doc["CustomerType"].AsString != "Retail") qty *= rng.Next(2, 6);
                var rate = Math.Round(item.Price * (0.97 + rng.NextDouble() * 0.06), 2);
                var lineGross = Math.Round(qty * rate, 2);
                var lineDiscount = rng.NextDouble() < 0.2 ? Math.Round(lineGross * rng.Next(2, 11) / 100.0, 2) : 0;
                var lineTax = Math.Round((lineGross - lineDiscount) * c.VatRate, 2);
                var cost = Math.Round(qty * item.Cost, 2);
                gross += lineGross; discount += lineDiscount; tax += lineTax;
                lineDocs.Add(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "CompanyId", c.Id }, { "SaleId", saleId }, { "InvoiceNo", invoiceNo },
                    { "InvoiceDate", invoiceDate }, { "BranchId", branch.Id }, { "CustomerId", customer.Id },
                    { "ItemId", item.Id }, { "ItemCode", item.Code }, { "ItemName", item.Name }, { "Category", item.Category },
                    { "Quantity", (double)qty }, { "Rate", rate }, { "Discount", lineDiscount }, { "Tax", lineTax },
                    { "LineTotal", Math.Round(lineGross - lineDiscount + lineTax, 2) }, { "CostAmount", cost },
                    { "Profit", Math.Round(lineGross - lineDiscount - cost, 2) }
                });
            }
            gross = Math.Round(gross, 2); discount = Math.Round(discount, 2); tax = Math.Round(tax, 2);
            var net = Math.Round(gross - discount + tax, 2);

            string status;
            double paid;
            if (rng.NextDouble() < 0.02) { status = "Cancelled"; paid = 0; }
            else if (paymentMode is "Cash" or "Card" or "BankTransfer") { status = "Paid"; paid = net; }
            else
            {
                var r = rng.NextDouble();
                var old = daysAgo > 60;
                if (r < (old ? 0.70 : 0.30)) { status = "Paid"; paid = net; }
                else if (r < (old ? 0.90 : 0.60)) { status = "Partial"; paid = Math.Round(net * (0.3 + rng.NextDouble() * 0.5), 2); }
                else { status = "Unpaid"; paid = 0; }
            }
            var balance = status == "Cancelled" ? 0 : Math.Round(net - paid, 2);

            data["Sales"].Add(new BsonDocument
            {
                { "_id", saleId }, { "CompanyId", c.Id }, { "InvoiceNo", invoiceNo }, { "InvoiceDate", invoiceDate },
                { "CustomerId", customer.Id }, { "CustomerName", customer.Name }, { "BranchId", branch.Id }, { "BranchName", branch.Name },
                { "SalesPerson", c.Staff[rng.Next(c.Staff.Length)] }, { "PaymentMode", paymentMode }, { "ItemCount", lineDocs.Count },
                { "GrossAmount", gross }, { "Discount", discount }, { "Tax", tax }, { "NetAmount", net },
                { "PaidAmount", paid }, { "BalanceAmount", balance }, { "Status", status }, { "Currency", c.Currency }
            });
            data["SaleItems"].AddRange(lineDocs);

            if (status != "Cancelled")
            {
                customer.Balance += balance;
                if (customer.LastPurchase is null || invoiceDate > customer.LastPurchase) customer.LastPurchase = invoiceDate;
            }

            if (paid > 0)
            {
                var paymentDate = paymentMode == "Credit" ? invoiceDate.AddDays(rng.Next(3, 45)) : invoiceDate;
                if (paymentDate > nowUtc) paymentDate = nowUtc.AddHours(-rng.Next(1, 24));
                var mode = paymentMode == "Credit" ? (rng.NextDouble() < 0.6 ? "BankTransfer" : rng.NextDouble() < 0.5 ? "Cheque" : "Cash") : paymentMode;
                data["Payments"].Add(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "CompanyId", c.Id }, { "PaymentNo", $"RCT-{c.Letter}-{++paymentCounter:000000}" },
                    { "PaymentDate", paymentDate }, { "PaymentType", "Receipt" }, { "PartyType", "Customer" },
                    { "PartyId", customer.Id }, { "PartyName", customer.Name }, { "InvoiceNo", invoiceNo },
                    { "Amount", paid }, { "Mode", mode }, { "BranchId", branch.Id }
                });
            }
        }

        foreach (var cust in customers)
        {
            cust.Doc["Balance"] = Math.Round(cust.Balance, 2);
            cust.Doc["LastPurchaseDate"] = cust.LastPurchase.HasValue ? new BsonDateTime(cust.LastPurchase.Value) : BsonNull.Value;
            data["Customers"].Add(cust.Doc);
        }
        data["Items"].AddRange(items.Select(i => i.Doc));

        // Purchases
        var purchaseCounter = 0;
        for (int p = 0; p < c.Purchases; p++)
        {
            var daysAgo = (int)(rng.NextDouble() * 395);
            var date = nowUtc.Date.AddDays(-daysAgo).AddHours(6 + rng.Next(0, 8));
            if (date > nowUtc) date = nowUtc.AddHours(-2);
            var branch = branches[rng.Next(branches.Count)];
            var supplier = c.Suppliers[rng.Next(c.Suppliers.Length)];
            var purchaseId = ObjectId.GenerateNewId();
            var purchaseNo = $"PUR-{c.Letter}-{++purchaseCounter:00000}";
            var lines = rng.Next(2, 9);
            double gross = 0, tax = 0;
            var used = new HashSet<ObjectId>();
            var lineDocs = new List<BsonDocument>();
            for (int l = 0; l < lines; l++)
            {
                var item = items[rng.Next(items.Count)];
                if (!used.Add(item.Id)) continue;
                var qty = (double)(c.Catalog == "electronics" ? rng.Next(5, 40) : rng.Next(10, 120));
                var lineGross = Math.Round(qty * item.Cost, 2);
                var lineTax = Math.Round(lineGross * c.VatRate, 2);
                gross += lineGross; tax += lineTax;
                lineDocs.Add(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "CompanyId", c.Id }, { "PurchaseId", purchaseId }, { "PurchaseNo", purchaseNo },
                    { "PurchaseDate", date }, { "ItemId", item.Id }, { "ItemCode", item.Code }, { "ItemName", item.Name },
                    { "Quantity", qty }, { "Rate", item.Cost }, { "Tax", lineTax }, { "LineTotal", Math.Round(lineGross + lineTax, 2) }
                });
            }
            gross = Math.Round(gross, 2); tax = Math.Round(tax, 2);
            var net = Math.Round(gross + tax, 2);
            var roll = rng.NextDouble();
            var (status, paid) = daysAgo > 45
                ? (roll < 0.85 ? ("Paid", net) : ("Partial", Math.Round(net * 0.5, 2)))
                : (roll < 0.4 ? ("Paid", net) : roll < 0.7 ? ("Partial", Math.Round(net * 0.4, 2)) : ("Unpaid", 0.0));

            data["Purchases"].Add(new BsonDocument
            {
                { "_id", purchaseId }, { "CompanyId", c.Id }, { "PurchaseNo", purchaseNo }, { "PurchaseDate", date },
                { "SupplierName", supplier }, { "BranchId", branch.Id }, { "BranchName", branch.Name }, { "ItemCount", lineDocs.Count },
                { "GrossAmount", gross }, { "Tax", tax }, { "NetAmount", net }, { "PaidAmount", paid },
                { "BalanceAmount", Math.Round(net - paid, 2) }, { "Status", status }
            });
            data["PurchaseItems"].AddRange(lineDocs);

            if (paid > 0)
            {
                var payDate = date.AddDays(rng.Next(0, 30));
                if (payDate > nowUtc) payDate = nowUtc.AddHours(-1);
                data["Payments"].Add(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "CompanyId", c.Id }, { "PaymentNo", $"PAY-{c.Letter}-{++paymentCounter:000000}" },
                    { "PaymentDate", payDate }, { "PaymentType", "Payment" }, { "PartyType", "Supplier" },
                    { "PartyId", BsonNull.Value }, { "PartyName", supplier }, { "InvoiceNo", purchaseNo },
                    { "Amount", paid }, { "Mode", rng.NextDouble() < 0.7 ? "BankTransfer" : "Cheque" }, { "BranchId", branch.Id }
                });
            }
        }
    }

    private static TimeZoneInfo ResolveTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }
}
