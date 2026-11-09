using System.Data;
using System.Text.Json;
using Accounting.Domain;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Server;

public sealed record TenantPolicyRegistration(
    string BaseCurrencyCode,
    int CurrencyScale,
    int QuantityScale,
    MidpointRounding RoundingMode,
    InventoryValuationMethod InventoryValuationMethod,
    bool AllowNegativeStock,
    bool TaxEnabled,
    decimal TaxRate,
    bool PricesIncludeTax,
    Guid? OutputTaxAccountId,
    Guid? InputTaxAccountId);

public sealed record ServerAccountRegistration(
    Guid Id, string Code, string Name, Guid? ParentAccountId, AccountType AccountType,
    NormalBalance NormalBalance, bool IsPostable, bool IsActive);

public sealed record ServerUnitRegistration(Guid Id, string Code, string Name);

public sealed record ServerBusinessPartnerRegistration(
    Guid Id, string Code, string Name, PartnerType PartnerType, Guid ControlAccountId,
    string? Phone, decimal CreditLimit, bool IsActive);

public sealed record ServerItemRegistration(
    Guid Id, string Sku, string Name, Guid BaseUnitOfMeasureId, ItemKind ItemKind,
    Guid InventoryAccountId, Guid SalesRevenueAccountId, Guid CogsAccountId, bool IsActive);

public sealed record ServerWarehouseRegistration(Guid Id, string Code, string Name, bool? AllowNegativeStockOverride);

public sealed record TenantConfigurationRequest(
    string CompanyName,
    string BranchCode,
    string BranchName,
    IReadOnlyList<string> EnabledModules,
    TenantPolicyRegistration Policy,
    IReadOnlyList<ServerAccountRegistration> Accounts,
    IReadOnlyList<ServerUnitRegistration> Units,
    IReadOnlyList<ServerBusinessPartnerRegistration> Partners,
    IReadOnlyList<ServerItemRegistration> Items,
    IReadOnlyList<ServerWarehouseRegistration> Warehouses);

public sealed record TenantConfigurationResult(
    int Accounts,
    int Units,
    int Partners,
    int Items,
    int Warehouses,
    long ServerWatermark,
    DateTimeOffset ConfiguredAtUtc);

public sealed class TenantAdministrationService
{
    private readonly ServerAccountingDbContext _db;
    private readonly IServerTenantContext _tenant;
    private readonly TenantPostgresSession _session;
    private readonly IClock _clock;

    public TenantAdministrationService(ServerAccountingDbContext db, IServerTenantContext tenant, TenantPostgresSession session, IClock clock)
    {
        _db = db;
        _tenant = tenant;
        _session = session;
        _clock = clock;
    }

    public async Task<TenantConfigurationResult> ConfigureAsync(TenantConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        if (!_tenant.IsAuthenticated || _tenant.IsSystem) throw new UnauthorizedAccessException("Authenticated tenant context is required.");
        if (string.IsNullOrWhiteSpace(request.CompanyName) || string.IsNullOrWhiteSpace(request.BranchCode) || string.IsNullOrWhiteSpace(request.BranchName))
            throw new InvalidOperationException("Company and branch names/codes are required.");
        if (request.EnabledModules.Count == 0)
            throw new InvalidOperationException("At least one activity module must be enabled.");

        var validatedPolicy = new CompanyPolicy(request.Policy.BaseCurrencyCode, request.Policy.CurrencyScale, request.Policy.RoundingMode,
            request.Policy.InventoryValuationMethod, request.Policy.AllowNegativeStock,
            new TaxPolicy(request.Policy.TaxEnabled, request.Policy.TaxRate, request.Policy.PricesIncludeTax,
                request.Policy.OutputTaxAccountId, request.Policy.InputTaxAccountId),
            request.Policy.QuantityScale);

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);

        var before = JsonSerializer.Serialize(new
        {
            Profile = await _db.TenantProfiles.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            Policy = await _db.CompanyPolicies.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            AccountCount = await _db.Accounts.CountAsync(cancellationToken).ConfigureAwait(false),
            UnitCount = await _db.UnitOfMeasures.CountAsync(cancellationToken).ConfigureAwait(false),
            PartnerCount = await _db.BusinessPartners.CountAsync(cancellationToken).ConfigureAwait(false),
            ItemCount = await _db.Items.CountAsync(cancellationToken).ConfigureAwait(false),
            WarehouseCount = await _db.Warehouses.CountAsync(cancellationToken).ConfigureAwait(false)
        });

        var profile = await _db.TenantProfiles.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            profile = new ServerTenantProfile { CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
            _db.TenantProfiles.Add(profile);
        }
        profile.CompanyName = request.CompanyName.Trim();
        profile.BranchCode = request.BranchCode.Trim();
        profile.BranchName = request.BranchName.Trim();
        profile.EnabledModulesCsv = string.Join(',', request.EnabledModules
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var policy = await _db.CompanyPolicies.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            policy = new ServerCompanyPolicy { CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
            _db.CompanyPolicies.Add(policy);
        }
        policy.BaseCurrencyCode = validatedPolicy.BaseCurrencyCode;
        policy.CurrencyScale = validatedPolicy.CurrencyScale;
        policy.QuantityScale = validatedPolicy.QuantityScale;
        policy.RoundingMode = validatedPolicy.RoundingMode;
        policy.InventoryValuationMethod = validatedPolicy.InventoryValuationMethod;
        policy.AllowNegativeStock = validatedPolicy.AllowNegativeStock;
        policy.TaxEnabled = validatedPolicy.TaxPolicy.Enabled;
        policy.TaxRate = validatedPolicy.TaxPolicy.Rate;
        policy.PricesIncludeTax = validatedPolicy.TaxPolicy.PricesIncludeTax;
        policy.OutputTaxAccountId = validatedPolicy.TaxPolicy.OutputTaxAccountId;
        policy.InputTaxAccountId = validatedPolicy.TaxPolicy.InputTaxAccountId;

        foreach (var dto in request.Accounts)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
                throw new InvalidOperationException("Every server account requires Id, Code and Name.");
            var row = await _db.Accounts.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new ServerAccount { Id = dto.Id, CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
                _db.Accounts.Add(row);
            }
            row.Code = dto.Code.Trim();
            row.Name = dto.Name.Trim();
            row.ParentAccountId = dto.ParentAccountId;
            row.AccountType = dto.AccountType;
            row.NormalBalance = dto.NormalBalance;
            row.IsPostable = dto.IsPostable;
            row.IsActive = dto.IsActive;
        }

        foreach (var dto in request.Units)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
                throw new InvalidOperationException("Every unit requires Id, Code and Name.");
            var row = await _db.UnitOfMeasures.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new ServerUnitOfMeasure { Id = dto.Id, CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
                _db.UnitOfMeasures.Add(row);
            }
            row.Code = dto.Code.Trim();
            row.Name = dto.Name.Trim();
        }

        foreach (var dto in request.Partners)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name) || dto.ControlAccountId == Guid.Empty)
                throw new InvalidOperationException("Every partner requires Id, Code, Name and control account.");
            if (dto.CreditLimit < 0m) throw new InvalidOperationException("Partner credit limit cannot be negative.");
            var row = await _db.BusinessPartners.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new ServerBusinessPartner { Id = dto.Id, CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
                _db.BusinessPartners.Add(row);
            }
            row.Code = dto.Code.Trim();
            row.Name = dto.Name.Trim();
            row.PartnerType = dto.PartnerType;
            row.ControlAccountId = dto.ControlAccountId;
            row.Phone = dto.Phone?.Trim();
            row.CreditLimit = dto.CreditLimit;
            row.IsActive = dto.IsActive;
        }

        foreach (var dto in request.Items)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Sku) || string.IsNullOrWhiteSpace(dto.Name) || dto.BaseUnitOfMeasureId == Guid.Empty)
                throw new InvalidOperationException("Every server item requires Id, SKU, Name and base unit.");
            if (dto.SalesRevenueAccountId == Guid.Empty)
                throw new InvalidOperationException("Every item requires a sales revenue account.");
            if (dto.ItemKind == ItemKind.Stock && (dto.InventoryAccountId == Guid.Empty || dto.CogsAccountId == Guid.Empty))
                throw new InvalidOperationException("Stock items require inventory and COGS accounts.");

            var row = await _db.Items.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new ServerItem { Id = dto.Id, CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
                _db.Items.Add(row);
            }
            row.Sku = dto.Sku.Trim();
            row.Name = dto.Name.Trim();
            row.BaseUnitOfMeasureId = dto.BaseUnitOfMeasureId;
            row.ItemKind = dto.ItemKind;
            row.InventoryAccountId = dto.InventoryAccountId;
            row.SalesRevenueAccountId = dto.SalesRevenueAccountId;
            row.CogsAccountId = dto.CogsAccountId;
            row.IsActive = dto.IsActive;
        }

        foreach (var dto in request.Warehouses)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
                throw new InvalidOperationException("Every warehouse requires Id, Code and Name.");
            var row = await _db.Warehouses.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new ServerWarehouse { Id = dto.Id, CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
                _db.Warehouses.Add(row);
            }
            row.Code = dto.Code.Trim();
            row.Name = dto.Name.Trim();
            row.AllowNegativeStockOverride = dto.AllowNegativeStockOverride;
        }

        var now = _clock.UtcNow;
        var bootstrap = BuildPayload(request, now);
        var change = new SyncChange
        {
            CompanyId = _tenant.CompanyId,
            BranchId = _tenant.BranchId,
            ChangeType = "TenantMasterDataChanged",
            EntityId = _tenant.CompanyId,
            OperationId = UuidV7.NewGuid(),
            SourceDeviceId = _tenant.DeviceId,
            PayloadJson = JsonSerializer.Serialize(bootstrap),
            ServerTimestampUtc = now
        };
        _db.SyncChanges.Add(change);

        _db.AuditLogs.Add(new ServerAuditLog
        {
            CompanyId = _tenant.CompanyId,
            BranchId = _tenant.BranchId,
            UserId = _tenant.UserId,
            DeviceId = _tenant.DeviceId,
            Action = "ConfigureTenantMasterData",
            EntityId = _tenant.CompanyId,
            EntityType = "TenantConfiguration",
            BeforeJson = before,
            AfterJson = JsonSerializer.Serialize(request),
            TimestampUtc = now
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TenantConfigurationResult(request.Accounts.Count, request.Units.Count, request.Partners.Count,
            request.Items.Count, request.Warehouses.Count, change.ServerWatermark, now);
    }

    private TenantBootstrapResult BuildPayload(TenantConfigurationRequest request, DateTimeOffset now) =>
        new(
            SyncProtocol.CurrentClientSchemaVersion,
            SyncProtocol.CurrentProtocolVersion,
            _tenant.CompanyId,
            _tenant.BranchId,
            request.CompanyName.Trim(),
            request.BranchCode.Trim(),
            request.BranchName.Trim(),
            new CompanyPolicySyncRecord(
                request.Policy.BaseCurrencyCode, request.Policy.CurrencyScale, request.Policy.QuantityScale,
                (int)request.Policy.RoundingMode, (int)request.Policy.InventoryValuationMethod, request.Policy.AllowNegativeStock,
                request.Policy.TaxEnabled, request.Policy.TaxRate, request.Policy.PricesIncludeTax,
                request.Policy.OutputTaxAccountId, request.Policy.InputTaxAccountId),
            request.Accounts.Select(x => new AccountMasterSyncRecord(x.Id, x.Code, x.Name, x.ParentAccountId,
                (int)x.AccountType, (int)x.NormalBalance, x.IsPostable, x.IsActive)).ToArray(),
            request.Units.Select(x => new UnitMasterSyncRecord(x.Id, x.Code, x.Name)).ToArray(),
            request.Partners.Select(x => new BusinessPartnerMasterSyncRecord(x.Id, x.Code, x.Name,
                (int)x.PartnerType, x.ControlAccountId, x.Phone, x.CreditLimit, x.IsActive)).ToArray(),
            request.Items.Select(x => new ItemMasterSyncRecord(x.Id, x.Sku, x.Name, x.BaseUnitOfMeasureId,
                x.InventoryAccountId, x.SalesRevenueAccountId, x.CogsAccountId, (int)x.ItemKind, x.IsActive)).ToArray(),
            request.Warehouses.Select(x => new WarehouseMasterSyncRecord(x.Id, x.Code, x.Name, x.AllowNegativeStockOverride)).ToArray(),
            request.EnabledModules.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).Distinct().ToArray(),
            Array.Empty<string>(),
            now);
}

public sealed class TenantBootstrapService
{
    private readonly ServerAccountingDbContext _db;
    private readonly IServerTenantContext _tenant;
    private readonly TenantPostgresSession _session;
    private readonly IClock _clock;

    public TenantBootstrapService(ServerAccountingDbContext db, IServerTenantContext tenant, TenantPostgresSession session, IClock clock)
    {
        _db = db;
        _tenant = tenant;
        _session = session;
        _clock = clock;
    }

    public async Task<TenantBootstrapResult> GetAsync(TenantBootstrapRequest request, CancellationToken cancellationToken = default)
    {
        SyncProtocol.EnsureCompatible(request.ClientSchemaVersion, request.ProtocolVersion);
        if (!_tenant.IsAuthenticated || _tenant.IsSystem) throw new UnauthorizedAccessException("Authenticated tenant context is required.");
        if (request.DeviceId != _tenant.DeviceId) throw new UnauthorizedAccessException("JWT DeviceId does not match bootstrap request.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);
        var device = await _db.DeviceRegistrations.SingleOrDefaultAsync(x => x.DeviceId == request.DeviceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new UnauthorizedAccessException("Device must be registered before bootstrap.");
        if (!device.IsActive) throw new UnauthorizedAccessException("Device is disabled.");

        var profile = await _db.TenantProfiles.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Tenant profile is not configured.");
        var policy = await _db.CompanyPolicies.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Tenant accounting policy is not configured.");
        var accounts = await _db.Accounts.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var units = await _db.UnitOfMeasures.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var partners = await _db.BusinessPartners.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = await _db.Items.AsNoTracking().OrderBy(x => x.Sku).ToListAsync(cancellationToken).ConfigureAwait(false);
        var warehouses = await _db.Warehouses.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        device.LastSeenAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TenantBootstrapResult(
            SyncProtocol.CurrentClientSchemaVersion,
            SyncProtocol.CurrentProtocolVersion,
            _tenant.CompanyId,
            _tenant.BranchId,
            profile.CompanyName,
            profile.BranchCode,
            profile.BranchName,
            new CompanyPolicySyncRecord(policy.BaseCurrencyCode, policy.CurrencyScale, policy.QuantityScale,
                (int)policy.RoundingMode, (int)policy.InventoryValuationMethod, policy.AllowNegativeStock,
                policy.TaxEnabled, policy.TaxRate, policy.PricesIncludeTax, policy.OutputTaxAccountId, policy.InputTaxAccountId),
            accounts.Select(x => new AccountMasterSyncRecord(x.Id, x.Code, x.Name, x.ParentAccountId,
                (int)x.AccountType, (int)x.NormalBalance, x.IsPostable, x.IsActive)).ToArray(),
            units.Select(x => new UnitMasterSyncRecord(x.Id, x.Code, x.Name)).ToArray(),
            partners.Select(x => new BusinessPartnerMasterSyncRecord(x.Id, x.Code, x.Name,
                (int)x.PartnerType, x.ControlAccountId, x.Phone, x.CreditLimit, x.IsActive)).ToArray(),
            items.Select(x => new ItemMasterSyncRecord(x.Id, x.Sku, x.Name, x.BaseUnitOfMeasureId,
                x.InventoryAccountId, x.SalesRevenueAccountId, x.CogsAccountId, (int)x.ItemKind, x.IsActive)).ToArray(),
            warehouses.Select(x => new WarehouseMasterSyncRecord(x.Id, x.Code, x.Name, x.AllowNegativeStockOverride)).ToArray(),
            profile.EnabledModulesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _tenant.RoleClaims.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            _clock.UtcNow);
    }
}
