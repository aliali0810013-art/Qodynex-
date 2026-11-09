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

public sealed record ServerAccountRegistration(Guid Id, string Code, string Name, bool IsPostable, bool IsActive);
public sealed record ServerItemRegistration(Guid Id, string Sku, ItemKind ItemKind, Guid InventoryAccountId, Guid SalesRevenueAccountId, Guid CogsAccountId, bool IsActive);
public sealed record ServerWarehouseRegistration(Guid Id, string Code, bool? AllowNegativeStockOverride);

public sealed record TenantConfigurationRequest(
    TenantPolicyRegistration Policy,
    IReadOnlyList<ServerAccountRegistration> Accounts,
    IReadOnlyList<ServerItemRegistration> Items,
    IReadOnlyList<ServerWarehouseRegistration> Warehouses);

public sealed record TenantConfigurationResult(int Accounts, int Items, int Warehouses, DateTimeOffset ConfiguredAtUtc);

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
        var validatedPolicy = new CompanyPolicy(request.Policy.BaseCurrencyCode, request.Policy.CurrencyScale, request.Policy.RoundingMode,
            request.Policy.InventoryValuationMethod, request.Policy.AllowNegativeStock,
            new TaxPolicy(request.Policy.TaxEnabled, request.Policy.TaxRate, request.Policy.PricesIncludeTax,
                request.Policy.OutputTaxAccountId, request.Policy.InputTaxAccountId),
            request.Policy.QuantityScale);

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);
        var before = JsonSerializer.Serialize(new
        {
            Policy = await _db.CompanyPolicies.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            AccountCount = await _db.Accounts.CountAsync(cancellationToken).ConfigureAwait(false),
            ItemCount = await _db.Items.CountAsync(cancellationToken).ConfigureAwait(false),
            WarehouseCount = await _db.Warehouses.CountAsync(cancellationToken).ConfigureAwait(false)
        });

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
            row.IsPostable = dto.IsPostable;
            row.IsActive = dto.IsActive;
        }

        foreach (var dto in request.Items)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Sku))
                throw new InvalidOperationException("Every server item requires Id and SKU.");
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
            row.ItemKind = dto.ItemKind;
            row.InventoryAccountId = dto.InventoryAccountId;
            row.SalesRevenueAccountId = dto.SalesRevenueAccountId;
            row.CogsAccountId = dto.CogsAccountId;
            row.IsActive = dto.IsActive;
        }

        foreach (var dto in request.Warehouses)
        {
            if (dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Code))
                throw new InvalidOperationException("Every warehouse requires Id and Code.");
            var row = await _db.Warehouses.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new ServerWarehouse { Id = dto.Id, CompanyId = _tenant.CompanyId, BranchId = _tenant.BranchId };
                _db.Warehouses.Add(row);
            }
            row.Code = dto.Code.Trim();
            row.AllowNegativeStockOverride = dto.AllowNegativeStockOverride;
        }

        var now = _clock.UtcNow;
        var after = JsonSerializer.Serialize(new
        {
            Policy = request.Policy,
            AccountCount = request.Accounts.Count,
            ItemCount = request.Items.Count,
            WarehouseCount = request.Warehouses.Count
        });
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
            AfterJson = after,
            TimestampUtc = now
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TenantConfigurationResult(request.Accounts.Count, request.Items.Count, request.Warehouses.Count, now);
    }
}
