using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Android;

public sealed record MobileItem(Guid Id, string Sku, string Name, string? Barcode, Guid UnitOfMeasureId, ItemKind ItemKind);
public sealed record MobilePartner(Guid Id, string Code, string Name, Guid ControlAccountId, decimal CreditLimit);
public sealed record MobileAccount(Guid Id, string Code, string Name);
public sealed record MobileWarehouse(Guid Id, string Code, string Name);
public sealed record MobileSaleLine(Guid ItemId, Guid UnitOfMeasureId, decimal Quantity, decimal BaseUnitsPerUnit, decimal UnitPrice);
public sealed record MobileStatementRow(DateTimeOffset DateUtc, string Reference, string Description, decimal Debit, decimal Credit, decimal RunningBalance);
public sealed record MobileInventoryResult(MobileItem Item, StockPosition? Position, IReadOnlyList<ItemMovement> Movements);
public sealed record ItemMovement(DateTimeOffset DateUtc, string Source, decimal QuantityDelta, decimal InventoryValueDelta, decimal UnitCost);

public sealed class MobileAccountingFacade
{
    private readonly LocalAccountingDbContext _db;
    private readonly AccountingPostingEngine _posting;
    private readonly IAccountingQueryService _queries;
    private readonly LocalTempNumberGenerator _numbers;
    private readonly UserContext _user;

    public MobileAccountingFacade(LocalAccountingDbContext db, AccountingPostingEngine posting, IAccountingQueryService queries,
        LocalTempNumberGenerator numbers, UserContext user)
    {
        _db = db; _posting = posting; _queries = queries; _numbers = numbers; _user = user;
    }

    public async Task<MobileItem?> FindItemAsync(string code, CancellationToken ct = default)
    {
        var value = code.Trim();
        if (value.Length == 0) return null;
        return await _db.Items.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive
                     && (x.Sku == value || x.Barcode == value || x.Name == value))
            .Select(x => new MobileItem(x.Id, x.Sku, x.Name, x.Barcode, x.BaseUnitOfMeasureId, x.ItemKind))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public Task<List<MobilePartner>> GetCustomersAsync(CancellationToken ct = default) =>
        _db.BusinessPartners.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive
                     && (x.PartnerType == PartnerType.Customer || x.PartnerType == PartnerType.Both))
            .OrderBy(x => x.Name)
            .Select(x => new MobilePartner(x.Id, x.Code, x.Name, x.ControlAccountId, x.CreditLimit))
            .ToListAsync(ct);

    public Task<List<MobileAccount>> GetPostableAccountsAsync(CancellationToken ct = default) =>
        _db.Accounts.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive && x.IsPostable)
            .OrderBy(x => x.Code)
            .Select(x => new MobileAccount(x.Id, x.Code, x.Name))
            .ToListAsync(ct);

    public Task<List<MobileWarehouse>> GetWarehousesAsync(CancellationToken ct = default) =>
        _db.Warehouses.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderBy(x => x.Code)
            .Select(x => new MobileWarehouse(x.Id, x.Code, x.Name))
            .ToListAsync(ct);

    public async Task<PostingResult> PostSaleAsync(string moduleKey, string moduleMetadataJson, Guid warehouseId,
        Guid settlementAccountId, Guid? partnerId, IReadOnlyList<MobileSaleLine> lines, CancellationToken ct = default)
    {
        if (lines.Count == 0) throw new InvalidOperationException("الفاتورة لا تحتوي أصنافاً.");
        var number = await _numbers.NextAsync(_user, nameof(SalesInvoice), ct).ConfigureAwait(false);
        var invoice = new SalesInvoice(_user.CompanyId, _user.BranchId, number, moduleKey, DateTimeOffset.UtcNow,
            warehouseId, settlementAccountId, partnerId);
        invoice.SetModuleMetadata(moduleMetadataJson);
        foreach (var line in lines)
            invoice.AddLine(line.ItemId, line.UnitOfMeasureId, line.Quantity, line.BaseUnitsPerUnit, line.UnitPrice);
        _db.SalesInvoices.Add(invoice);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await _posting.PostSalesInvoiceAsync(new PostSalesInvoiceCommand(invoice.Id,
            new PostingCommandContext(_user, UuidV7.NewGuid(), $"mobile-sale:{invoice.Id:N}")), ct).ConfigureAwait(false);
    }

    public async Task<PostingResult> CollectAsync(Guid customerId, Guid cashAccountId, decimal amount, string description, CancellationToken ct = default)
    {
        var partner = await _db.BusinessPartners.AsNoTracking()
            .SingleAsync(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.Id == customerId, ct)
            .ConfigureAwait(false);
        var number = await _numbers.NextAsync(_user, nameof(ReceiptVoucher), ct).ConfigureAwait(false);
        var voucher = new ReceiptVoucher(_user.CompanyId, _user.BranchId, number, "CORE", DateTimeOffset.UtcNow,
            cashAccountId, partner.ControlAccountId, amount, description, customerId);
        _db.ReceiptVouchers.Add(voucher);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await _posting.PostReceiptVoucherAsync(new PostReceiptVoucherCommand(voucher.Id,
            new PostingCommandContext(_user, UuidV7.NewGuid(), $"mobile-receipt:{voucher.Id:N}")), ct).ConfigureAwait(false);
    }

    public async Task<(decimal Balance, IReadOnlyList<MobileStatementRow> Rows)> GetCustomerStatementAsync(Guid customerId, CancellationToken ct = default)
    {
        var partner = await _db.BusinessPartners.AsNoTracking()
            .SingleAsync(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.Id == customerId, ct)
            .ConfigureAwait(false);
        var account = await _db.Accounts.AsNoTracking().SingleAsync(x => x.Id == partner.ControlAccountId, ct).ConfigureAwait(false);

        var sourceIds = new HashSet<Guid>();
        foreach (var id in await _db.SalesInvoices.AsNoTracking().Where(x => x.BusinessPartnerId == customerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.PurchaseInvoices.AsNoTracking().Where(x => x.BusinessPartnerId == customerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.ReceiptVouchers.AsNoTracking().Where(x => x.BusinessPartnerId == customerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.PaymentVouchers.AsNoTracking().Where(x => x.BusinessPartnerId == customerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);

        var rows = await (from entry in _db.JournalEntries.AsNoTracking()
                          join line in _db.JournalLines.AsNoTracking() on entry.Id equals line.JournalEntryId
                          where sourceIds.Contains(entry.SourceDocumentId) && line.AccountId == partner.ControlAccountId
                                && entry.Status == PostingStatus.Posted
                          orderby entry.EntryDateUtc, line.Id
                          select new { entry.EntryDateUtc, entry.Reference, entry.Description, line.Debit, line.Credit })
            .ToListAsync(ct).ConfigureAwait(false);

        decimal running = 0m;
        var result = new List<MobileStatementRow>(rows.Count);
        foreach (var row in rows)
        {
            running += account.NormalBalance == NormalBalance.Debit ? row.Debit - row.Credit : row.Credit - row.Debit;
            result.Add(new MobileStatementRow(row.EntryDateUtc, row.Reference, row.Description, row.Debit, row.Credit, running));
        }
        return (running, result);
    }

    public async Task<MobileInventoryResult?> GetInventoryAsync(string code, Guid warehouseId, CancellationToken ct = default)
    {
        var item = await FindItemAsync(code, ct).ConfigureAwait(false);
        if (item is null) return null;
        if (item.ItemKind == ItemKind.Service) return new MobileInventoryResult(item, null, Array.Empty<ItemMovement>());

        var position = await _queries.GetStockPositionAsync(_user, item.Id, warehouseId, ct).ConfigureAwait(false);
        var movements = await _db.StockLedgerEntries.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.ItemId == item.Id && x.WarehouseId == warehouseId)
            .OrderByDescending(x => x.PostedAtUtc).Take(50)
            .Select(x => new ItemMovement(x.PostedAtUtc, x.SourceDocumentType, x.QuantityDelta, x.InventoryValueDelta, x.UnitCost))
            .ToListAsync(ct).ConfigureAwait(false);
        return new MobileInventoryResult(item, position, movements);
    }
}
