using System.IO;
using System.Text.Json;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public sealed partial class WebErpServer
{
    private async Task<object?> TrialBalanceAsync(CancellationToken ct)
    {
        var rows = await _facade.GetTrialBalanceAsync(ct).ConfigureAwait(false);
        var accounts = await _db.Accounts.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId && a.BranchId == _user.BranchId)
            .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);
        return rows.Select(r => new
        {
            id = r.AccountId, code = r.Code, name = r.Name,
            type = accounts.TryGetValue(r.AccountId, out var a) ? a.AccountType.ToString() : "",
            normal = accounts.TryGetValue(r.AccountId, out a) ? a.NormalBalance.ToString() : "",
            debit = r.Debit, credit = r.Credit, balance = r.Balance
        }).ToArray();
    }

    private async Task<object?> ProfitLossAsync(CancellationToken ct)
    {
        var tb = await _facade.GetTrialBalanceAsync(ct).ConfigureAwait(false);
        var types = await _db.Accounts.AsNoTracking().Where(a => a.CompanyId == _user.CompanyId && a.BranchId == _user.BranchId)
            .ToDictionaryAsync(a => a.Id, a => a.AccountType, ct).ConfigureAwait(false);
        decimal revenue = 0m, expense = 0m;
        foreach (var row in tb)
        {
            if (!types.TryGetValue(row.AccountId, out var type)) continue;
            if (type == AccountType.Revenue) revenue += row.Credit - row.Debit;
            if (type == AccountType.Expense) expense += row.Debit - row.Credit;
        }
        return new { revenue, expense, profit = revenue - expense };
    }

    private async Task<object?> IntegrityAsync(CancellationToken ct)
    {
        var result = await _facade.CheckLocalDatabaseIntegrityAsync(ct).ConfigureAwait(false);
        return new { healthy = result.IsHealthy, quickCheck = result.QuickCheck, foreignKeys = Enumerable.Repeat("violation", result.ForeignKeyViolations).ToArray(), unbalanced = Enumerable.Repeat("unbalanced", result.UnbalancedPostedJournals).ToArray(), summary = result.Summary };
    }

    private async Task<object?> BackupAsync(CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_dbPath)!, "Backups"); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"Qodynex-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
        var path = await _facade.CreateLocalBackupAsync(target, ct).ConfigureAwait(false);
        return new { path, size = new FileInfo(path).Length };
    }

    private async Task<object?> RestoreAsync(WebRestoreRequest x, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(x.Path)) throw new InvalidOperationException("مسار النسخة الاحتياطية مطلوب.");
        var path = await _facade.ScheduleLocalRestoreAsync(x.Path, ct).ConfigureAwait(false);
        return new { restored = true, pendingPath = path, restartRequired = true };
    }

    private async Task<object?> SyncAsync(CancellationToken ct)
    {
        var serverUrl = await _facade.GetServerUrlAsync(ct).ConfigureAwait(false);
        var conflicts = await _db.LocalSyncConflicts.AsNoTracking().Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderByDescending(x => x.ServerTimestampUtc).Select(x => new { id = x.Id, code = x.ConflictCode, message = x.Message, resolved = x.IsResolved, created_at = x.ServerTimestampUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        return new { serverUrl, conflicts };
    }

    private async Task<object?> SaveSyncAsync(WebSyncSettingsRequest x, CancellationToken ct)
    {
        await _facade.SaveServerUrlAsync(x.ServerUrl ?? string.Empty, ct).ConfigureAwait(false);
        return new { saved = true };
    }

    private async Task<object?> ResolveConflictAsync(Guid id, CancellationToken ct)
    {
        await _facade.ResolveConflictLocallyAsync(id, ct).ConfigureAwait(false);
        return new { resolved = true };
    }

    private async Task<object?> GetSettingsAsync(CancellationToken ct)
    {
        var b = await _facade.GetBrandingAsync(ct).ConfigureAwait(false);
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var web = await QueryOneAsync(con, "SELECT WhatsApp whatsapp,LogoData logo_data FROM LocalWebSettings WHERE CompanyId=$company AND BranchId=$branch", ct,
            ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString())).ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["arabic_name"] = b.ArabicName, ["english_name"] = b.EnglishName, ["business"] = b.BusinessDescription,
            ["city"] = b.City, ["address"] = b.Address, ["phones"] = b.Phones, ["whatsapp"] = web?["whatsapp"]?.ToString() ?? string.Empty,
            ["tax_number"] = b.TaxNumber, ["commercial_registration"] = b.CommercialRegistration, ["footer"] = b.FooterNotes,
            ["currency"] = b.CurrencyCode, ["currency_symbol"] = b.CurrencySymbol, ["tax_enabled"] = b.TaxEnabled, ["tax_rate"] = b.TaxRate * 100m,
            ["dark_mode"] = b.DarkThemeEnabled, ["allow_negative_stock"] = b.AllowNegativeStock, ["logo_data"] = web?["logo_data"]?.ToString() ?? string.Empty,
            ["server_url"] = await _facade.GetServerUrlAsync(ct).ConfigureAwait(false)
        };
    }

    private async Task<object?> SaveSettingsAsync(WebSettingsRequest x, CancellationToken ct)
    {
        byte[]? logo;
        string? logoFileName = null;
        if (!string.IsNullOrWhiteSpace(x.LogoData))
        {
            var comma = x.LogoData.IndexOf(','); var payload = comma >= 0 ? x.LogoData[(comma + 1)..] : x.LogoData;
            try { logo = Convert.FromBase64String(payload); }
            catch (FormatException ex) { throw new InvalidOperationException("ملف الشعار غير صالح.", ex); }
            logoFileName = x.LogoData.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase) ? "web-logo.jpg" : "web-logo.png";
        }
        else logo = (await _facade.GetBrandingAsync(ct).ConfigureAwait(false)).LogoBytes;
        if (x.TaxRate < 0m || x.TaxRate > 100m) throw new InvalidOperationException("نسبة الضريبة يجب أن تكون بين 0 و100.");

        await _facade.SaveBrandingAsync(new CompanyBrandingSnapshot(
            x.ArabicName, x.EnglishName, x.Business, logo, x.City, x.Address, x.Phones, "", x.TaxNumber,
            x.CommercialRegistration, x.Footer, x.Currency, x.CurrencySymbol, x.TaxEnabled, x.TaxRate / 100m,
            x.AllowNegativeStock, false), logoFileName, ct).ConfigureAwait(false);

        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(con, """
            INSERT INTO LocalWebSettings(CompanyId,BranchId,WhatsApp,LogoData) VALUES($company,$branch,$wa,$logo)
            ON CONFLICT(CompanyId,BranchId) DO UPDATE SET WhatsApp=excluded.WhatsApp,LogoData=excluded.LogoData
            """, ct, ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()), ("$wa", x.WhatsApp ?? ""), ("$logo", x.LogoData ?? "")).ConfigureAwait(false);
        return new { saved = true };
    }

    private async Task<object?> PrintInvoiceAsync(Guid id, string size, CancellationToken ct)
    {
        var docsObj = await ListInvoicesAsync(string.Empty, string.Empty, ct).ConfigureAwait(false);
        var docs = docsObj as List<Dictionary<string, object?>> ?? throw new InvalidOperationException("تعذر قراءة قائمة الفواتير.");
        var doc = docs.FirstOrDefault(d => Guid.TryParse(d["id"]?.ToString(), out var g) && g == id) ?? throw new InvalidOperationException("الفاتورة غير موجودة.");
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false); var settingsMap = (Dictionary<string, object?>)settings!;
        var json = JsonSerializer.Serialize(new { invoice = doc["number"], total = doc["total"], tax = doc["tax"], taxNo = settingsMap["tax_number"] });
        return new { invoice = doc, lines = doc["lines"], settings, qr = QrDataUri(json), size = string.IsNullOrWhiteSpace(size) ? "a4" : size };
    }

    private async Task<object?> PrintVoucherAsync(Guid id, string size, CancellationToken ct)
    {
        var rowsObj = await ListVouchersAsync(ct).ConfigureAwait(false);
        var rows = rowsObj as List<Dictionary<string, object?>> ?? throw new InvalidOperationException("تعذر قراءة قائمة السندات.");
        var found = rows.FirstOrDefault(d => Guid.TryParse(d["id"]?.ToString(), out var g) && g == id) ?? throw new InvalidOperationException("السند غير موجود.");
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        return new { voucher = found, settings, qr = QrDataUri(JsonSerializer.Serialize(new { voucher = found["number"], amount = found["amount"] })), size = string.IsNullOrWhiteSpace(size) ? "a4" : size };
    }

    private static IReadOnlyList<LedgerRow> FilterLedger(IReadOnlyList<LedgerRow> rows, string from, string to)
    {
        DateOnly? fromDate = DateOnly.TryParse(from, out var f) ? f : null; DateOnly? toDate = DateOnly.TryParse(to, out var t) ? t : null;
        return rows.Where(r => (!fromDate.HasValue || DateOnly.FromDateTime(r.DateUtc.LocalDateTime) >= fromDate.Value)
                            && (!toDate.HasValue || DateOnly.FromDateTime(r.DateUtc.LocalDateTime) <= toDate.Value)).ToArray();
    }


}