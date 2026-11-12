using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting.Infrastructure.Local;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public static class WebErpScenarioVerifier
{
    private const string LogoData = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    public static async Task RunApiScenarioAsync(WebErpServer server, LocalAccountingDbContext db, CancellationToken ct = default)
    {
        await server.StartAsync(ct).ConfigureAwait(true);
        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };

        await PostAsync(http, "/api/settings", new
        {
            arabicName = "كودينكس للاختبار النهائي", englishName = "Qodynex Final QA", business = "أنظمة محاسبية",
            city = "صنعاء", address = "شارع حدة", phones = "782843242", whatsapp = "967782843242",
            taxNumber = "TX-QDX-2026", commercialRegistration = "CR-QDX-2026", footer = "شكراً لتعاملكم معنا",
            currency = "YER", currencySymbol = "ر.ي", taxEnabled = true, taxRate = 5m,
            allowNegativeStock = false, logoData = LogoData
        }, ct).ConfigureAwait(true);

        var boot = await GetDataAsync(http, "/api/bootstrap", ct).ConfigureAwait(true);
        var warehouseId = Guid.Parse(boot.GetProperty("warehouses")[0].GetProperty("id").GetString()!);
        var units = boot.GetProperty("units");
        var baseUnitId = FindByCode(units, "EA");
        var cartonUnitId = FindByCode(units, "CTN");
        var accounts = boot.GetProperty("accounts");
        var inventoryAccountId = FindByCode(accounts, "1140");
        var salesAccountId = FindByCode(accounts, "4100");
        var cogsAccountId = FindByCode(accounts, "5100");
        var cashAccountId = FindByCode(accounts, "1110");

        var customer = await PostDataAsync(http, "/api/partners", new
        {
            id = (Guid?)null, type = "Customer", name = "عميل فحص نهائي", phone = "777000001", whatsapp = "967777000001",
            city = "صنعاء", address = "حدة", taxNumber = "CUS-TAX", creditLimit = 50000m, openingBalance = 100m
        }, ct).ConfigureAwait(true);
        var customerId = Guid.Parse(customer.GetProperty("id").GetString()!);
        var customerControl = Guid.Parse(customer.GetProperty("control_account_id").GetString()!);

        var supplier = await PostDataAsync(http, "/api/partners", new
        {
            id = (Guid?)null, type = "Supplier", name = "مورد فحص نهائي", phone = "777000002", whatsapp = "967777000002",
            city = "صنعاء", address = "الستين", taxNumber = "SUP-TAX", creditLimit = 50000m, openingBalance = 0m
        }, ct).ConfigureAwait(true);
        var supplierId = Guid.Parse(supplier.GetProperty("id").GetString()!);
        var supplierControl = Guid.Parse(supplier.GetProperty("control_account_id").GetString()!);

        var item = await PostDataAsync(http, "/api/items", new
        {
            id = (Guid?)null, sku = "QA-CARTON-24", name = "صنف فحص كرتون 24", baseUnitId,
            inventoryAccountId, salesAccountId, cogsAccountId,
            units = new object[]
            {
                new { unitId = baseUnitId, factor = 1m, barcode = "299000000001", salesPrice = 16m, openingCost = 10m, isBase = true },
                new { unitId = cartonUnitId, factor = 24m, barcode = "299000000024", salesPrice = 384m, openingCost = 240m, isBase = false }
            }
        }, ct).ConfigureAwait(true);
        var itemId = Guid.Parse(item.GetProperty("id").GetString()!);

        var scan = await GetDataAsync(http, "/api/items/scan/299000000024", ct).ConfigureAwait(true);
        var scannedItemId = Guid.Parse(scan.GetProperty("item_id").GetString()!);
        var scannedFactor = scan.GetProperty("factor").GetDecimal();
        if (scannedItemId != itemId || Math.Abs(scannedFactor - 24m) > 0.0001m)
            throw new InvalidOperationException($"Carton barcode did not resolve to the expected item/unit factor. Item={scannedItemId}, Expected={itemId}, Factor={scannedFactor}.");
        var cartonItemUnitId = Guid.Parse(scan.GetProperty("item_unit_id").GetString()!);

        var purchase = await PostDataAsync(http, "/api/invoices/post", new
        {
            id = (Guid?)null, kind = "purchase", warehouseId, settlementAccountId = supplierControl, partnerId = supplierId,
            paymentMode = "credit", discount = 0m, taxRate = 5m, notes = "فحص WAC",
            lines = new[] { new { itemId, itemUnitId = cartonItemUnitId, quantity = 5m, unitPrice = 240m, discount = 0m } }
        }, ct).ConfigureAwait(true);
        var purchaseId = Guid.Parse(purchase.GetProperty("id").GetString()!);
        _ = purchaseId;

        var stock = await GetDataAsync(http, $"/api/stock-card/{itemId}", ct).ConfigureAwait(true);
        var qtyAfterPurchase = stock.GetProperty("position").GetProperty("quantity").GetDecimal();
        var avgAfterPurchase = stock.GetProperty("position").GetProperty("averageCost").GetDecimal();
        if (qtyAfterPurchase != 120m || Math.Abs(avgAfterPurchase - 10m) > 0.01m)
            throw new InvalidOperationException($"Purchase WAC scenario failed. Quantity={qtyAfterPurchase}, WAC={avgAfterPurchase}.");

        var baseScan = await GetDataAsync(http, "/api/items/scan/299000000001", ct).ConfigureAwait(true);
        var baseItemUnitId = Guid.Parse(baseScan.GetProperty("item_unit_id").GetString()!);
        var draft = await PostDataAsync(http, "/api/invoices/draft", new
        {
            id = (Guid?)null, kind = "sale", warehouseId, settlementAccountId = customerControl, partnerId = customerId,
            paymentMode = "credit", discount = 0m, taxRate = 5m, notes = "مسودة قابلة للتحرير",
            lines = new[] { new { itemId, itemUnitId = baseItemUnitId, quantity = 10m, unitPrice = 15m, discount = 0m } }
        }, ct).ConfigureAwait(true);
        var draftId = Guid.Parse(draft.GetProperty("id").GetString()!);

        var postedSale = await PostDataAsync(http, "/api/invoices/post", new
        {
            id = draftId, kind = "sale", warehouseId, settlementAccountId = customerControl, partnerId = customerId,
            paymentMode = "credit", discount = 0m, taxRate = 5m, notes = "تم تحرير المسودة قبل الترحيل",
            lines = new[] { new { itemId, itemUnitId = baseItemUnitId, quantity = 12m, unitPrice = 16m, discount = 0m } }
        }, ct).ConfigureAwait(true);
        var saleId = Guid.Parse(postedSale.GetProperty("id").GetString()!);

        stock = await GetDataAsync(http, $"/api/stock-card/{itemId}", ct).ConfigureAwait(true);
        var qtyAfterSale = stock.GetProperty("position").GetProperty("quantity").GetDecimal();
        if (qtyAfterSale != 108m) throw new InvalidOperationException($"Sales stock decrement failed. Quantity={qtyAfterSale}.");

        await PostAsync(http, "/api/vouchers", new
        {
            id = (Guid?)null, kind = "receipt", cashAccountId, counterAccountId = customerControl, partnerId = customerId,
            amount = 50m, description = "تحصيل اختبار", draft = false
        }, ct).ConfigureAwait(true);

        var statement = await GetDataAsync(http, $"/api/partners/{customerId}/statement", ct).ConfigureAwait(true);
        if (statement.GetProperty("rows").GetArrayLength() < 3) throw new InvalidOperationException("Customer statement did not include opening balance, sale and receipt.");

        var integrity = await GetDataAsync(http, "/api/reports/integrity", ct).ConfigureAwait(true);
        if (!integrity.GetProperty("healthy").GetBoolean()) throw new InvalidOperationException("Ledger integrity check failed during final web ERP scenario.");

        var print80 = await GetDataAsync(http, $"/api/print/invoice/{saleId}?size=thermal", ct).ConfigureAwait(true);
        var printA4 = await GetDataAsync(http, $"/api/print/invoice/{saleId}?size=a4", ct).ConfigureAwait(true);
        foreach (var print in new[] { print80, printA4 })
        {
            if (!print.GetProperty("qr").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                throw new InvalidOperationException("Printable invoice QR was not generated as a real SVG data URI.");
            var s = print.GetProperty("settings");
            if (s.GetProperty("city").GetString() != "صنعاء" || s.GetProperty("phones").GetString() != "782843242" || string.IsNullOrWhiteSpace(s.GetProperty("logo_data").GetString()))
                throw new InvalidOperationException("Branding/logo/city/phones were not returned in print data.");
        }

        var backup = await PostDataAsync(http, "/api/backup", new { }, ct).ConfigureAwait(true);
        var backupPath = backup.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath)) throw new InvalidOperationException("Backup file was not created.");

        var itemRow = await db.Items.AsNoTracking().SingleAsync(i => i.Id == itemId, ct).ConfigureAwait(true);
        if (!itemRow.IsActive || itemRow.Sku != "QA-CARTON-24") throw new InvalidOperationException("Created item was not persisted in the real SQLite database.");
    }

    public static async Task RunWebViewSmokeAsync(WebErpWindow window, CancellationToken ct = default)
    {
        window.Show();
        window.Activate();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(true);
        var script = "JSON.stringify({title:document.title,nav:document.querySelectorAll('[data-route]').length,svg:document.querySelectorAll('svg').length,view:!!document.querySelector('#view')})";
        var json = await window.Browser.ExecuteScriptAsync(script).ConfigureAwait(true);
        var decoded = JsonSerializer.Deserialize<string>(json) ?? "{}";
        using var doc = JsonDocument.Parse(decoded);
        var root = doc.RootElement;
        if (!root.GetProperty("view").GetBoolean() || root.GetProperty("nav").GetInt32() < 9 || root.GetProperty("svg").GetInt32() < 10)
            throw new InvalidOperationException($"WebView2 shell did not render the complete interactive ERP navigation. {decoded}");

        foreach (var route in new[] { "dashboard", "items", "partners", "sales", "purchases", "vouchers", "accounts", "reports", "settings" })
        {
            await window.Browser.ExecuteScriptAsync(
                $"window.__qodynexTourDone=''; navigate('{route}').then(()=>window.__qodynexTourDone='{route}').catch(e=>window.__qodynexTourDone='ERROR:'+String(e?.message||e)); 'started';")
                .ConfigureAwait(true);

            var rendered = false;
            var snapshot = "{}";
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(200, ct).ConfigureAwait(true);
                var raw = await window.Browser.ExecuteScriptAsync(
                    $"JSON.stringify({{done:window.__qodynexTourDone||'',route:state.route,text:(document.querySelector('#view')?.textContent||'').trim(),pageHead:!!document.querySelector('#view .page-head'),active:!!document.querySelector('[data-route=\"{route}\"].active')}})")
                    .ConfigureAwait(true);
                snapshot = JsonSerializer.Deserialize<string>(raw) ?? "{}";
                using var routeDoc = JsonDocument.Parse(snapshot);
                var rr = routeDoc.RootElement;
                var done = rr.GetProperty("done").GetString() ?? "";
                if (done.StartsWith("ERROR:", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Web UI route '{route}' navigation failed. {snapshot}");
                if (string.Equals(done, route, StringComparison.Ordinal)
                    && string.Equals(rr.GetProperty("route").GetString(), route, StringComparison.Ordinal)
                    && rr.GetProperty("active").GetBoolean()
                    && rr.GetProperty("pageHead").GetBoolean()
                    && (rr.GetProperty("text").GetString()?.Length ?? 0) > 20)
                {
                    rendered = true;
                    break;
                }
            }

            if (!rendered)
                throw new InvalidOperationException($"Web UI route '{route}' did not finish rendering. {snapshot}");
        }
        window.Close();
    }

    private static Guid FindByCode(JsonElement array, string code)
    {
        foreach (var e in array.EnumerateArray())
            if (string.Equals(e.GetProperty("code").GetString(), code, StringComparison.OrdinalIgnoreCase))
                return Guid.Parse(e.GetProperty("id").GetString()!);
        throw new InvalidOperationException($"Required code was not found: {code}");
    }

    private static async Task<JsonElement> GetDataAsync(HttpClient http, string path, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(path, ct).ConfigureAwait(true);
            return await ReadDataAsync(res, ct).ConfigureAwait(true);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"GET {path} exceeded the Web ERP verification timeout.", ex);
        }
    }

    private static async Task PostAsync(HttpClient http, string path, object body, CancellationToken ct) =>
        _ = await PostDataAsync(http, path, body, ct).ConfigureAwait(true);

    private static async Task<JsonElement> PostDataAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        try
        {
            using var res = await http.PostAsJsonAsync(path, body, ct).ConfigureAwait(true);
            return await ReadDataAsync(res, ct).ConfigureAwait(true);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"POST {path} exceeded the Web ERP verification timeout.", ex);
        }
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(true);
        using var doc = JsonDocument.Parse(text);
        if (!res.IsSuccessStatusCode || !doc.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : text);
        return doc.RootElement.GetProperty("data").Clone();
    }
}