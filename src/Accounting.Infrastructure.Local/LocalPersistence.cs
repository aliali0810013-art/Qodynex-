using System.Data;
using System.Security;
using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Accounting.Infrastructure.Local;

// SQLite stores every instant as an INTEGER with UTC ticks (including nullable
// DateTimeOffset properties). Unlike DateTimeOffsetToBinaryConverter this preserves
// chronological ordering across different input UTC offsets.
public sealed class UtcDateTimeOffsetTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcDateTimeOffsetTicksConverter() : base(
        value => value.UtcDateTime.Ticks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero)) { }
}

public sealed class LocalAccountingDbContext : DbContext
{
    public LocalAccountingDbContext(DbContextOptions<LocalAccountingDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<AppUserRole> AppUserRoles => Set<AppUserRole>();
    public DbSet<RolePermissionAssignment> RolePermissionAssignments => Set<RolePermissionAssignment>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<BusinessPartner> BusinessPartners => Set<BusinessPartner>();
    public DbSet<UnitOfMeasure> UnitOfMeasures => Set<UnitOfMeasure>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemUnitConversion> ItemUnitConversions => Set<ItemUnitConversion>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<CommercialDocument> Documents => Set<CommercialDocument>();
    public DbSet<ReceiptVoucher> ReceiptVouchers => Set<ReceiptVoucher>();
    public DbSet<PaymentVoucher> PaymentVouchers => Set<PaymentVoucher>();
    public DbSet<SalesInvoice> SalesInvoices => Set<SalesInvoice>();
    public DbSet<SalesInvoiceLine> SalesInvoiceLines => Set<SalesInvoiceLine>();
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<PurchaseInvoiceLine> PurchaseInvoiceLines => Set<PurchaseInvoiceLine>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<StockLedgerEntry> StockLedgerEntries => Set<StockLedgerEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OfficialSequenceAssignment> OfficialSequenceAssignments => Set<OfficialSequenceAssignment>();
    public DbSet<DocumentReversalRecord> DocumentReversalRecords => Set<DocumentReversalRecord>();
    public DbSet<LocalSyncState> LocalSyncStates => Set<LocalSyncState>();
    public DbSet<LocalSyncConflict> LocalSyncConflicts => Set<LocalSyncConflict>();
    public DbSet<LocalSequenceCounter> LocalSequenceCounters => Set<LocalSequenceCounter>();
    public DbSet<LocalModuleConfiguration> LocalModuleConfigurations => Set<LocalModuleConfiguration>();
    public DbSet<LocalCompanyBrandingSettings> LocalCompanyBrandingSettings => Set<LocalCompanyBrandingSettings>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetTicksConverter>()
            .HaveColumnType("INTEGER");
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);
        Configure<Company>(m, "Companies"); Configure<Branch>(m, "Branches"); Configure<AppUser>(m, "AppUsers"); Configure<Role>(m, "Roles");
        Configure<Permission>(m, "Permissions"); Configure<AppUserRole>(m, "AppUserRoles"); Configure<RolePermissionAssignment>(m, "RolePermissionAssignments");
        Configure<Account>(m, "Accounts"); Configure<BusinessPartner>(m, "BusinessPartners"); Configure<UnitOfMeasure>(m, "UnitOfMeasures");
        Configure<Item>(m, "Items"); Configure<ItemUnitConversion>(m, "ItemUnitConversions"); Configure<Warehouse>(m, "Warehouses");
        Configure<JournalEntry>(m, "JournalEntries"); Configure<JournalLine>(m, "JournalLines"); Configure<StockLedgerEntry>(m, "StockLedgerEntries");
        Configure<AuditLog>(m, "AuditLogs"); Configure<OutboxMessage>(m, "OutboxMessages"); Configure<OfficialSequenceAssignment>(m, "OfficialSequenceAssignments");
        Configure<DocumentReversalRecord>(m, "DocumentReversalRecords");
        m.Entity<LocalSyncState>(b =>
        {
            b.ToTable("LocalSyncStates");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.DeviceId }).IsUnique();
        });
        m.Entity<LocalSyncConflict>(b =>
        {
            b.ToTable("LocalSyncConflicts");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.OperationId }).IsUnique();
        });
        m.Entity<LocalSequenceCounter>(b =>
        {
            b.ToTable("LocalSequenceCounters");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.DeviceId, x.DocumentType }).IsUnique();
        });
        m.Entity<LocalModuleConfiguration>(b =>
        {
            b.ToTable("LocalModuleConfigurations");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.ModuleKey }).IsUnique();
        });
        m.Entity<LocalCompanyBrandingSettings>(b =>
        {
            b.ToTable("LocalCompanyBrandingSettings");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CompanyId, x.BranchId }).IsUnique();
            b.Property(x => x.TaxRate).HasPrecision(38, 12);
        });

        m.Entity<CommercialDocument>(b =>
        {
            b.ToTable("Documents"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.CompanyId, x.BranchId, x.Id }).IsUnique();
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.LocalTempNumber }).IsUnique();
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.PostingIdempotencyKey }).IsUnique();
            b.HasDiscriminator<string>("DocumentType")
              .HasValue<ReceiptVoucher>(nameof(ReceiptVoucher)).HasValue<PaymentVoucher>(nameof(PaymentVoucher))
              .HasValue<SalesInvoice>(nameof(SalesInvoice)).HasValue<PurchaseInvoice>(nameof(PurchaseInvoice));
        });

        m.Entity<SalesInvoice>(b =>
        {
            b.Property(x => x.WarehouseId).HasColumnName("WarehouseId");
            b.Property(x => x.SettlementAccountId).HasColumnName("SettlementAccountId");
            b.Property(x => x.BusinessPartnerId).HasColumnName("BusinessPartnerId");
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        Configure<SalesInvoiceLine>(m, "SalesInvoiceLines");
        m.Entity<PurchaseInvoice>(b =>
        {
            b.Property(x => x.WarehouseId).HasColumnName("WarehouseId");
            b.Property(x => x.SettlementAccountId).HasColumnName("SettlementAccountId");
            b.Property(x => x.BusinessPartnerId).HasColumnName("BusinessPartnerId");
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseInvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        Configure<PurchaseInvoiceLine>(m, "PurchaseInvoiceLines");

        m.Entity<ReceiptVoucher>(b =>
        {
            b.Property(x => x.CashOrBankAccountId).HasColumnName("CashOrBankAccountId");
            b.Property(x => x.CounterAccountId).HasColumnName("CounterAccountId");
            b.Property(x => x.BusinessPartnerId).HasColumnName("BusinessPartnerId");
            b.Property(x => x.Amount).HasColumnName("VoucherAmount");
            b.Property(x => x.Description).HasColumnName("VoucherDescription");
        });
        m.Entity<PaymentVoucher>(b =>
        {
            b.Property(x => x.CashOrBankAccountId).HasColumnName("CashOrBankAccountId");
            b.Property(x => x.CounterAccountId).HasColumnName("CounterAccountId");
            b.Property(x => x.BusinessPartnerId).HasColumnName("BusinessPartnerId");
            b.Property(x => x.Amount).HasColumnName("VoucherAmount");
            b.Property(x => x.Description).HasColumnName("VoucherDescription");
        });

        m.Entity<JournalEntry>(b =>
        {
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.SourceDocumentId });
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        m.Entity<Branch>().HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        m.Entity<AppUser>().HasIndex(x => new { x.CompanyId, x.BranchId, x.UserName }).IsUnique();
        m.Entity<Role>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Name }).IsUnique();
        m.Entity<Permission>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Code }).IsUnique();
        m.Entity<AppUserRole>().HasIndex(x => new { x.CompanyId, x.BranchId, x.UserId, x.RoleId }).IsUnique();
        m.Entity<RolePermissionAssignment>().HasIndex(x => new { x.CompanyId, x.BranchId, x.RoleId, x.PermissionId }).IsUnique();
        m.Entity<Account>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Code }).IsUnique();
        m.Entity<BusinessPartner>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Code }).IsUnique();
        m.Entity<UnitOfMeasure>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Code }).IsUnique();
        m.Entity<Item>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Sku }).IsUnique();
        m.Entity<ItemUnitConversion>().HasIndex(x => new { x.CompanyId, x.BranchId, x.ItemId, x.UnitOfMeasureId }).IsUnique();
        m.Entity<Warehouse>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Code }).IsUnique();
        m.Entity<StockLedgerEntry>().HasIndex(x => new { x.CompanyId, x.BranchId, x.ItemId, x.WarehouseId, x.PostedAtUtc, x.Id })
            .HasDatabaseName("IX_StockLedgerEntries_Tenant_Item_Warehouse_Date");
        m.Entity<StockLedgerEntry>().HasIndex(x => new { x.CompanyId, x.BranchId, x.SourceDocumentId, x.PostedAtUtc, x.Id })
            .HasDatabaseName("IX_StockLedgerEntries_Tenant_Source_Date");
        m.Entity<JournalEntry>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Status, x.EntryDateUtc, x.Id })
            .HasDatabaseName("IX_JournalEntries_Tenant_Status_Date");
        m.Entity<JournalLine>().HasIndex(x => new { x.CompanyId, x.BranchId, x.AccountId, x.JournalEntryId, x.Id })
            .HasDatabaseName("IX_JournalLines_Tenant_Account_Entry");
        m.Entity<OutboxMessage>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Status, x.CreatedAtUtc, x.Id })
            .HasDatabaseName("IX_OutboxMessages_Tenant_Status_Date");
        m.Entity<OutboxMessage>().HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey }).IsUnique();
        m.Entity<OfficialSequenceAssignment>().HasIndex(x => new { x.CompanyId, x.BranchId, x.DocumentId }).IsUnique();
        m.Entity<DocumentReversalRecord>().HasIndex(x => new { x.CompanyId, x.BranchId, x.SourceDocumentId }).IsUnique();
        m.Entity<DocumentReversalRecord>().HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey }).IsUnique();

        m.Entity<Company>().Property(x => x.TaxRate).HasPrecision(38, 12);
        m.Entity<BusinessPartner>().Property(x => x.CreditLimit).HasPrecision(38, 12);
        m.Entity<ItemUnitConversion>().Property(x => x.BaseUnitsPerUnit).HasPrecision(38, 12);
        m.Entity<SalesInvoiceLine>().Property(x => x.Quantity).HasPrecision(38, 12);
        m.Entity<SalesInvoiceLine>().Property(x => x.BaseUnitsPerUnit).HasPrecision(38, 12);
        m.Entity<SalesInvoiceLine>().Property(x => x.UnitPrice).HasPrecision(38, 12);
        m.Entity<PurchaseInvoiceLine>().Property(x => x.Quantity).HasPrecision(38, 12);
        m.Entity<PurchaseInvoiceLine>().Property(x => x.BaseUnitsPerUnit).HasPrecision(38, 12);
        m.Entity<PurchaseInvoiceLine>().Property(x => x.UnitPrice).HasPrecision(38, 12);
        m.Entity<ReceiptVoucher>().Property(x => x.Amount).HasPrecision(38, 12);
        m.Entity<PaymentVoucher>().Property(x => x.Amount).HasPrecision(38, 12);
        m.Entity<JournalLine>().Property(x => x.Debit).HasPrecision(38, 12);
        m.Entity<JournalLine>().Property(x => x.Credit).HasPrecision(38, 12);
        m.Entity<StockLedgerEntry>().Property(x => x.QuantityDelta).HasPrecision(38, 12);
        m.Entity<StockLedgerEntry>().Property(x => x.InventoryValueDelta).HasPrecision(38, 12);
        m.Entity<StockLedgerEntry>().Property(x => x.UnitCost).HasPrecision(38, 12);
    }

    private static void Configure<TEntity>(ModelBuilder m, string table) where TEntity : TenantEntity
    {
        m.Entity<TEntity>(b => { b.ToTable(table); b.HasKey(x => x.Id); b.HasIndex(x => new { x.CompanyId, x.BranchId, x.Id }).IsUnique(); });
    }
}

public sealed class ImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> r) { Validate(e.Context); return base.SavingChanges(e, r); }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default)
    { Validate(e.Context); return base.SavingChangesAsync(e, r, ct); }

    private static void Validate(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is AuditLog or JournalLine or StockLedgerEntry or DocumentReversalRecord or OfficialSequenceAssignment)
                throw new SecurityException($"{entry.Entity.GetType().Name} is append-only.");
            if (entry.Entity is IImmutablePostedEntity)
            {
                var original = (PostingStatus)entry.Property(nameof(IImmutablePostedEntity.Status)).OriginalValue!;
                if (original == PostingStatus.Posted) throw new SecurityException($"Posted {entry.Entity.GetType().Name} is immutable.");
            }
        }
    }
}

public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly LocalAccountingDbContext _db;
    public SqliteUnitOfWork(LocalAccountingDbContext db) => _db = db;
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            var nestedResult = await work(ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return nestedResult;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            var result = await work(ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            throw;
        }
    }
}

public static class SqliteSchemaGuard
{
    private static readonly string[] Sql =
    {
        Trigger("JournalEntries","journal_no_update","UPDATE","OLD.Status = 1","Posted JournalEntry is immutable"),
        Trigger("JournalEntries","journal_no_delete","DELETE","OLD.Status = 1","Posted JournalEntry cannot be deleted"),
        Trigger("Documents","document_no_update","UPDATE","OLD.Status = 1","Posted document is immutable"),
        Trigger("Documents","document_no_delete","DELETE","OLD.Status = 1","Posted document cannot be deleted"),
        Trigger("JournalLines","journal_line_no_update","UPDATE","1 = 1","JournalLine is append-only"),
        Trigger("JournalLines","journal_line_no_delete","DELETE","1 = 1","JournalLine is append-only"),
        Trigger("StockLedgerEntries","stock_no_update","UPDATE","1 = 1","StockLedgerEntry is append-only"),
        Trigger("StockLedgerEntries","stock_no_delete","DELETE","1 = 1","StockLedgerEntry is append-only"),
        Trigger("AuditLogs","audit_no_update","UPDATE","1 = 1","AuditLog is append-only"),
        Trigger("AuditLogs","audit_no_delete","DELETE","1 = 1","AuditLog is append-only"),
        Trigger("DocumentReversalRecords","reversal_no_update","UPDATE","1 = 1","DocumentReversalRecord is append-only"),
        Trigger("DocumentReversalRecords","reversal_no_delete","DELETE","1 = 1","DocumentReversalRecord is append-only"),
        Trigger("OfficialSequenceAssignments","official_sequence_no_update","UPDATE","1 = 1","OfficialSequenceAssignment is append-only"),
        Trigger("OfficialSequenceAssignments","official_sequence_no_delete","DELETE","1 = 1","OfficialSequenceAssignment is append-only"),
        ChildDocumentTrigger("SalesInvoiceLines","SalesInvoiceId","sales_line_no_update_posted","UPDATE"),
        ChildDocumentTrigger("SalesInvoiceLines","SalesInvoiceId","sales_line_no_delete_posted","DELETE"),
        ChildDocumentTrigger("PurchaseInvoiceLines","PurchaseInvoiceId","purchase_line_no_update_posted","UPDATE"),
        ChildDocumentTrigger("PurchaseInvoiceLines","PurchaseInvoiceId","purchase_line_no_delete_posted","DELETE")
    };
    public static async Task ApplyPragmasAsync(LocalAccountingDbContext db, CancellationToken ct = default)
    { await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", ct); await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct); }
    public static async Task ApplyTriggersAsync(LocalAccountingDbContext db, CancellationToken ct = default)
    { foreach (var sql in Sql) await db.Database.ExecuteSqlRawAsync(sql, ct); }
    private static string Trigger(string table,string name,string operation,string condition,string message) => $@"CREATE TRIGGER IF NOT EXISTS {name}
BEFORE {operation} ON {table}
WHEN {condition}
BEGIN
 SELECT RAISE(ABORT, 'SECURITY: {message}');
END;";

    private static string ChildDocumentTrigger(string table,string foreignKey,string name,string operation) => $@"CREATE TRIGGER IF NOT EXISTS {name}
BEFORE {operation} ON {table}
WHEN EXISTS (SELECT 1 FROM Documents d WHERE d.Id = OLD.{foreignKey} AND d.Status = 1)
BEGIN
 SELECT RAISE(ABORT, 'SECURITY: Posted document line is immutable');
END;";
}

public sealed class LocalDatabaseBootstrapper
{
    private readonly LocalAccountingDbContext _db;
    public DatabaseUpgradeResult LastUpgradeResult { get; private set; } = DatabaseUpgradeResult.None;

    public LocalDatabaseBootstrapper(LocalAccountingDbContext db) => _db = db;

    public async Task<DatabaseUpgradeResult> InitializeAsync(CancellationToken ct = default)
    {
        var upgrader = new DatabaseSchemaUpgrader(_db);
        LastUpgradeResult = await upgrader.PrepareAsync(ct).ConfigureAwait(false);

        try
        {
            await InitializeOnceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (upgrader.CanRecoverFile && DatabaseSchemaUpgrader.IsRecoverableSchemaFailure(ex))
        {
            try { await _db.Database.CloseConnectionAsync().ConfigureAwait(false); } catch { }
            _db.ChangeTracker.Clear();
            LastUpgradeResult = await upgrader.BackupAndResetAsync(
                $"EF migration/schema initialization failed: {ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
            await InitializeOnceAsync(ct).ConfigureAwait(false);
        }

        return LastUpgradeResult;
    }

    private async Task InitializeOnceAsync(CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await SqliteSchemaGuard.ApplyPragmasAsync(_db, ct).ConfigureAwait(false);
            await _db.Database.MigrateAsync(ct).ConfigureAwait(false);
            await SqliteSchemaGuard.ApplyTriggersAsync(_db, ct).ConfigureAwait(false);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}

public sealed class LocalDbContextFactory : IDesignTimeDbContextFactory<LocalAccountingDbContext>
{
    public LocalAccountingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LocalAccountingDbContext>().UseSqlite("Data Source=qodynex-accounting.db").AddInterceptors(new ImmutabilityInterceptor()).Options;
        return new LocalAccountingDbContext(options);
    }
}
