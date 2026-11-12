using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZXing;
using ZXing.Common;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Accounting.UI.Windows;

public sealed partial class WebErpServer : IAsyncDisposable
{
    private const string ModuleKey = "WHOLESALE";
    private readonly LocalAccountingDbContext _db;
    private readonly WindowsAccountingFacade _facade;
    private readonly UserContext _user;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // Local Web ERP currently shares a single EF Core DbContext with the WPF composition root.
    // ASP.NET Core may dispatch multiple fetches from the dashboard/navigation concurrently,
    // so serialize API requests to prevent overlapping operations on that DbContext.
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private WebApplication? _app;

    public WebErpServer(LocalAccountingDbContext db, WindowsAccountingFacade facade, UserContext user, string dbPath)
    {
        _db = db;
        _facade = facade;
        _user = user;
        _dbPath = Path.GetFullPath(dbPath);
        BaseUrl = $"http://127.0.0.1:{ReserveLoopbackPort()}";
    }

    public string BaseUrl { get; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null) return;
        await EnsureWebSchemaAsync(ct).ConfigureAwait(false);

        var options = new WebApplicationOptions
        {
            ApplicationName = typeof(WebErpServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(BaseUrl);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api"))
            {
                await next().ConfigureAwait(false);
                return;
            }

            await _requestGate.WaitAsync(ctx.RequestAborted).ConfigureAwait(false);
            try { await next().ConfigureAwait(false); }
            finally { _requestGate.Release(); }
        });

        app.Use(async (ctx, next) =>
        {
            try { await next().ConfigureAwait(false); }
            catch (Exception ex)
            {
                RuntimeErrorHub.Report(ex, $"Web ERP API {ctx.Request.Method} {ctx.Request.Path}");
                if (ctx.Response.HasStarted) throw;
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new { ok = false, error = ex.Message, type = ex.GetType().Name, path = ctx.Request.Path.Value }, ctx.RequestAborted).ConfigureAwait(false);
            }
        });

        MapStatic(app);
        MapApi(app);
        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
    }

    private static void MapStatic(WebApplication app)
    {
        app.MapGet("/", ctx => ServeResourceAsync(ctx, "WebUI.index.html", "text/html; charset=utf-8"));
        app.MapGet("/index.html", ctx => ServeResourceAsync(ctx, "WebUI.index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.css", ctx => ServeResourceAsync(ctx, "WebUI.app.css", "text/css; charset=utf-8"));
        app.MapGet("/app.js", ctx => ServeResourceAsync(ctx, "WebUI.app.js", "application/javascript; charset=utf-8"));
    }

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/bootstrap", ctx => JsonOkAsync(ctx, BootstrapAsync(ctx.RequestAborted)));
        app.MapGet("/api/dashboard", ctx => JsonOkAsync(ctx, DashboardAsync(ctx.RequestAborted)));

        app.MapGet("/api/items", ctx => JsonOkAsync(ctx, ListItemsAsync(ctx.Request.Query["q"].ToString(), ctx.RequestAborted)));
        app.MapGet("/api/items/scan/{barcode}", ctx => JsonOkAsync(ctx, ScanItemAsync((string)ctx.Request.RouteValues["barcode"]!, ctx.RequestAborted)));
        app.MapPost("/api/items", async ctx => await JsonOkAsync(ctx, SaveItemAsync(await ReadAsync<WebItemRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapDelete("/api/items/{id:guid}", ctx => JsonOkAsync(ctx, DeleteItemAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.RequestAborted)));
        app.MapPost("/api/barcode/generate", ctx => JsonOkAsync(ctx, GenerateBarcodeAsync(ctx.RequestAborted)));
        app.MapGet("/api/stock-card/{id:guid}", ctx => JsonOkAsync(ctx, StockCardAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.RequestAborted)));

        app.MapGet("/api/partners", ctx => JsonOkAsync(ctx, ListPartnersAsync(ctx.Request.Query["type"].ToString(), ctx.Request.Query["q"].ToString(), ctx.RequestAborted)));
        app.MapPost("/api/partners", async ctx => await JsonOkAsync(ctx, SavePartnerAsync(await ReadAsync<WebPartnerRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapGet("/api/partners/{id:guid}/statement", ctx => JsonOkAsync(ctx, PartnerStatementAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.Request.Query["fromDate"].ToString(), ctx.Request.Query["toDate"].ToString(), ctx.RequestAborted)));

        app.MapPost("/api/invoices/draft", async ctx => await JsonOkAsync(ctx, SaveInvoiceAsync(await ReadAsync<WebInvoiceRequest>(ctx).ConfigureAwait(false), post: false, ctx.RequestAborted)).ConfigureAwait(false));
        app.MapPost("/api/invoices/post", async ctx => await JsonOkAsync(ctx, SaveInvoiceAsync(await ReadAsync<WebInvoiceRequest>(ctx).ConfigureAwait(false), post: true, ctx.RequestAborted)).ConfigureAwait(false));
        app.MapGet("/api/invoices", ctx => JsonOkAsync(ctx, ListInvoicesAsync(ctx.Request.Query["kind"].ToString(), ctx.Request.Query["status"].ToString(), ctx.RequestAborted)));
        app.MapPost("/api/invoices/{id:guid}/reverse", ctx => JsonOkAsync(ctx, ReverseDocumentAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.RequestAborted)));

        app.MapGet("/api/vouchers", ctx => JsonOkAsync(ctx, ListVouchersAsync(ctx.RequestAborted)));
        app.MapPost("/api/vouchers", async ctx => await JsonOkAsync(ctx, SaveVoucherAsync(await ReadAsync<WebVoucherRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapPost("/api/vouchers/{id:guid}/reverse", ctx => JsonOkAsync(ctx, ReverseDocumentAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.RequestAborted)));

        app.MapGet("/api/journals", ctx => JsonOkAsync(ctx, ListJournalsAsync(ctx.RequestAborted)));
        app.MapPost("/api/journals", async ctx => await JsonOkAsync(ctx, SaveJournalAsync(await ReadAsync<WebJournalRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapPost("/api/journals/{id:guid}/reverse", ctx => JsonOkAsync(ctx, ReverseJournalAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.RequestAborted)));
        app.MapPost("/api/accounts", async ctx => await JsonOkAsync(ctx, SaveAccountAsync(await ReadAsync<WebAccountRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapGet("/api/accounts/{id:guid}/ledger", ctx => JsonOkAsync(ctx, AccountLedgerAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.Request.Query["fromDate"].ToString(), ctx.Request.Query["toDate"].ToString(), ctx.RequestAborted)));

        app.MapGet("/api/reports/trial-balance", ctx => JsonOkAsync(ctx, TrialBalanceAsync(ctx.RequestAborted)));
        app.MapGet("/api/reports/profit-loss", ctx => JsonOkAsync(ctx, ProfitLossAsync(ctx.RequestAborted)));
        app.MapGet("/api/reports/inventory", ctx => JsonOkAsync(ctx, ListItemsAsync(string.Empty, ctx.RequestAborted)));
        app.MapGet("/api/reports/integrity", ctx => JsonOkAsync(ctx, IntegrityAsync(ctx.RequestAborted)));

        app.MapPost("/api/backup", ctx => JsonOkAsync(ctx, BackupAsync(ctx.RequestAborted)));
        app.MapPost("/api/restore", async ctx => await JsonOkAsync(ctx, RestoreAsync(await ReadAsync<WebRestoreRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapGet("/api/sync", ctx => JsonOkAsync(ctx, SyncAsync(ctx.RequestAborted)));
        app.MapPost("/api/sync/settings", async ctx => await JsonOkAsync(ctx, SaveSyncAsync(await ReadAsync<WebSyncSettingsRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapPost("/api/sync/conflicts/{id:guid}/resolve", ctx => JsonOkAsync(ctx, ResolveConflictAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.RequestAborted)));

        app.MapGet("/api/settings", ctx => JsonOkAsync(ctx, GetSettingsAsync(ctx.RequestAborted)));
        app.MapPost("/api/settings", async ctx => await JsonOkAsync(ctx, SaveSettingsAsync(await ReadAsync<WebSettingsRequest>(ctx).ConfigureAwait(false), ctx.RequestAborted)).ConfigureAwait(false));
        app.MapGet("/api/print/invoice/{id:guid}", ctx => JsonOkAsync(ctx, PrintInvoiceAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.Request.Query["size"].ToString(), ctx.RequestAborted)));
        app.MapGet("/api/print/voucher/{id:guid}", ctx => JsonOkAsync(ctx, PrintVoucherAsync(Guid.Parse((string)ctx.Request.RouteValues["id"]!), ctx.Request.Query["size"].ToString(), ctx.RequestAborted)));
    }

    private async Task<object?> BootstrapAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        var warehouses = await _db.Warehouses.AsNoTracking().Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderBy(x => x.Code).Select(x => new { id = x.Id, code = x.Code, name = x.Name }).ToListAsync(ct).ConfigureAwait(false);
        var units = await _db.UnitOfMeasures.AsNoTracking().Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderBy(x => x.Code).Select(x => new { id = x.Id, code = x.Code, name = x.Name }).ToListAsync(ct).ConfigureAwait(false);
        var accounts = await _db.Accounts.AsNoTracking().Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive)
            .OrderBy(x => x.Code).Select(x => new
            {
                id = x.Id, code = x.Code, name = x.Name, parent_id = x.ParentAccountId,
                type = x.AccountType.ToString(), normal = x.NormalBalance.ToString(), postable = x.IsPostable, active = x.IsActive
            }).ToListAsync(ct).ConfigureAwait(false);
        return new { settings, warehouses, units, accounts };
    }

    private async Task<object?> DashboardAsync(CancellationToken ct)
    {
        var x = await _facade.GetDashboardAsync(ct).ConfigureAwait(false);
        var recent = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == _user.CompanyId && j.BranchId == _user.BranchId && j.Status == PostingStatus.Posted)
            .OrderByDescending(j => j.EntryDateUtc).ThenByDescending(j => j.Id).Take(15)
            .Select(j => new { reference = j.Reference, description = j.Description, date = j.EntryDateUtc, source_type = j.SourceDocumentType })
            .ToListAsync(ct).ConfigureAwait(false);
        return new { sales = x.SalesTotal, purchases = x.PurchaseTotal, cash = x.CashAndBankBalance, inventory = x.InventoryValue, receivables = x.CustomerReceivables, pending = x.PendingOutbox, recent };
    }

    private async Task EnsureWebSchemaAsync(CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(con, """
            CREATE TABLE IF NOT EXISTS LocalWebPartnerProfiles(
                PartnerId TEXT NOT NULL PRIMARY KEY, CompanyId TEXT NOT NULL, BranchId TEXT NOT NULL,
                WhatsApp TEXT NOT NULL DEFAULT '', City TEXT NOT NULL DEFAULT '', Address TEXT NOT NULL DEFAULT '', TaxNumber TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS LocalWebItemUnits(
                Id TEXT NOT NULL PRIMARY KEY, CompanyId TEXT NOT NULL, BranchId TEXT NOT NULL, ItemId TEXT NOT NULL,
                UnitOfMeasureId TEXT NOT NULL, Factor TEXT NOT NULL, Barcode TEXT NOT NULL, SalesPrice TEXT NOT NULL DEFAULT '0',
                OpeningCost TEXT NOT NULL DEFAULT '0', IsBase INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalWebItemUnits_Item_Unit ON LocalWebItemUnits(CompanyId,BranchId,ItemId,UnitOfMeasureId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalWebItemUnits_Barcode ON LocalWebItemUnits(CompanyId,BranchId,Barcode);
            CREATE TABLE IF NOT EXISTS LocalWebDocumentMetadata(
                DocumentId TEXT NOT NULL PRIMARY KEY, CompanyId TEXT NOT NULL, BranchId TEXT NOT NULL, PaymentMode TEXT NOT NULL DEFAULT 'cash',
                InvoiceDiscount TEXT NOT NULL DEFAULT '0', TaxRatePercent TEXT NOT NULL DEFAULT '0', Subtotal TEXT NOT NULL DEFAULT '0',
                Tax TEXT NOT NULL DEFAULT '0', Total TEXT NOT NULL DEFAULT '0', Notes TEXT NOT NULL DEFAULT '', UpdatedAtUtcTicks INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS LocalWebDocumentLineMetadata(
                LineId TEXT NOT NULL PRIMARY KEY, DocumentId TEXT NOT NULL, ItemUnitId TEXT NOT NULL, DisplayUnitPrice TEXT NOT NULL DEFAULT '0',
                Discount TEXT NOT NULL DEFAULT '0', LineTotal TEXT NOT NULL DEFAULT '0', Factor TEXT NOT NULL DEFAULT '1');
            CREATE INDEX IF NOT EXISTS IX_LocalWebDocumentLineMetadata_Document ON LocalWebDocumentLineMetadata(DocumentId);
            CREATE TABLE IF NOT EXISTS LocalWebSettings(
                CompanyId TEXT NOT NULL, BranchId TEXT NOT NULL, WhatsApp TEXT NOT NULL DEFAULT '', LogoData TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(CompanyId,BranchId));
            """, ct).ConfigureAwait(false);
        await SeedWebItemUnitsAsync(con, ct).ConfigureAwait(false);
    }

    private async Task SeedWebItemUnitsAsync(SqliteConnection con, CancellationToken ct)
    {
        var items = await QueryAsync(con, """
            SELECT i.Id id,i.Sku sku,i.Barcode barcode,i.BaseUnitOfMeasureId base_unit,
                   COALESCE(c.BaseSalesPrice,'0') base_price,COALESCE(c.OpeningUnitCost,'0') opening_cost,
                   c.CartonUnitId carton_unit,c.CartonBarcode carton_barcode,COALESCE(c.CartonSalesPrice,'0') carton_price
            FROM Items i LEFT JOIN LocalItemCommercialSettings c ON c.ItemId=i.Id
            WHERE i.CompanyId=$company AND i.BranchId=$branch
            """, ct, ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString())).ConfigureAwait(false);
        foreach (var item in items)
        {
            var itemId = item["id"]!.ToString()!;
            if (await ScalarLongAsync(con, "SELECT COUNT(*) FROM LocalWebItemUnits WHERE ItemId=$id", ct, ("$id", itemId)).ConfigureAwait(false) > 0) continue;
            var sku = item["sku"]?.ToString() ?? itemId[..8];
            var baseBarcode = item["barcode"]?.ToString();
            if (string.IsNullOrWhiteSpace(baseBarcode)) baseBarcode = "QDX-" + sku;
            await ExecuteAsync(con, """
                INSERT OR IGNORE INTO LocalWebItemUnits(Id,CompanyId,BranchId,ItemId,UnitOfMeasureId,Factor,Barcode,SalesPrice,OpeningCost,IsBase)
                VALUES($id,$company,$branch,$item,$unit,'1',$barcode,$price,$cost,1)
                """, ct, ("$id", UuidV7.NewGuid().ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                ("$item", itemId), ("$unit", item["base_unit"]!.ToString()), ("$barcode", baseBarcode),
                ("$price", item["base_price"]?.ToString() ?? "0"), ("$cost", item["opening_cost"]?.ToString() ?? "0")).ConfigureAwait(false);
            var conversions = await QueryAsync(con, "SELECT UnitOfMeasureId unit_id,BaseUnitsPerUnit factor FROM ItemUnitConversions WHERE ItemId=$id", ct, ("$id", itemId)).ConfigureAwait(false);
            foreach (var conversion in conversions)
            {
                var isCarton = string.Equals(item["carton_unit"]?.ToString(), conversion["unit_id"]?.ToString(), StringComparison.OrdinalIgnoreCase);
                var barcode = isCarton && !string.IsNullOrWhiteSpace(item["carton_barcode"]?.ToString()) ? item["carton_barcode"]!.ToString()! : $"QDX-{sku}-{conversion["unit_id"]!.ToString()![..6]}";
                await ExecuteAsync(con, """
                    INSERT OR IGNORE INTO LocalWebItemUnits(Id,CompanyId,BranchId,ItemId,UnitOfMeasureId,Factor,Barcode,SalesPrice,OpeningCost,IsBase)
                    VALUES($id,$company,$branch,$item,$unit,$factor,$barcode,$price,$cost,0)
                    """, ct, ("$id", UuidV7.NewGuid().ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                    ("$item", itemId), ("$unit", conversion["unit_id"]!.ToString()), ("$factor", conversion["factor"]!.ToString()), ("$barcode", barcode),
                    ("$price", isCarton ? item["carton_price"]?.ToString() ?? "0" : "0"), ("$cost", item["opening_cost"]?.ToString() ?? "0")).ConfigureAwait(false);
            }
        }
    }

    private static string QrDataUri(string text)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Height = 220, Width = 220, Margin = 1, PureBarcode = true }
        };
        var pixels = writer.Write(text);
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private static string? TicksToIso(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (!long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) return Convert.ToString(value, CultureInfo.InvariantCulture);
        return new DateTimeOffset(ticks, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
    }

    private static string Dec(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal DbDecimal(object? value)
    {
        if (value is null or DBNull) return 0m;
        if (value is decimal decimalValue) return decimalValue;
        if (value is double doubleValue) return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
        if (value is float floatValue) return Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
        if (value is long longValue) return longValue;
        if (value is int intValue) return intValue;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new InvalidOperationException($"Invalid decimal value in local database: '{text}'.");
    }

    // EF Core's SQLite provider persists Guid values as upper-case TEXT. The embedded
    // Web ERP also writes through raw Microsoft.Data.Sqlite commands, so normalize every
    // Guid-shaped parameter to the provider representation. Without this, raw lower-case
    // Guid strings can be inserted successfully but later fail joins/queries against
    // EF-created rows (for example item-unit barcode scans).
    private static object SqlValue(object? value) => value switch
    {
        null => DBNull.Value,
        Guid guid => guid.ToString("D").ToUpperInvariant(),
        string text when Guid.TryParseExact(text, "D", out var guid) => guid.ToString("D").ToUpperInvariant(),
        _ => value
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var con = new SqliteConnection(_db.Database.GetConnectionString());
        await con.OpenAsync(ct).ConfigureAwait(false);
        await using var command = con.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return con;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection con, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = con.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, SqlValue(value));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection con, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = con.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, SqlValue(value));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0L : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection con, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = con.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, SqlValue(value));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(SqliteConnection con, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = con.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, SqlValue(value));
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<Dictionary<string, object?>?> QueryOneAsync(SqliteConnection con, string sql, CancellationToken ct, params (string Name, object? Value)[] args) =>
        (await QueryAsync(con, sql, ct, args).ConfigureAwait(false)).FirstOrDefault();

    private static async Task<T> ReadAsync<T>(HttpContext ctx) =>
        await ctx.Request.ReadFromJsonAsync<T>(cancellationToken: ctx.RequestAborted).ConfigureAwait(false) ?? throw new InvalidOperationException("بيانات الطلب فارغة أو غير صالحة.");

    private static async Task JsonOkAsync(HttpContext ctx, Task<object?> task)
    {
        var data = await task.ConfigureAwait(false);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(new { ok = true, data }, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ServeResourceAsync(HttpContext ctx, string suffix, string contentType)
    {
        var assembly = typeof(WebErpServer).Assembly;
        var normalized = suffix.Replace('/', '.').Replace('\\', '.');
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded Web UI resource missing: {suffix}");
        await using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException(resourceName);
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.CacheControl = "no-store";
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
        _writeGate.Dispose();
        _requestGate.Dispose();
    }
}