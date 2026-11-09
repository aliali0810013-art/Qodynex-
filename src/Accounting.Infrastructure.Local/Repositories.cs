using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Local;

public sealed class EfAccountingDataStore : IAccountingDataStore
{
    private readonly LocalAccountingDbContext _db;
    public EfAccountingDataStore(LocalAccountingDbContext db) => _db = db;

    public async Task<Company> GetCompanyAsync(Guid companyId, CancellationToken ct) =>
        await _db.Companies.SingleOrDefaultAsync(x => x.Id == companyId && x.CompanyId == companyId, ct) ?? throw new AccountingDomainException("Company not found.");

    public Task<CommercialDocument?> GetDocumentAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        _db.Documents.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct);

    public async Task<SalesInvoice> GetSalesInvoiceAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        await _db.SalesInvoices.Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct) ?? throw new AccountingDomainException("Sales invoice not found.");

    public async Task<PurchaseInvoice> GetPurchaseInvoiceAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        await _db.PurchaseInvoices.Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct) ?? throw new AccountingDomainException("Purchase invoice not found.");

    public async Task<ReceiptVoucher> GetReceiptVoucherAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        await _db.ReceiptVouchers.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct) ?? throw new AccountingDomainException("Receipt voucher not found.");

    public async Task<PaymentVoucher> GetPaymentVoucherAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        await _db.PaymentVouchers.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct) ?? throw new AccountingDomainException("Payment voucher not found.");

    public async Task<Warehouse> GetWarehouseAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        await _db.Warehouses.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct) ?? throw new AccountingDomainException("Warehouse not found.");

    public async Task<Item> GetItemAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        await _db.Items.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct) ?? throw new AccountingDomainException("Item not found.");

    public async Task<BusinessPartner> GetBusinessPartnerAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        await _db.BusinessPartners.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.Id == id, ct) ?? throw new AccountingDomainException("Business partner not found.");

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(Guid c, Guid b, IEnumerable<Guid> ids, CancellationToken ct)
    { var list = ids.Distinct().ToArray(); return await _db.Accounts.Where(x => x.CompanyId == c && x.BranchId == b && list.Contains(x.Id)).ToListAsync(ct); }

    public async Task<IReadOnlyList<StockLedgerEntry>> GetStockLedgerAsync(Guid c, Guid b, Guid itemId, Guid warehouseId, CancellationToken ct) =>
        await _db.StockLedgerEntries.AsNoTracking().Where(x => x.CompanyId == c && x.BranchId == b && x.ItemId == itemId && x.WarehouseId == warehouseId).OrderBy(x => x.PostedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<JournalLine>> GetPostedJournalLinesAsync(Guid c, Guid b, Guid accountId, CancellationToken ct) =>
        await (from line in _db.JournalLines.AsNoTracking()
               join entry in _db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
               where line.CompanyId == c && line.BranchId == b && line.AccountId == accountId && entry.Status == PostingStatus.Posted
               orderby entry.EntryDateUtc, line.Id
               select line).ToListAsync(ct);

    public async Task<IReadOnlyList<JournalLine>> GetPostedJournalLinesForBusinessPartnerAsync(Guid c, Guid b, Guid partnerId, Guid accountId, CancellationToken ct)
    {
        var sourceIds = new HashSet<Guid>();
        foreach (var id in await _db.SalesInvoices.AsNoTracking().Where(x => x.CompanyId == c && x.BranchId == b && x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.PurchaseInvoices.AsNoTracking().Where(x => x.CompanyId == c && x.BranchId == b && x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.ReceiptVouchers.AsNoTracking().Where(x => x.CompanyId == c && x.BranchId == b && x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.PaymentVouchers.AsNoTracking().Where(x => x.CompanyId == c && x.BranchId == b && x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);

        if (sourceIds.Count == 0) return Array.Empty<JournalLine>();
        return await (from line in _db.JournalLines.AsNoTracking()
                      join entry in _db.JournalEntries.AsNoTracking() on line.JournalEntryId equals entry.Id
                      where line.CompanyId == c && line.BranchId == b && line.AccountId == accountId
                            && entry.Status == PostingStatus.Posted && sourceIds.Contains(entry.SourceDocumentId)
                      orderby entry.EntryDateUtc, line.Id
                      select line).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StockLedgerEntry>> GetStockBySourceDocumentAsync(Guid c, Guid b, Guid documentId, CancellationToken ct) =>
        await _db.StockLedgerEntries.AsNoTracking().Where(x => x.CompanyId == c && x.BranchId == b && x.SourceDocumentId == documentId).OrderBy(x => x.PostedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);

    public Task<JournalEntry?> FindJournalByIdempotencyKeyAsync(Guid c, Guid b, string key, CancellationToken ct) =>
        _db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.IdempotencyKey == key, ct);

    public Task<JournalEntry?> GetJournalBySourceDocumentAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        _db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.SourceDocumentId == id && x.ReversedEntryId == null, ct);

    public Task<DocumentReversalRecord?> FindReversalByIdempotencyKeyAsync(Guid c, Guid b, string key, CancellationToken ct) =>
        _db.DocumentReversalRecords.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.IdempotencyKey == key, ct);

    public Task<DocumentReversalRecord?> GetReversalForDocumentAsync(Guid c, Guid b, Guid id, CancellationToken ct) =>
        _db.DocumentReversalRecords.SingleOrDefaultAsync(x => x.CompanyId == c && x.BranchId == b && x.SourceDocumentId == id, ct);

    public void Add<TEntity>(TEntity entity) where TEntity : class => _db.Add(entity);
}

public sealed class EfUserAuthorizationReader : IUserAuthorizationReader
{
    private readonly LocalAccountingDbContext _db;
    public EfUserAuthorizationReader(LocalAccountingDbContext db) => _db = db;
    public Task<bool> HasPermissionAsync(UserContext u, string code, CancellationToken ct)
    {
        var query = from user in _db.AppUsers.AsNoTracking()
                    join ur in _db.AppUserRoles.AsNoTracking() on user.Id equals ur.UserId
                    join rp in _db.RolePermissionAssignments.AsNoTracking() on ur.RoleId equals rp.RoleId
                    join p in _db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                    where user.Id == u.UserId && user.CompanyId == u.CompanyId && user.BranchId == u.BranchId && user.IsActive
                       && p.CompanyId == u.CompanyId && p.BranchId == u.BranchId && p.Code == code
                    select p.Id;
        return query.AnyAsync(ct);
    }
}
