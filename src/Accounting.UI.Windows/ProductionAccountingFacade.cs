using System.Data;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public sealed record CompanyProfileRow(string CompanyName, string BranchName, string CurrencyCode);
public sealed record DashboardSnapshot(decimal SalesTotal, decimal CashAndBankBalance, int ItemCount, decimal InventoryValue, int PendingOutbox, IReadOnlyList<RecentJournalRow> RecentJournals);
public sealed record RecentJournalRow(Guid Id, DateTimeOffset DateUtc, string Reference, string Description, decimal Debit, decimal Credit);
public sealed record WarehouseLookup(Guid Id, string Code, string Name);
public sealed record InventoryGridRow(Guid ItemId, string Sku, string Name, string UnitName, decimal Quantity, decimal InventoryValue, decimal AverageCost, string CartonText);
public sealed record AccountTreeRow(Guid Id, string Code, string Name, Guid? ParentAccountId, AccountType Type, NormalBalance NormalBalance, bool IsPostable);
public sealed record JournalHeaderRow(Guid Id, DateTimeOffset DateUtc, string Reference, string Description, string SourceType, Guid SourceDocumentId, decimal Debit, decimal Credit, bool IsReversal);
public sealed record JournalDetailRow(Guid Id, string AccountCode, string AccountName, string Memo, decimal Debit, decimal Credit);
public sealed record VoucherHistoryRow(Guid Id, string Kind, string LocalNumber, string OfficialNumber, DateTimeOffset DateUtc, string PartnerName, decimal Amount, string Description);
public sealed record PartnerStatementResult(string PartnerName, decimal Balance, IReadOnlyList<LedgerRow> Rows);
public sealed record OutboxUiRow(Guid Id, string EventType, string Status, int RetryCount, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastAttemptAtUtc);
public sealed record ConflictUiRow(Guid Id, string Code, string Message, DateTimeOffset ServerTimestampUtc, bool IsResolved);
public sealed record SyncCenterSnapshot(IReadOnlyList<OutboxUiRow> Outbox, IReadOnlyList<ConflictUiRow> Conflicts, DateTimeOffset? LastSync, string? LastError);
public sealed record ManualJournalLine(Guid AccountId, decimal Debit, decimal Credit, string Memo);
public sealed record UnitLookup(Guid Id, string Code, string Name);

public sealed partial class WindowsAccountingFacade
{
    public async Task<CompanyProfileRow> GetCompanyProfileAsync(CancellationToken ct = default)
    {
        var company = await _db.Companies.AsNoTracking().SingleAsync(x => x.Id == _user.CompanyId, ct).ConfigureAwait(false);
        var branch = await _db.Branches.AsNoTracking().SingleAsync(x => x.Id == _user.BranchId, ct).ConfigureAwait(false);
        return new(company.Name, branch.Name, company.BaseCurrencyCode);
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        var postedSalesIds = _db.SalesInvoices.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId
                     && x.Status == PostingStatus.Posted
                     && x.DocumentDateUtc >= new DateTimeOffset(today, TimeSpan.Zero)
                     && x.DocumentDateUtc < new DateTimeOffset(tomorrow, TimeSpan.Zero))
            .Select(x => x.Id);
        var sales = await _db.SalesInvoiceLines.AsNoTracking()
            .Where(x => postedSalesIds.Contains(x.SalesInvoiceId))
            .SumAsync(x => (decimal?)(x.Quantity * x.UnitPrice), ct).ConfigureAwait(false) ?? 0m;

        var cashAccountIds = await _db.Accounts.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId
                     && x.IsPostable && (x.Code.StartsWith("111") || x.Code.StartsWith("112")))
            .Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        decimal cash = 0m;
        foreach (var id in cashAccountIds)
            cash += (await _queries.GetAccountBalanceAsync(_user, id, ct).ConfigureAwait(false)).SignedBalance;

        var itemCount = await _db.Items.AsNoTracking()
            .CountAsync(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive, ct).ConfigureAwait(false);
        var inventoryValue = await _db.StockLedgerEntries.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .SumAsync(x => (decimal?)x.InventoryValueDelta, ct).ConfigureAwait(false) ?? 0m;
        var pending = await _db.OutboxMessages.AsNoTracking()
            .CountAsync(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId
                          && (x.Status == OutboxStatus.Pending || x.Status == OutboxStatus.Failed), ct).ConfigureAwait(false);

        var recentBase = await _db.JournalEntries.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.Status == PostingStatus.Posted)
            .OrderByDescending(x => x.EntryDateUtc).ThenByDescending(x => x.Id).Take(10)
            .Select(x => new { x.Id, x.EntryDateUtc, x.Reference, x.Description }).ToListAsync(ct).ConfigureAwait(false);
        var recent = new List<RecentJournalRow>(recentBase.Count);
        foreach (var row in recentBase)
        {
            var sums = await _db.JournalLines.AsNoTracking().Where(x => x.JournalEntryId == row.Id)
                .GroupBy(_ => 1).Select(g => new { Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            recent.Add(new(row.Id, row.EntryDateUtc, row.Reference, row.Description, sums?.Debit ?? 0m, sums?.Credit ?? 0m));
        }

        return new(sales, cash, itemCount, inventoryValue, pending, recent);
    }

    public Task<List<WarehouseLookup>> GetWarehousesAsync(CancellationToken ct = default) =>
        _db.Warehouses.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderBy(x => x.Code)
            .Select(x => new WarehouseLookup(x.Id, x.Code, x.Name))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<InventoryGridRow>> GetInventoryGridAsync(Guid? warehouseId = null, CancellationToken ct = default)
    {
        var warehouse = warehouseId ?? await _db.Warehouses.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderBy(x => x.Code).Select(x => x.Id).FirstAsync(ct).ConfigureAwait(false);

        var items = await (from item in _db.Items.AsNoTracking()
                           join unit in _db.UnitOfMeasures.AsNoTracking() on item.BaseUnitOfMeasureId equals unit.Id
                           where item.CompanyId == _user.CompanyId && item.BranchId == _user.BranchId && item.IsActive
                           orderby item.Name
                           select new { item.Id, item.Sku, item.Name, UnitName = unit.Name, item.ItemKind })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<InventoryGridRow>(items.Count);
        foreach (var item in items)
        {
            decimal qty = 0m, value = 0m, avg = 0m;
            if (item.ItemKind == ItemKind.Stock)
            {
                var pos = await _queries.GetStockPositionAsync(_user, item.Id, warehouse, ct).ConfigureAwait(false);
                qty = pos.QuantityOnHand; value = pos.InventoryValue; avg = pos.AverageUnitCost;
            }
            var carton = await (from c in _db.ItemUnitConversions.AsNoTracking()
                                join u in _db.UnitOfMeasures.AsNoTracking() on c.UnitOfMeasureId equals u.Id
                                where c.CompanyId == _user.CompanyId && c.BranchId == _user.BranchId && c.ItemId == item.Id
                                orderby c.BaseUnitsPerUnit descending
                                select new { u.Name, c.BaseUnitsPerUnit }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var cartonText = carton is null ? "-" : $"{qty / carton.BaseUnitsPerUnit:N2} {carton.Name} ({carton.BaseUnitsPerUnit:N2} {item.UnitName})";
            result.Add(new(item.Id, item.Sku, item.Name, item.UnitName, qty, value, avg, cartonText));
        }
        return result;
    }

    public Task<List<AccountTreeRow>> GetAccountTreeAsync(CancellationToken ct = default) =>
        _db.Accounts.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.IsActive)
            .OrderBy(x => x.Code)
            .Select(x => new AccountTreeRow(x.Id, x.Code, x.Name, x.ParentAccountId, x.AccountType, x.NormalBalance, x.IsPostable))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<JournalHeaderRow>> GetRecentJournalsAsync(int take = 200, CancellationToken ct = default)
    {
        var rows = await _db.JournalEntries.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.Status == PostingStatus.Posted)
            .OrderByDescending(x => x.EntryDateUtc).Take(Math.Clamp(take, 1, 500))
            .Select(x => new { x.Id, x.EntryDateUtc, x.Reference, x.Description, x.SourceDocumentType, x.SourceDocumentId, x.ReversedEntryId })
            .ToListAsync(ct).ConfigureAwait(false);
        var result = new List<JournalHeaderRow>(rows.Count);
        foreach (var row in rows)
        {
            var sums = await _db.JournalLines.AsNoTracking().Where(x => x.JournalEntryId == row.Id)
                .GroupBy(_ => 1).Select(g => new { Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            result.Add(new(row.Id, row.EntryDateUtc, row.Reference, row.Description, row.SourceDocumentType, row.SourceDocumentId,
                sums?.Debit ?? 0m, sums?.Credit ?? 0m, row.ReversedEntryId.HasValue));
        }
        return result;
    }

    public Task<List<JournalDetailRow>> GetJournalDetailsAsync(Guid journalId, CancellationToken ct = default) =>
        (from line in _db.JournalLines.AsNoTracking()
         join account in _db.Accounts.AsNoTracking() on line.AccountId equals account.Id
         where line.JournalEntryId == journalId && line.CompanyId == _user.CompanyId && line.BranchId == _user.BranchId
         orderby account.Code
         select new JournalDetailRow(line.Id, account.Code, account.Name, line.Memo, line.Debit, line.Credit)).ToListAsync(ct);

    public async Task<IReadOnlyList<VoucherHistoryRow>> GetVoucherHistoryAsync(CancellationToken ct = default)
    {
        var official = _db.OfficialSequenceAssignments.AsNoTracking();
        var partners = _db.BusinessPartners.AsNoTracking();
        var receipts = await (from v in _db.ReceiptVouchers.AsNoTracking()
                              join p in partners on v.BusinessPartnerId equals (Guid?)p.Id into pg from p in pg.DefaultIfEmpty()
                              join o in official on v.Id equals o.DocumentId into og from o in og.DefaultIfEmpty()
                              where v.CompanyId == _user.CompanyId && v.BranchId == _user.BranchId && v.Status == PostingStatus.Posted
                              select new VoucherHistoryRow(v.Id, "سند قبض", v.LocalTempNumber, o == null ? "-" : o.OfficialSequenceNumber,
                                  v.DocumentDateUtc, p == null ? "-" : p.Name, v.Amount, v.Description)).ToListAsync(ct).ConfigureAwait(false);
        var payments = await (from v in _db.PaymentVouchers.AsNoTracking()
                              join p in partners on v.BusinessPartnerId equals (Guid?)p.Id into pg from p in pg.DefaultIfEmpty()
                              join o in official on v.Id equals o.DocumentId into og from o in og.DefaultIfEmpty()
                              where v.CompanyId == _user.CompanyId && v.BranchId == _user.BranchId && v.Status == PostingStatus.Posted
                              select new VoucherHistoryRow(v.Id, "سند صرف", v.LocalTempNumber, o == null ? "-" : o.OfficialSequenceNumber,
                                  v.DocumentDateUtc, p == null ? "-" : p.Name, v.Amount, v.Description)).ToListAsync(ct).ConfigureAwait(false);
        return receipts.Concat(payments).OrderByDescending(x => x.DateUtc).Take(300).ToArray();
    }

    public async Task<PartnerStatementResult> GetPartnerStatementAsync(Guid partnerId, CancellationToken ct = default)
    {
        var partner = await _db.BusinessPartners.AsNoTracking()
            .SingleAsync(x => x.Id == partnerId && x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId, ct).ConfigureAwait(false);
        var account = await _db.Accounts.AsNoTracking().SingleAsync(x => x.Id == partner.ControlAccountId, ct).ConfigureAwait(false);
        var sourceIds = new HashSet<Guid>();
        foreach (var id in await _db.SalesInvoices.AsNoTracking().Where(x => x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.PurchaseInvoices.AsNoTracking().Where(x => x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.ReceiptVouchers.AsNoTracking().Where(x => x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        foreach (var id in await _db.PaymentVouchers.AsNoTracking().Where(x => x.BusinessPartnerId == partnerId).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false)) sourceIds.Add(id);
        var raw = await (from entry in _db.JournalEntries.AsNoTracking()
                         join line in _db.JournalLines.AsNoTracking() on entry.Id equals line.JournalEntryId
                         where sourceIds.Contains(entry.SourceDocumentId) && line.AccountId == partner.ControlAccountId && entry.Status == PostingStatus.Posted
                         orderby entry.EntryDateUtc, line.Id
                         select new { entry.EntryDateUtc, entry.Reference, entry.Description, line.Debit, line.Credit }).ToListAsync(ct).ConfigureAwait(false);
        decimal running = 0m;
        var rows = new List<LedgerRow>(raw.Count);
        foreach (var row in raw)
        {
            running += account.NormalBalance == NormalBalance.Debit ? row.Debit - row.Credit : row.Credit - row.Debit;
            rows.Add(new LedgerRow(row.EntryDateUtc, row.Reference, row.Description, row.Debit, row.Credit, running));
        }
        return new(partner.Name, running, rows);
    }

    public async Task<SyncCenterSnapshot> GetSyncCenterAsync(CancellationToken ct = default)
    {
        var outbox = await _db.OutboxMessages.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(200)
            .Select(x => new OutboxUiRow(x.Id, x.EventType, x.Status.ToString(), x.RetryCount, x.CreatedAtUtc, x.LastAttemptAtUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        var conflicts = await _db.LocalSyncConflicts.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderByDescending(x => x.ServerTimestampUtc).Take(200)
            .Select(x => new ConflictUiRow(x.Id, x.ConflictCode, x.Message, x.ServerTimestampUtc, x.IsResolved))
            .ToListAsync(ct).ConfigureAwait(false);
        var state = await _db.LocalSyncStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.DeviceId == _user.DeviceId, ct).ConfigureAwait(false);
        return new(outbox, conflicts, state?.LastSuccessfulSyncUtc, state?.LastError);
    }

    public async Task<PostingResult> ReverseDocumentAsync(Guid documentId, CancellationToken ct = default) =>
        await _posting.ReversePostedDocumentAsync(new ReversePostedDocumentCommand(documentId,
            new PostingCommandContext(_user, UuidV7.NewGuid(), $"reverse:{documentId:N}:{UuidV7.NewGuid():N}")), ct).ConfigureAwait(false);

    public async Task<Guid> CreateManualJournalAsync(string description, string reference, IReadOnlyList<ManualJournalLine> lines, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("البيان مطلوب.");
        if (lines.Count < 2) throw new InvalidOperationException("القيد اليدوي يحتاج سطرين على الأقل.");
        var policy = await GetPolicyAsync(ct).ConfigureAwait(false);
        var currency = new CurrencyPolicyService();
        var debit = lines.Sum(x => x.Debit); var credit = lines.Sum(x => x.Credit);
        if (currency.RoundMoney(debit, policy) != currency.RoundMoney(credit, policy) || debit <= 0m)
            throw new InvalidOperationException("القيد غير متوازن.");
        await new PermissionGuard(new EfUserAuthorizationReader(_db)).DemandAsync(_user, AccountingPostingEngine.PostPermission, ct).ConfigureAwait(false);

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            var accountIds = lines.Select(x => x.AccountId).Distinct().ToArray();
            var accounts = await _db.Accounts.Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && accountIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
            if (accounts.Count != accountIds.Length) throw new InvalidOperationException("أحد الحسابات غير موجود.");
            var operationId = UuidV7.NewGuid();
            var sourceId = UuidV7.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var journal = new JournalEntry(_user.CompanyId, _user.BranchId, now, description, reference, sourceId, "ManualJournal",
                operationId, $"manual-journal:{sourceId:N}");
            foreach (var line in lines) journal.AddLine(line.AccountId, line.Debit, line.Credit, line.Memo, policy, currency);
            journal.Post(accounts, policy, currency, now);
            _db.JournalEntries.Add(journal);
            _db.AuditLogs.Add(new AuditLog(_user.CompanyId, _user.BranchId, _user.UserId, _user.DeviceId,
                "PostManualJournal", journal.Id, nameof(JournalEntry), "{}", "{}", now));
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return journal.Id;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            throw;
        }
    }
    public Task<List<UnitLookup>> GetUnitsAsync(CancellationToken ct = default) =>
        _db.UnitOfMeasures.AsNoTracking()
            .Where(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId)
            .OrderBy(x => x.Code)
            .Select(x => new UnitLookup(x.Id, x.Code, x.Name))
            .ToListAsync(ct);

    public async Task<Guid> CreateLocalItemAsync(string sku, string name, string? barcode, Guid baseUnitId,
        Guid inventoryAccountId, Guid salesAccountId, Guid cogsAccountId, Guid? cartonUnitId, decimal cartonFactor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("كود الصنف والاسم مطلوبان.");
        if (await _db.Items.AnyAsync(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId && x.Sku == sku.Trim(), ct).ConfigureAwait(false))
            throw new InvalidOperationException("كود الصنف مستخدم مسبقاً.");
        var item = new Item(_user.CompanyId, _user.BranchId, sku.Trim(), name.Trim(), baseUnitId,
            inventoryAccountId, salesAccountId, cogsAccountId, ItemKind.Stock, barcode);
        _db.Items.Add(item);
        if (cartonUnitId.HasValue && cartonUnitId.Value != baseUnitId && cartonFactor > 1m)
            _db.ItemUnitConversions.Add(new ItemUnitConversion(_user.CompanyId, _user.BranchId, item.Id, cartonUnitId.Value, cartonFactor));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return item.Id;
    }

    public async Task ResolveConflictLocallyAsync(Guid conflictId, CancellationToken ct = default)
    {
        var row = await _db.LocalSyncConflicts.SingleAsync(x => x.Id == conflictId && x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId, ct).ConfigureAwait(false);
        row.IsResolved = true;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
