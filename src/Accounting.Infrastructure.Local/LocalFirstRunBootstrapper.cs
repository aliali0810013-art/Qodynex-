using Accounting.Domain;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Local;

public sealed record LocalClientIdentity(Guid CompanyId, Guid BranchId, Guid UserId, Guid DeviceId)
{
    public static LocalClientIdentity Create() =>
        new(UuidV7.NewGuid(), UuidV7.NewGuid(), UuidV7.NewGuid(), UuidV7.NewGuid());
}

public sealed record LocalDemoSeedResult(
    LocalClientIdentity Identity,
    Guid WarehouseId,
    Guid CashAccountId,
    Guid ReceivableAccountId,
    Guid PayableAccountId,
    IReadOnlyList<Guid> ItemIds);

/// <summary>
/// Creates a complete offline-first demonstration tenant only when the local
/// database has no company at all. It never overwrites a server bootstrap.
/// </summary>
public sealed class LocalFirstRunBootstrapper
{
    private readonly LocalAccountingDbContext _db;

    public LocalFirstRunBootstrapper(LocalAccountingDbContext db) => _db = db;

    public async Task<LocalDemoSeedResult> EnsureDemoSeedAsync(
        LocalClientIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (identity.CompanyId == Guid.Empty || identity.BranchId == Guid.Empty
            || identity.UserId == Guid.Empty || identity.DeviceId == Guid.Empty)
            throw new ArgumentException("A complete local client identity is required.", nameof(identity));

        var existingCompany = await _db.Companies.AsNoTracking()
            .OrderBy(x => x.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existingCompany is not null)
        {
            if (existingCompany.Id != identity.CompanyId)
                throw new InvalidOperationException("The local database belongs to another company identity.");

            var warehouseId = await _db.Warehouses.AsNoTracking()
                .Where(x => x.CompanyId == identity.CompanyId && x.BranchId == identity.BranchId)
                .OrderBy(x => x.Code).Select(x => x.Id)
                .FirstAsync(cancellationToken).ConfigureAwait(false);
            var cashId = await RequiredAccountIdAsync(identity, "1110", cancellationToken).ConfigureAwait(false);
            var receivableId = await RequiredAccountIdAsync(identity, "1130", cancellationToken).ConfigureAwait(false);
            var payableId = await RequiredAccountIdAsync(identity, "2110", cancellationToken).ConfigureAwait(false);
            var itemIds = await _db.Items.AsNoTracking()
                .Where(x => x.CompanyId == identity.CompanyId && x.BranchId == identity.BranchId)
                .OrderBy(x => x.Sku).Select(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new(identity, warehouseId, cashId, receivableId, payableId, itemIds);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var policy = new CompanyPolicy(
                "YER",
                2,
                MidpointRounding.AwayFromZero,
                InventoryValuationMethod.MovingWeightedAverage,
                allowNegativeStock: false,
                TaxPolicy.Disabled,
                quantityScale: 6);

            var company = new Company(identity.CompanyId, "Qodynex Demo - شركة تجريبية", policy);
            var branch = new Branch(identity.CompanyId, identity.BranchId, "MAIN", "الفرع الرئيسي");
            var user = new AppUser(identity.CompanyId, identity.BranchId, "demo", "مستخدم تجريبي", identity.UserId);
            var role = new Role(identity.CompanyId, identity.BranchId, "Local Administrator");
            var postPermission = new Permission(identity.CompanyId, identity.BranchId,
                Accounting.Application.AccountingPostingEngine.PostPermission, "ترحيل القيود والمستندات");
            var reversePermission = new Permission(identity.CompanyId, identity.BranchId,
                Accounting.Application.AccountingPostingEngine.ReversePermission, "عكس القيود والمستندات");

            var assets = new Account(identity.CompanyId, identity.BranchId, "1000", "الأصول",
                AccountType.Asset, NormalBalance.Debit, false);
            var currentAssets = new Account(identity.CompanyId, identity.BranchId, "1100", "الأصول المتداولة",
                AccountType.Asset, NormalBalance.Debit, false, assets.Id);
            var cash = new Account(identity.CompanyId, identity.BranchId, "1110", "الصندوق",
                AccountType.Asset, NormalBalance.Debit, true, currentAssets.Id);
            var bank = new Account(identity.CompanyId, identity.BranchId, "1120", "الحسابات البنكية",
                AccountType.Asset, NormalBalance.Debit, true, currentAssets.Id);
            var receivable = new Account(identity.CompanyId, identity.BranchId, "1130", "العملاء",
                AccountType.Asset, NormalBalance.Debit, true, currentAssets.Id);
            var inventory = new Account(identity.CompanyId, identity.BranchId, "1140", "المخزون",
                AccountType.Asset, NormalBalance.Debit, true, currentAssets.Id);

            var liabilities = new Account(identity.CompanyId, identity.BranchId, "2000", "الالتزامات",
                AccountType.Liability, NormalBalance.Credit, false);
            var currentLiabilities = new Account(identity.CompanyId, identity.BranchId, "2100", "الالتزامات المتداولة",
                AccountType.Liability, NormalBalance.Credit, false, liabilities.Id);
            var payable = new Account(identity.CompanyId, identity.BranchId, "2110", "الموردون",
                AccountType.Liability, NormalBalance.Credit, true, currentLiabilities.Id);

            var equity = new Account(identity.CompanyId, identity.BranchId, "3000", "حقوق الملكية",
                AccountType.Equity, NormalBalance.Credit, false);
            var openingEquity = new Account(identity.CompanyId, identity.BranchId, "3100", "رصيد افتتاحي",
                AccountType.Equity, NormalBalance.Credit, true, equity.Id);

            var revenue = new Account(identity.CompanyId, identity.BranchId, "4000", "الإيرادات",
                AccountType.Revenue, NormalBalance.Credit, false);
            var sales = new Account(identity.CompanyId, identity.BranchId, "4100", "المبيعات",
                AccountType.Revenue, NormalBalance.Credit, true, revenue.Id);
            var otherRevenue = new Account(identity.CompanyId, identity.BranchId, "4200", "إيرادات أخرى",
                AccountType.Revenue, NormalBalance.Credit, true, revenue.Id);

            var expenses = new Account(identity.CompanyId, identity.BranchId, "5000", "المصروفات",
                AccountType.Expense, NormalBalance.Debit, false);
            var cogs = new Account(identity.CompanyId, identity.BranchId, "5100", "تكلفة البضاعة المباعة",
                AccountType.Expense, NormalBalance.Debit, true, expenses.Id);
            var operatingExpense = new Account(identity.CompanyId, identity.BranchId, "5200", "مصروفات تشغيلية",
                AccountType.Expense, NormalBalance.Debit, true, expenses.Id);

            var each = new UnitOfMeasure(identity.CompanyId, identity.BranchId, "EA", "حبة");
            var carton = new UnitOfMeasure(identity.CompanyId, identity.BranchId, "CTN", "كرتون");
            var warehouse = new Warehouse(identity.CompanyId, identity.BranchId, "MAIN", "المخزن الرئيسي", false);

            var customer = new BusinessPartner(identity.CompanyId, identity.BranchId, "C001", "عميل نقدي تجريبي",
                PartnerType.Customer, receivable.Id, "777000001", 500000m);
            var supplier = new BusinessPartner(identity.CompanyId, identity.BranchId, "S001", "مورد تجريبي",
                PartnerType.Supplier, payable.Id, "777000002", 0m);

            var itemSpecs = new[]
            {
                new { Sku = "DEMO-001", Name = "مياه معدنية", Barcode = "6290000000011", Carton = 24m, Quantity = 240m, Cost = 150m },
                new { Sku = "DEMO-002", Name = "أرز", Barcode = "6290000000028", Carton = 10m, Quantity = 100m, Cost = 1200m },
                new { Sku = "DEMO-003", Name = "سكر", Barcode = "6290000000035", Carton = 10m, Quantity = 100m, Cost = 900m },
                new { Sku = "DEMO-004", Name = "زيت طبخ", Barcode = "6290000000042", Carton = 12m, Quantity = 120m, Cost = 800m },
                new { Sku = "DEMO-005", Name = "بسكويت", Barcode = "6290000000059", Carton = 24m, Quantity = 240m, Cost = 100m }
            };

            var items = itemSpecs.Select(x => new Item(
                identity.CompanyId, identity.BranchId, x.Sku, x.Name, each.Id,
                inventory.Id, sales.Id, cogs.Id, ItemKind.Stock, x.Barcode)).ToArray();
            var conversions = items.Zip(itemSpecs, (item, spec) =>
                new ItemUnitConversion(identity.CompanyId, identity.BranchId, item.Id, carton.Id, spec.Carton)).ToArray();

            _db.AddRange(company, branch, user, role, postPermission, reversePermission);
            _db.Add(new AppUserRole(identity.CompanyId, identity.BranchId, user.Id, role.Id));
            _db.Add(new RolePermissionAssignment(identity.CompanyId, identity.BranchId, role.Id, postPermission.Id));
            _db.Add(new RolePermissionAssignment(identity.CompanyId, identity.BranchId, role.Id, reversePermission.Id));
            _db.AddRange(assets, currentAssets, cash, bank, receivable, inventory,
                liabilities, currentLiabilities, payable, equity, openingEquity,
                revenue, sales, otherRevenue, expenses, cogs, operatingExpense);
            _db.AddRange(each, carton, warehouse, customer, supplier);
            _db.AddRange(items);
            _db.AddRange(conversions);
            _db.LocalSyncStates.Add(new LocalSyncState
            {
                Id = UuidV7.NewGuid(),
                CompanyId = identity.CompanyId,
                BranchId = identity.BranchId,
                DeviceId = identity.DeviceId,
                LastPulledWatermark = 0,
                CompanyName = company.Name,
                BranchName = branch.Name,
                EnabledModulesCsv = "RETAIL",
                LastSuccessfulSyncUtc = null,
                LastError = null
            });

            var openingDate = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var openingOperation = UuidV7.NewGuid(openingDate);
            decimal openingValue = 0m;
            var stock = new List<StockLedgerEntry>(items.Length);
            for (var index = 0; index < items.Length; index++)
            {
                var spec = itemSpecs[index];
                var value = spec.Quantity * spec.Cost;
                openingValue += value;
                stock.Add(new StockLedgerEntry(
                    identity.CompanyId,
                    identity.BranchId,
                    items[index].Id,
                    warehouse.Id,
                    openingOperation,
                    "LocalDemoOpeningBalance",
                    openingOperation,
                    StockMovementType.AdjustmentReceipt,
                    spec.Quantity,
                    value,
                    spec.Cost,
                    openingDate));
            }

            var openingJournal = new JournalEntry(
                identity.CompanyId,
                identity.BranchId,
                openingDate,
                "الرصيد الافتتاحي للمخزون التجريبي",
                "OPENING-DEMO",
                openingOperation,
                "LocalDemoOpeningBalance",
                openingOperation,
                "local-demo-opening-balance");
            openingJournal.AddLine(inventory.Id, openingValue, 0m, "مخزون افتتاحي", policy, new CurrencyPolicyService());
            openingJournal.AddLine(openingEquity.Id, 0m, openingValue, "مقابل الرصيد الافتتاحي", policy, new CurrencyPolicyService());
            openingJournal.Post(new Dictionary<Guid, Account>
            {
                [inventory.Id] = inventory,
                [openingEquity.Id] = openingEquity
            }, policy, new CurrencyPolicyService(), openingDate);

            _db.Add(openingJournal);
            _db.AddRange(stock);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new LocalDemoSeedResult(
                identity, warehouse.Id, cash.Id, receivable.Id, payable.Id, items.Select(x => x.Id).ToArray());
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<Guid> RequiredAccountIdAsync(
        LocalClientIdentity identity,
        string code,
        CancellationToken cancellationToken) =>
        await _db.Accounts.AsNoTracking()
            .Where(x => x.CompanyId == identity.CompanyId && x.BranchId == identity.BranchId && x.Code == code)
            .Select(x => x.Id)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
}
