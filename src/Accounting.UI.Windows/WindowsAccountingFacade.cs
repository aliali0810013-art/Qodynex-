using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public sealed record ItemLookup(Guid Id, string Sku, string Name, string? Barcode, Guid BaseUnitOfMeasureId, ItemKind ItemKind);
public sealed record PartnerLookup(Guid Id, string Code, string Name, Guid ControlAccountId, decimal CreditLimit);
public sealed record AccountLookup(Guid Id, string Code, string Name);
public sealed record InvoiceDraftLine(Guid ItemId, Guid UnitOfMeasureId, decimal Quantity, decimal BaseUnitsPerUnit, decimal UnitPrice);
public sealed record LedgerRow(DateTimeOffset DateUtc, string Reference, string Description, decimal Debit, decimal Credit, decimal RunningBalance);
public sealed record ItemMovementRow(DateTimeOffset DateUtc, string SourceType, decimal QuantityDelta, decimal InventoryValueDelta, decimal UnitCost);
public sealed record TrialBalanceRow(Guid AccountId, string Code, string Name, decimal Debit, decimal Credit, decimal Balance);

public sealed class WindowsAccountingFacade
{
    private readonly LocalAccountingDbContext _db;
    private readonly AccountingPostingEngine _posting;
    private readonly IAccountingQueryService _queries;
    private readonly LocalTempNumberGenerator _numbers;
    private readonly UserContext _user;

    public WindowsAccountingFacade(LocalAccountingDbContext db, AccountingPostingEngine posting, IAccountingQueryService queries,
        LocalTempNumberGenerator numbers, UserContext user)
    {
        _db = db; _posting = posting; _queries = queries; _numbers = numbers; _user = user;
    }

    public async Task<IReadOnlyList<ItemLookup>> SearchItemsAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        return await _db.Items.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive
                && (text == "" || x.Sku.Contains(text) || x.Name.Contains(text) || (x.Barcode != null && x.Barcode.Contains(text))))
            .OrderBy(x => x.Name).Take(50)
            .Select(x => new ItemLookup(x.Id, x.Sku, x.Name, x.Barcode, x.BaseUnitOfMeasureId, x.ItemKind))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<ItemLookup?> FindItemByBarcodeOrSkuAsync(string code, CancellationToken ct = default)
    {
        var value = code.Trim();
        if (value.Length == 0) return null;
        return await _db.Items.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive && (x.Sku == value || x.Barcode == value))
            .Select(x => new ItemLookup(x.Id, x.Sku, x.Name, x.BaseUnitOfMeasureId, x.ItemKind))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PartnerLookup>> SearchPartnersAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        return await _db.BusinessPartners.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive
                && (text == "" || x.Code.Contains(text) || x.Name.Contains(text)))
            .OrderBy(x => x.Name).Take(50)
            .Select(x => new PartnerLookup(x.Id, x.Code, x.Name, x.ControlAccountId, x.CreditLimit))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountLookup>> SearchAccountsAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        return await _db.Accounts.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive && x.IsPostable
                && (text == "" || x.Code.Contains(text) || x.Name.Contains(text)))
            .OrderBy(x => x.Code).Take(50)
            .Select(x => new AccountLookup(x.Id, x.Code, x.Name))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<StockPosition> GetStockPositionAsync(Guid itemId, Guid warehouseId, CancellationToken ct = default) =>
        _queries.GetStockPositionAsync(_user, itemId, warehouseId, ct);

    public async Task<IReadOnlyList<ItemMovementRow>> GetItemMovementAsync(Guid itemId, Guid warehouseId, CancellationToken ct = default) =>
        await _db.StockLedgerEntries.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.ItemId == itemId && x.WarehouseId == warehouseId)
            .OrderByDescending(x => x.PostedAtUtc)
            .Select(x => new ItemMovementRow(x.PostedAtUtc, x.SourceDocumentType, x.QuantityDelta, x.InventoryValueDelta, x.UnitCost))
            .Take(500)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<decimal> GetSuggestedUnitPriceAsync(string moduleKey, Guid itemId, decimal baseQuantity, decimal fallback, CancellationToken ct = default)
    {
        if (!string.Equals(moduleKey, Accounting.Modules.WholesaleModule.ModuleKey, StringComparison.OrdinalIgnoreCase)) return fallback;
        var json = await _db.LocalModuleConfigurations.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.ModuleKey == Accounting.Modules.WholesaleModule.ModuleKey)
            .Select(x => x.ConfigurationJson)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return new Accounting.Modules.WholesalePricingService().GetUnitPrice(json ?? "{}", itemId, baseQuantity, fallback);
    }

    public async Task<CompanyPolicy> GetPolicyAsync(CancellationToken ct = default) =>
        (await _db.Companies.AsNoTracking().SingleAsync(x => x.Id == _user.CompanyId, ct).ConfigureAwait(false)).GetPolicy();

    public async Task<PostingResult> PostSalesAsync(Guid warehouseId, Guid settlementAccountId, Guid? partnerId, string moduleKey,
        string moduleMetadataJson, IReadOnlyList<InvoiceDraftLine> lines, CancellationToken ct = default)
    {
        var number = await _numbers.NextAsync(_user, nameof(SalesInvoice), ct).ConfigureAwait(false);
        var invoice = new SalesInvoice(_user.CompanyId, _user.BranchId, number, moduleKey, DateTimeOffset.UtcNow, warehouseId, settlementAccountId, partnerId);
        invoice.SetModuleMetadata(moduleMetadataJson);
        foreach (var line in lines) invoice.AddLine(line.ItemId, line.UnitOfMeasureId, line.Quantity, line.BaseUnitsPerUnit, line.UnitPrice);
        _db.SalesInvoices.Add(invoice);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await _posting.PostSalesInvoiceAsync(new PostSalesInvoiceCommand(invoice.Id,
            new PostingCommandContext(_user, UuidV7.NewGuid(), $"sales:{invoice.Id:N}")), ct).ConfigureAwait(false);
    }

    public async Task<PostingResult> PostPurchaseAsync(Guid warehouseId, Guid settlementAccountId, Guid? partnerId, string moduleKey,
        IReadOnlyList<InvoiceDraftLine> lines, CancellationToken ct = default)
    {
        var number = await _numbers.NextAsync(_user, nameof(PurchaseInvoice), ct).ConfigureAwait(false);
        var invoice = new PurchaseInvoice(_user.CompanyId, _user.BranchId, number, moduleKey, DateTimeOffset.UtcNow, warehouseId, settlementAccountId, partnerId);
        foreach (var line in lines) invoice.AddLine(line.ItemId, line.UnitOfMeasureId, line.Quantity, line.BaseUnitsPerUnit, line.UnitPrice);
        _db.PurchaseInvoices.Add(invoice);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await _posting.PostPurchaseInvoiceAsync(new PostPurchaseInvoiceCommand(invoice.Id,
            new PostingCommandContext(_user, UuidV7.NewGuid(), $"purchase:{invoice.Id:N}")), ct).ConfigureAwait(false);
    }

    public async Task<PostingResult> PostVoucherAsync(bool receipt, Guid cashAccountId, Guid counterAccountId, Guid? partnerId,
        decimal amount, string description, CancellationToken ct = default)
    {
        if (receipt)
        {
            var number = await _numbers.NextAsync(_user, nameof(ReceiptVoucher), ct).ConfigureAwait(false);
            var voucher = new ReceiptVoucher(_user.CompanyId, _user.BranchId, number, "CORE", DateTimeOffset.UtcNow,
                cashAccountId, counterAccountId, amount, description, partnerId);
            _db.ReceiptVouchers.Add(voucher); await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return await _posting.PostReceiptVoucherAsync(new PostReceiptVoucherCommand(voucher.Id,
                new PostingCommandContext(_user, UuidV7.NewGuid(), $"receipt:{voucher.Id:N}")), ct).ConfigureAwait(false);
        }
        else
        {
            var number = await _numbers.NextAsync(_user, nameof(PaymentVoucher), ct).ConfigureAwait(false);
            var voucher = new PaymentVoucher(_user.CompanyId, _user.BranchId, number, "CORE", DateTimeOffset.UtcNow,
                cashAccountId, counterAccountId, amount, description, partnerId);
            _db.PaymentVouchers.Add(voucher); await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return await _posting.PostPaymentVoucherAsync(new PostPaymentVoucherCommand(voucher.Id,
                new PostingCommandContext(_user, UuidV7.NewGuid(), $"payment:{voucher.Id:N}")), ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<LedgerRow>> GetLedgerAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await _db.Accounts.AsNoTracking().SingleAsync(x => x.Id == accountId && x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId, ct).ConfigureAwait(false);
        var raw = await (from line in _db.JournalLines.AsNoTracking()
                         join entry in _db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
                         where line.AccountId == accountId && line.CompanyId == _user.CompanyId && line.BranchId == _user.BranchId
                               && entry.Status == PostingStatus.Posted
                         orderby entry.EntryDateUtc, line.Id
                         select new { entry.EntryDateUtc, entry.Reference, entry.Description, line.Debit, line.Credit })
            .ToListAsync(ct).ConfigureAwait(false);

        decimal running = 0m;
        var rows = new List<LedgerRow>(raw.Count);
        foreach (var row in raw)
        {
            running += account.NormalBalance == NormalBalance.Debit ? row.Debit - row.Credit : row.Credit - row.Debit;
            rows.Add(new LedgerRow(row.EntryDateUtc, row.Reference, row.Description, row.Debit, row.Credit, running));
        }
        return rows;
    }

    public async Task<IReadOnlyList<TrialBalanceRow>> GetTrialBalanceAsync(CancellationToken ct = default)
    {
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsPostable)
            .OrderBy(x => x.Code).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<TrialBalanceRow>(accounts.Count);
        foreach (var account in accounts)
        {
            var balance = await _queries.GetAccountBalanceAsync(_user, account.Id, ct).ConfigureAwait(false);
            result.Add(new TrialBalanceRow(account.Id, account.Code, account.Name, balance.DebitTotal, balance.CreditTotal, balance.SignedBalance));
        }
        return result;
    }
}
