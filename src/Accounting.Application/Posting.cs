using System.Security;
using System.Text.Json;
using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Application;

public sealed class PermissionGuard : IPermissionGuard
{
    private readonly IUserAuthorizationReader _reader;
    public PermissionGuard(IUserAuthorizationReader reader) => _reader = reader;
    public async Task DemandAsync(UserContext userContext, string permissionCode, CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated) throw new SecurityException("Authenticated user context is required.");
        if (!await _reader.HasPermissionAsync(userContext, permissionCode, cancellationToken).ConfigureAwait(false))
            throw new SecurityException($"User {userContext.UserId} lacks permission '{permissionCode}'.");
    }
}

public sealed record PostingCommandContext(UserContext UserContext, Guid OperationId, string IdempotencyKey);
public sealed record PostSalesInvoiceCommand(Guid InvoiceId, PostingCommandContext Context);
public sealed record PostPurchaseInvoiceCommand(Guid InvoiceId, PostingCommandContext Context);
public sealed record PostReceiptVoucherCommand(Guid VoucherId, PostingCommandContext Context);
public sealed record PostPaymentVoucherCommand(Guid VoucherId, PostingCommandContext Context);
public sealed record ReversePostedDocumentCommand(Guid DocumentId, PostingCommandContext Context);
public sealed record PostingResult(Guid DocumentId, Guid JournalEntryId, IReadOnlyList<Guid> StockLedgerEntryIds, Guid OperationId, string IdempotencyKey, bool WasAlreadyPosted);

public sealed class AccountingPostingEngine
{
    public const string PostPermission = "accounting.post";
    public const string ReversePermission = "accounting.reverse";
    private readonly IAccountingDataStore _store;
    private readonly IUnitOfWork _uow;
    private readonly IPermissionGuard _guard;
    private readonly DocumentPostingRuleRegistry _rules;
    private readonly ICurrencyPolicyService _currency;
    private readonly InventoryValuationEngine _inventory;
    private readonly IClock _clock;

    public AccountingPostingEngine(IAccountingDataStore store, IUnitOfWork uow, IPermissionGuard guard, DocumentPostingRuleRegistry rules,
        ICurrencyPolicyService currency, InventoryValuationEngine inventory, IClock clock)
    { _store = store; _uow = uow; _guard = guard; _rules = rules; _currency = currency; _inventory = inventory; _clock = clock; }

    public Task<PostingResult> PostReceiptVoucherAsync(PostReceiptVoucherCommand command, CancellationToken ct = default) =>
        PostVoucherAsync(command.VoucherId, command.Context, true, ct);

    public Task<PostingResult> PostPaymentVoucherAsync(PostPaymentVoucherCommand command, CancellationToken ct = default) =>
        PostVoucherAsync(command.VoucherId, command.Context, false, ct);

    private async Task<PostingResult> PostVoucherAsync(Guid id, PostingCommandContext context, bool receipt, CancellationToken ct)
    {
        ValidateContext(context); await _guard.DemandAsync(context.UserContext, PostPermission, ct).ConfigureAwait(false);
        return await _uow.ExecuteInTransactionAsync(async token =>
        {
            var u = context.UserContext;
            var company = await _store.GetCompanyAsync(u.CompanyId, token).ConfigureAwait(false);
            var policy = company.GetPolicy();
            CommercialDocument document = receipt
                ? await _store.GetReceiptVoucherAsync(u.CompanyId, u.BranchId, id, token).ConfigureAwait(false)
                : await _store.GetPaymentVoucherAsync(u.CompanyId, u.BranchId, id, token).ConfigureAwait(false);
            EnsureTenant(u, document);
            var prior = await Existing(document, context, token).ConfigureAwait(false); if (prior is not null) return prior;
            await _rules.ValidateAsync(document, u, token).ConfigureAwait(false);

            Guid cash, counter; decimal amount; string description;
            if (document is ReceiptVoucher rv) { cash = rv.CashOrBankAccountId; counter = rv.CounterAccountId; amount = rv.Amount; description = rv.Description; }
            else { var pv = (PaymentVoucher)document; cash = pv.CashOrBankAccountId; counter = pv.CounterAccountId; amount = pv.Amount; description = pv.Description; }
            amount = _currency.RoundMoney(amount, policy);
            var accounts = await LoadAccounts(u, new[] { cash, counter }, token).ConfigureAwait(false);
            var now = _clock.UtcNow;
            var journal = CreateJournal(document, context, now, receipt ? "Receipt voucher" : "Payment voucher");
            if (receipt) { journal.AddLine(cash, amount, 0m, description, policy, _currency); journal.AddLine(counter, 0m, amount, description, policy, _currency); }
            else { journal.AddLine(counter, amount, 0m, description, policy, _currency); journal.AddLine(cash, 0m, amount, description, policy, _currency); }
            journal.Post(accounts, policy, _currency, now);
            document.MarkPosted(context.OperationId, context.IdempotencyKey, now); _store.Add(journal);
            AddAuditOutbox(document, journal, Array.Empty<StockLedgerEntry>(), context, now, u, receipt ? "PostReceiptVoucher" : "PostPaymentVoucher");
            return new PostingResult(document.Id, journal.Id, Array.Empty<Guid>(), context.OperationId, context.IdempotencyKey, false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<PostingResult> PostPurchaseInvoiceAsync(PostPurchaseInvoiceCommand command, CancellationToken ct = default)
    {
        ValidateContext(command.Context); await _guard.DemandAsync(command.Context.UserContext, PostPermission, ct).ConfigureAwait(false);
        return await _uow.ExecuteInTransactionAsync(async token =>
        {
            var u = command.Context.UserContext;
            var policy = (await _store.GetCompanyAsync(u.CompanyId, token).ConfigureAwait(false)).GetPolicy();
            var invoice = await _store.GetPurchaseInvoiceAsync(u.CompanyId, u.BranchId, command.InvoiceId, token).ConfigureAwait(false);
            EnsureTenant(u, invoice); var prior = await Existing(invoice, command.Context, token).ConfigureAwait(false); if (prior is not null) return prior;
            if (invoice.Lines.Count == 0) throw new AccountingDomainException("Purchase invoice must contain at least one line.");
            await _rules.ValidateAsync(invoice, u, token).ConfigureAwait(false);

            var items = new Dictionary<Guid, Item>();
            foreach (var itemId in invoice.Lines.Select(x => x.ItemId).Distinct()) items[itemId] = await _store.GetItemAsync(u.CompanyId, u.BranchId, itemId, token).ConfigureAwait(false);
            if (items.Values.Any(x => x.ItemKind == ItemKind.Stock))
                _ = await _store.GetWarehouseAsync(u.CompanyId, u.BranchId, invoice.WarehouseId, token).ConfigureAwait(false);
            var accountIds = new HashSet<Guid> { invoice.SettlementAccountId };
            foreach (var item in items.Values) accountIds.Add(item.ItemKind == ItemKind.Stock ? item.InventoryAccountId : item.CogsAccountId);
            if (policy.TaxPolicy.Enabled && policy.TaxPolicy.InputTaxAccountId is Guid inputTax) accountIds.Add(inputTax);
            var accounts = await LoadAccounts(u, accountIds, token).ConfigureAwait(false);
            var now = _clock.UtcNow; var journal = CreateJournal(invoice, command.Context, now, "Purchase invoice"); var stock = new List<StockLedgerEntry>();
            decimal grossTotal = 0m, inputTaxTotal = 0m;
            foreach (var line in invoice.Lines)
            {
                var item = items[line.ItemId]; var baseQty = _currency.MultiplyQuantity(line.Quantity, line.BaseUnitsPerUnit, policy);
                var tax = Tax(line.Quantity, line.UnitPrice, policy); var purchaseCost = policy.TaxPolicy.Enabled && policy.TaxPolicy.InputTaxAccountId.HasValue ? tax.Net : tax.Gross;
                grossTotal = _currency.Add(grossTotal, tax.Gross, policy); inputTaxTotal = _currency.Add(inputTaxTotal, tax.Tax, policy);
                if (item.ItemKind == ItemKind.Service)
                {
                    if (item.CogsAccountId == Guid.Empty) throw new AccountingDomainException($"Service item {item.Sku} requires a service cost account.");
                    journal.AddLine(item.CogsAccountId, purchaseCost, 0m, $"Service purchase {item.Sku}", policy, _currency);
                    continue;
                }

                var unitCost = _currency.Divide(purchaseCost, baseQty, policy, Math.Max(policy.CurrencyScale + 6, 6));
                journal.AddLine(item.InventoryAccountId, purchaseCost, 0m, $"Purchase {item.Sku}", policy, _currency);
                var movement = new StockLedgerEntry(u.CompanyId, u.BranchId, item.Id, invoice.WarehouseId, invoice.Id, nameof(PurchaseInvoice), command.Context.OperationId,
                    StockMovementType.PurchaseReceipt, baseQty, purchaseCost, unitCost, now); stock.Add(movement); _store.Add(movement);
            }
            if (inputTaxTotal > 0m && policy.TaxPolicy.InputTaxAccountId is Guid taxAccount) journal.AddLine(taxAccount, inputTaxTotal, 0m, "Input tax", policy, _currency);
            journal.AddLine(invoice.SettlementAccountId, 0m, grossTotal, "Purchase settlement", policy, _currency);
            journal.Post(accounts, policy, _currency, now); invoice.MarkPosted(command.Context.OperationId, command.Context.IdempotencyKey, now); _store.Add(journal);
            AddAuditOutbox(invoice, journal, stock, command.Context, now, u, "PostPurchaseInvoice");
            return new PostingResult(invoice.Id, journal.Id, stock.Select(x => x.Id).ToArray(), command.Context.OperationId, command.Context.IdempotencyKey, false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<PostingResult> PostSalesInvoiceAsync(PostSalesInvoiceCommand command, CancellationToken ct = default)
    {
        ValidateContext(command.Context); await _guard.DemandAsync(command.Context.UserContext, PostPermission, ct).ConfigureAwait(false);
        return await _uow.ExecuteInTransactionAsync(async token =>
        {
            var u = command.Context.UserContext;
            var policy = (await _store.GetCompanyAsync(u.CompanyId, token).ConfigureAwait(false)).GetPolicy();
            var invoice = await _store.GetSalesInvoiceAsync(u.CompanyId, u.BranchId, command.InvoiceId, token).ConfigureAwait(false);
            EnsureTenant(u, invoice); var prior = await Existing(invoice, command.Context, token).ConfigureAwait(false); if (prior is not null) return prior;
            if (invoice.Lines.Count == 0) throw new AccountingDomainException("Sales invoice must contain at least one line.");
            await _rules.ValidateAsync(invoice, u, token).ConfigureAwait(false);
            var items = new Dictionary<Guid, Item>(); foreach (var itemId in invoice.Lines.Select(x => x.ItemId).Distinct()) items[itemId] = await _store.GetItemAsync(u.CompanyId, u.BranchId, itemId, token).ConfigureAwait(false);
            Warehouse? warehouse = null;
            if (items.Values.Any(x => x.ItemKind == ItemKind.Stock))
                warehouse = await _store.GetWarehouseAsync(u.CompanyId, u.BranchId, invoice.WarehouseId, token).ConfigureAwait(false);

            var accountIds = new HashSet<Guid> { invoice.SettlementAccountId };
            foreach (var item in items.Values)
            {
                accountIds.Add(item.SalesRevenueAccountId);
                if (item.ItemKind == ItemKind.Stock) { accountIds.Add(item.InventoryAccountId); accountIds.Add(item.CogsAccountId); }
            }
            if (policy.TaxPolicy.Enabled && policy.TaxPolicy.OutputTaxAccountId is Guid outputTax) accountIds.Add(outputTax);
            var accounts = await LoadAccounts(u, accountIds, token).ConfigureAwait(false);
            var now = _clock.UtcNow; var journal = CreateJournal(invoice, command.Context, now, "Sales invoice"); var stock = new List<StockLedgerEntry>();
            decimal grossTotal = 0m, outputTaxTotal = 0m; var revenue = new Dictionary<Guid, decimal>();
            foreach (var line in invoice.Lines)
            {
                var item = items[line.ItemId]; var tax = Tax(line.Quantity, line.UnitPrice, policy);
                grossTotal = _currency.Add(grossTotal, tax.Gross, policy); outputTaxTotal = _currency.Add(outputTaxTotal, tax.Tax, policy);
                revenue[item.SalesRevenueAccountId] = revenue.TryGetValue(item.SalesRevenueAccountId, out var current) ? _currency.Add(current, tax.Net, policy) : tax.Net;
            }
            journal.AddLine(invoice.SettlementAccountId, grossTotal, 0m, "Sales settlement", policy, _currency);
            foreach (var r in revenue) journal.AddLine(r.Key, 0m, r.Value, "Sales revenue", policy, _currency);
            if (outputTaxTotal > 0m && policy.TaxPolicy.OutputTaxAccountId is Guid taxAccount) journal.AddLine(taxAccount, 0m, outputTaxTotal, "Output tax", policy, _currency);

            foreach (var group in invoice.Lines.GroupBy(x => x.ItemId))
            {
                var item = items[group.Key];
                if (item.ItemKind == ItemKind.Service) continue;
                if (warehouse is null) throw new AccountingDomainException("Warehouse is required for stock items.");
                decimal baseQty = 0m;
                foreach (var line in group) baseQty = _currency.AddQuantity(baseQty, _currency.MultiplyQuantity(line.Quantity, line.BaseUnitsPerUnit, policy), policy);
                var ledger = await _store.GetStockLedgerAsync(u.CompanyId, u.BranchId, item.Id, invoice.WarehouseId, token).ConfigureAwait(false);
                var issue = _inventory.CalculateIssue(ledger, baseQty, policy, warehouse.AllowsNegativeStock(policy));
                var movement = new StockLedgerEntry(u.CompanyId, u.BranchId, item.Id, invoice.WarehouseId, invoice.Id, nameof(SalesInvoice), command.Context.OperationId,
                    StockMovementType.SaleIssue, _currency.SubtractQuantity(0m, baseQty, policy), _currency.Subtract(0m, issue.TotalCost, policy), issue.UnitCost, now);
                stock.Add(movement); _store.Add(movement);
                if (issue.TotalCost > 0m)
                {
                    journal.AddLine(item.CogsAccountId, issue.TotalCost, 0m, $"COGS {item.Sku}", policy, _currency);
                    journal.AddLine(item.InventoryAccountId, 0m, issue.TotalCost, $"Inventory issue {item.Sku}", policy, _currency);
                }
            }
            journal.Post(accounts, policy, _currency, now); invoice.MarkPosted(command.Context.OperationId, command.Context.IdempotencyKey, now); _store.Add(journal);
            AddAuditOutbox(invoice, journal, stock, command.Context, now, u, "PostSalesInvoice");
            return new PostingResult(invoice.Id, journal.Id, stock.Select(x => x.Id).ToArray(), command.Context.OperationId, command.Context.IdempotencyKey, false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<PostingResult> ReversePostedDocumentAsync(ReversePostedDocumentCommand command, CancellationToken ct = default)
    {
        ValidateContext(command.Context); await _guard.DemandAsync(command.Context.UserContext, ReversePermission, ct).ConfigureAwait(false);
        return await _uow.ExecuteInTransactionAsync(async token =>
        {
            var u = command.Context.UserContext;
            var document = await _store.GetDocumentAsync(u.CompanyId, u.BranchId, command.DocumentId, token).ConfigureAwait(false) ?? throw new AccountingDomainException("Document not found.");
            EnsureTenant(u, document); if (document.Status != PostingStatus.Posted) throw new AccountingDomainException("Only posted documents can be reversed.");
            var prior = await _store.FindReversalByIdempotencyKeyAsync(u.CompanyId, u.BranchId, command.Context.IdempotencyKey, token).ConfigureAwait(false);
            if (prior is not null)
            {
                var rows = await _store.GetStockBySourceDocumentAsync(u.CompanyId, u.BranchId, document.Id, token).ConfigureAwait(false);
                return new PostingResult(document.Id, prior.ReversalJournalEntryId, rows.Where(x => x.OperationId == prior.OperationId && x.MovementType == StockMovementType.Reversal).Select(x => x.Id).ToArray(), prior.OperationId, prior.IdempotencyKey, true);
            }
            if (await _store.GetReversalForDocumentAsync(u.CompanyId, u.BranchId, document.Id, token).ConfigureAwait(false) is not null) throw new AccountingDomainException("Document has already been reversed.");
            var policy = (await _store.GetCompanyAsync(u.CompanyId, token).ConfigureAwait(false)).GetPolicy();
            var originalJournal = await _store.GetJournalBySourceDocumentAsync(u.CompanyId, u.BranchId, document.Id, token).ConfigureAwait(false) ?? throw new AccountingDomainException("Original journal not found.");
            var accounts = await LoadAccounts(u, originalJournal.Lines.Select(x => x.AccountId), token).ConfigureAwait(false);
            var now = _clock.UtcNow; var reversalJournal = originalJournal.CreateReversal(command.Context.OperationId, command.Context.IdempotencyKey, now, accounts, policy, _currency); _store.Add(reversalJournal);
            var originals = (await _store.GetStockBySourceDocumentAsync(u.CompanyId, u.BranchId, document.Id, token).ConfigureAwait(false)).Where(x => x.MovementType != StockMovementType.Reversal).ToArray();
            var reversalStock = new List<StockLedgerEntry>();
            foreach (var row in originals)
            {
                var reversed = new StockLedgerEntry(u.CompanyId, u.BranchId, row.ItemId, row.WarehouseId, document.Id, $"{document.GetType().Name}.Reversal", command.Context.OperationId,
                    StockMovementType.Reversal, _currency.SubtractQuantity(0m, row.QuantityDelta, policy), _currency.Subtract(0m, row.InventoryValueDelta, policy), row.UnitCost, now, row.Id);
                reversalStock.Add(reversed); _store.Add(reversed);
            }
            _store.Add(new DocumentReversalRecord(u.CompanyId, u.BranchId, document.Id, document.GetType().Name, originalJournal.Id, reversalJournal.Id,
                command.Context.OperationId, command.Context.IdempotencyKey, now));
            AddAuditOutbox(document, reversalJournal, reversalStock, command.Context, now, u, "ReversePostedDocument");
            return new PostingResult(document.Id, reversalJournal.Id, reversalStock.Select(x => x.Id).ToArray(), command.Context.OperationId, command.Context.IdempotencyKey, false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<PostingResult?> Existing(CommercialDocument document, PostingCommandContext context, CancellationToken ct)
    {
        if (document.Status != PostingStatus.Posted) return null;
        if (document.PostingIdempotencyKey != context.IdempotencyKey) throw new AccountingDomainException("Document is already posted with another idempotency key.");
        var journal = await _store.FindJournalByIdempotencyKeyAsync(document.CompanyId, document.BranchId, context.IdempotencyKey, ct).ConfigureAwait(false) ?? throw new AccountingDomainException("Idempotent journal is missing.");
        var stock = await _store.GetStockBySourceDocumentAsync(document.CompanyId, document.BranchId, document.Id, ct).ConfigureAwait(false);
        var operation = document.PostingOperationId ?? context.OperationId;
        return new PostingResult(document.Id, journal.Id, stock.Where(x => x.OperationId == operation && x.MovementType != StockMovementType.Reversal).Select(x => x.Id).ToArray(), operation, context.IdempotencyKey, true);
    }

    private JournalEntry CreateJournal(CommercialDocument d, PostingCommandContext c, DateTimeOffset now, string description) =>
        new(d.CompanyId, d.BranchId, d.DocumentDateUtc, description, d.LocalTempNumber, d.Id, d.GetType().Name, c.OperationId, c.IdempotencyKey);

    private async Task<IReadOnlyDictionary<Guid, Account>> LoadAccounts(UserContext u, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var required = ids.Distinct().ToArray(); var rows = await _store.GetAccountsAsync(u.CompanyId, u.BranchId, required, ct).ConfigureAwait(false);
        if (rows.Count != required.Length) throw new AccountingDomainException("One or more accounts are missing.");
        return rows.ToDictionary(x => x.Id);
    }

    private (decimal Net, decimal Tax, decimal Gross) Tax(decimal qty, decimal price, CompanyPolicy policy)
    {
        var raw = _currency.Multiply(qty, price, policy);
        if (!policy.TaxPolicy.Enabled || policy.TaxPolicy.Rate == 0m) return (raw, 0m, raw);
        if (policy.TaxPolicy.PricesIncludeTax)
        {
            var net = _currency.Divide(raw, 1m + policy.TaxPolicy.Rate, policy);
            return (net, _currency.Subtract(raw, net, policy), raw);
        }
        var tax = _currency.Multiply(raw, policy.TaxPolicy.Rate, policy); return (raw, tax, _currency.Add(raw, tax, policy));
    }

    private void AddAuditOutbox(CommercialDocument document, JournalEntry journal, IReadOnlyCollection<StockLedgerEntry> stock, PostingCommandContext context, DateTimeOffset now, UserContext u, string action)
    {
        var after = JsonSerializer.Serialize(new { JournalEntryId = journal.Id, StockLedgerEntryIds = stock.Select(x => x.Id).ToArray(), context.OperationId, context.IdempotencyKey });
        _store.Add(new AuditLog(u.CompanyId, u.BranchId, u.UserId, u.DeviceId, action, document.Id, document.GetType().Name, "{}", after, now));
        var payload = JsonSerializer.Serialize(BuildSyncPayload(document, journal, stock));
        _store.Add(new OutboxMessage(u.CompanyId, u.BranchId, context.OperationId, context.IdempotencyKey, payload, action, now));
    }

    private static PostedOperationSyncPayload BuildSyncPayload(CommercialDocument document, JournalEntry journal, IReadOnlyCollection<StockLedgerEntry> stock)
    {
        Guid? partnerId = null, warehouseId = null, primaryAccountId = null, counterAccountId = null;
        decimal? amount = null;
        string? description = null;
        IReadOnlyList<PostedDocumentLineSyncRecord> documentLines = Array.Empty<PostedDocumentLineSyncRecord>();
        switch (document)
        {
            case SalesInvoice sales:
                partnerId = sales.BusinessPartnerId; warehouseId = sales.WarehouseId; primaryAccountId = sales.SettlementAccountId;
                documentLines = sales.Lines.Select(x => new PostedDocumentLineSyncRecord(x.Id, x.ItemId, x.UnitOfMeasureId, x.Quantity, x.BaseUnitsPerUnit, x.UnitPrice)).ToArray();
                break;
            case PurchaseInvoice purchase:
                partnerId = purchase.BusinessPartnerId; warehouseId = purchase.WarehouseId; primaryAccountId = purchase.SettlementAccountId;
                documentLines = purchase.Lines.Select(x => new PostedDocumentLineSyncRecord(x.Id, x.ItemId, x.UnitOfMeasureId, x.Quantity, x.BaseUnitsPerUnit, x.UnitPrice)).ToArray();
                break;
            case ReceiptVoucher receipt:
                partnerId = receipt.BusinessPartnerId; primaryAccountId = receipt.CashOrBankAccountId; counterAccountId = receipt.CounterAccountId; amount = receipt.Amount; description = receipt.Description; break;
            case PaymentVoucher payment:
                partnerId = payment.BusinessPartnerId; primaryAccountId = payment.CashOrBankAccountId; counterAccountId = payment.CounterAccountId; amount = payment.Amount; description = payment.Description; break;
        }

        return new PostedOperationSyncPayload(
            new PostedDocumentSyncRecord(document.Id, document.CompanyId, document.BranchId, document.GetType().Name, document.ModuleKey,
                document.ModuleMetadataJson, document.LocalTempNumber, document.DocumentDateUtc, partnerId, warehouseId, primaryAccountId, counterAccountId, amount, description, documentLines),
            new PostedJournalSyncRecord(journal.Id, journal.CompanyId, journal.BranchId, journal.SourceDocumentId, journal.SourceDocumentType,
                journal.OperationId, journal.IdempotencyKey, journal.EntryDateUtc, journal.Description, journal.Reference, journal.ReversedEntryId,
                journal.Lines.Select(x => new PostedJournalLineSyncRecord(x.Id, x.AccountId, x.Debit, x.Credit, x.Memo)).ToArray()),
            stock.Select(x => new PostedStockLedgerSyncRecord(x.Id, x.ItemId, x.WarehouseId, x.SourceDocumentId, x.SourceDocumentType,
                x.OperationId, (int)x.MovementType, x.QuantityDelta, x.InventoryValueDelta, x.UnitCost, x.ReversedStockLedgerEntryId, x.PostedAtUtc)).ToArray());
    }

    private static void EnsureTenant(UserContext u, TenantEntity entity)
    {
        if (entity.CompanyId != u.CompanyId || entity.BranchId != u.BranchId) throw new SecurityException("Cross-tenant access is forbidden.");
    }

    private static void ValidateContext(PostingCommandContext context)
    {
        if (!context.UserContext.IsAuthenticated) throw new SecurityException("Authenticated user context is required.");
        if (!UuidV7.IsVersion7(context.OperationId)) throw new AccountingDomainException("OperationId must be UUIDv7.");
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey)) throw new AccountingDomainException("Idempotency key is required.");
    }
}
