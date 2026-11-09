using System.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Accounting.Infrastructure.Server;

public sealed class ServerAccountingDbContext : DbContext
{
    private readonly IServerTenantContext _tenant;

    public ServerAccountingDbContext(DbContextOptions<ServerAccountingDbContext> options, IServerTenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<ServerCompanyPolicy> CompanyPolicies => Set<ServerCompanyPolicy>();
    public DbSet<ServerAccount> Accounts => Set<ServerAccount>();
    public DbSet<ServerItem> Items => Set<ServerItem>();
    public DbSet<ServerWarehouse> Warehouses => Set<ServerWarehouse>();
    public DbSet<ServerFinancialDocument> FinancialDocuments => Set<ServerFinancialDocument>();
    public DbSet<ServerJournalEntry> JournalEntries => Set<ServerJournalEntry>();
    public DbSet<ServerJournalLine> JournalLines => Set<ServerJournalLine>();
    public DbSet<ServerStockLedgerEntry> StockLedgerEntries => Set<ServerStockLedgerEntry>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<SequenceCounter> SequenceCounters => Set<SequenceCounter>();
    public DbSet<SyncWatermark> SyncWatermarks => Set<SyncWatermark>();
    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    public DbSet<ServerAuditLog> AuditLogs => Set<ServerAuditLog>();
    public DbSet<ServerSyncConflict> SyncConflicts => Set<ServerSyncConflict>();
    public DbSet<ServerDocumentReversal> DocumentReversals => Set<ServerDocumentReversal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        Configure<ServerCompanyPolicy>(modelBuilder, "CompanyPolicies");
        Configure<ServerAccount>(modelBuilder, "Accounts");
        Configure<ServerItem>(modelBuilder, "Items");
        Configure<ServerWarehouse>(modelBuilder, "Warehouses");
        Configure<ServerFinancialDocument>(modelBuilder, "FinancialDocuments");
        Configure<ServerJournalEntry>(modelBuilder, "JournalEntries");
        Configure<ServerJournalLine>(modelBuilder, "JournalLines");
        Configure<ServerStockLedgerEntry>(modelBuilder, "StockLedgerEntries");
        Configure<InboxMessage>(modelBuilder, "InboxMessages");
        Configure<SequenceCounter>(modelBuilder, "SequenceCounters");
        Configure<SyncWatermark>(modelBuilder, "SyncWatermarks");
        Configure<DeviceRegistration>(modelBuilder, "DeviceRegistrations");
        Configure<SyncChange>(modelBuilder, "SyncChanges");
        Configure<ServerAuditLog>(modelBuilder, "AuditLogs");
        Configure<ServerSyncConflict>(modelBuilder, "SyncConflicts");
        Configure<ServerDocumentReversal>(modelBuilder, "DocumentReversals");

        modelBuilder.Entity<ServerCompanyPolicy>().HasIndex(x => new { x.CompanyId, x.BranchId }).IsUnique();
        modelBuilder.Entity<ServerAccount>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Code }).IsUnique();
        modelBuilder.Entity<ServerItem>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Sku }).IsUnique();
        modelBuilder.Entity<ServerWarehouse>().HasIndex(x => new { x.CompanyId, x.BranchId, x.Code }).IsUnique();
        modelBuilder.Entity<ServerFinancialDocument>().HasIndex(x => new { x.CompanyId, x.BranchId, x.OfficialSequenceNumber }).IsUnique();
        modelBuilder.Entity<ServerFinancialDocument>().HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<ServerJournalEntry>().HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<InboxMessage>().HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<InboxMessage>().HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey, x.OperationId }).IsUnique();
        modelBuilder.Entity<SequenceCounter>().HasIndex(x => new { x.CompanyId, x.BranchId, x.DocumentType, x.SequenceYear }).IsUnique();
        modelBuilder.Entity<SyncWatermark>().HasIndex(x => new { x.CompanyId, x.BranchId, x.DeviceId }).IsUnique();
        modelBuilder.Entity<DeviceRegistration>().HasIndex(x => new { x.CompanyId, x.BranchId, x.DeviceId }).IsUnique();
        modelBuilder.Entity<SyncChange>().Property(x => x.ServerWatermark).UseIdentityAlwaysColumn();
        modelBuilder.Entity<SyncChange>().HasIndex(x => x.ServerWatermark).IsUnique();
        modelBuilder.Entity<SyncChange>().HasIndex(x => new { x.CompanyId, x.BranchId, x.ServerWatermark });
        modelBuilder.Entity<ServerSyncConflict>().HasIndex(x => new { x.CompanyId, x.BranchId, x.OperationId }).IsUnique();
        modelBuilder.Entity<ServerDocumentReversal>().HasIndex(x => new { x.CompanyId, x.BranchId, x.SourceDocumentId }).IsUnique();
        modelBuilder.Entity<ServerDocumentReversal>().HasIndex(x => new { x.CompanyId, x.BranchId, x.IdempotencyKey }).IsUnique();

        modelBuilder.Entity<ServerCompanyPolicy>().Property(x => x.TaxRate).HasPrecision(38, 12);
        modelBuilder.Entity<ServerJournalLine>().Property(x => x.Debit).HasPrecision(38, 12);
        modelBuilder.Entity<ServerJournalLine>().Property(x => x.Credit).HasPrecision(38, 12);
        modelBuilder.Entity<ServerStockLedgerEntry>().Property(x => x.QuantityDelta).HasPrecision(38, 12);
        modelBuilder.Entity<ServerStockLedgerEntry>().Property(x => x.InventoryValueDelta).HasPrecision(38, 12);
        modelBuilder.Entity<ServerStockLedgerEntry>().Property(x => x.UnitCost).HasPrecision(38, 12);

        ApplyTenantFilters(modelBuilder);
    }

    private void ApplyTenantFilters(ModelBuilder m)
    {
        m.Entity<ServerCompanyPolicy>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerAccount>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerItem>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerWarehouse>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerFinancialDocument>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerJournalEntry>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerJournalLine>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerStockLedgerEntry>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<InboxMessage>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<SequenceCounter>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<SyncWatermark>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<DeviceRegistration>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<SyncChange>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerAuditLog>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerSyncConflict>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
        m.Entity<ServerDocumentReversal>().HasQueryFilter(x => _tenant.IsSystem || (x.CompanyId == _tenant.CompanyId && x.BranchId == _tenant.BranchId));
    }

    private static void Configure<TEntity>(ModelBuilder m, string table) where TEntity : ServerTenantEntity
    {
        m.Entity<TEntity>(b =>
        {
            b.ToTable(table);
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CompanyId, x.BranchId, x.Id });
        });
    }
}

public sealed class ServerImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Validate(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Validate(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Validate(DbContext? db)
    {
        if (db is null) return;
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is ServerFinancialDocument or ServerJournalEntry or ServerJournalLine or ServerStockLedgerEntry
                or ServerAuditLog or ServerSyncConflict or ServerDocumentReversal or InboxMessage)
                throw new SecurityException($"{entry.Entity.GetType().Name} is append-only on the authoritative server.");
        }
    }
}

public sealed class ServerDatabaseBootstrapper
{
    private readonly ServerAccountingDbContext _db;
    public ServerDatabaseBootstrapper(ServerAccountingDbContext db) => _db = db;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await ApplyRowLevelSecurityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyRowLevelSecurityAsync(CancellationToken cancellationToken = default)
    {
        var tables = new[]
        {
            "CompanyPolicies","Accounts","Items","Warehouses","FinancialDocuments","JournalEntries","JournalLines",
            "StockLedgerEntries","InboxMessages","SequenceCounters","SyncWatermarks","DeviceRegistrations","SyncChanges",
            "AuditLogs","SyncConflicts","DocumentReversals"
        };

        foreach (var table in tables)
        {
            var policyName = $"tenant_isolation_{table.ToLowerInvariant()}";
            var sql = $"""
ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS "{policyName}" ON "{table}";
CREATE POLICY "{policyName}" ON "{table}"
USING (
    "CompanyId" = NULLIF(current_setting('app.company_id', true), '')::uuid
    AND "BranchId" = NULLIF(current_setting('app.branch_id', true), '')::uuid
)
WITH CHECK (
    "CompanyId" = NULLIF(current_setting('app.company_id', true), '')::uuid
    AND "BranchId" = NULLIF(current_setting('app.branch_id', true), '')::uuid
);
""";
            await _db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
        }
    }
}
