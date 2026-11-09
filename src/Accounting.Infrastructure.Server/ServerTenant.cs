using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Server;

public interface IServerTenantContext
{
    Guid CompanyId { get; }
    Guid BranchId { get; }
    Guid UserId { get; }
    Guid DeviceId { get; }
    IReadOnlySet<string> RoleClaims { get; }
    bool IsAuthenticated { get; }
    bool IsSystem { get; }
}

public sealed class ServerTenantContext : IServerTenantContext
{
    private HashSet<string> _roleClaims = new(StringComparer.OrdinalIgnoreCase);
    public Guid CompanyId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid DeviceId { get; private set; }
    public IReadOnlySet<string> RoleClaims => _roleClaims;
    public bool IsAuthenticated { get; private set; }
    public bool IsSystem { get; private set; }

    public void SetAuthenticated(Guid companyId, Guid branchId, Guid userId, Guid deviceId, IEnumerable<string> roleClaims)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty || userId == Guid.Empty || deviceId == Guid.Empty)
            throw new UnauthorizedAccessException("CompanyId, BranchId, UserId and DeviceId claims are required.");
        CompanyId = companyId;
        BranchId = branchId;
        UserId = userId;
        DeviceId = deviceId;
        _roleClaims = roleClaims.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IsAuthenticated = true;
        IsSystem = false;
    }

    public void SetSystem()
    {
        CompanyId = Guid.Empty;
        BranchId = Guid.Empty;
        UserId = Guid.Empty;
        DeviceId = Guid.Empty;
        _roleClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IsAuthenticated = true;
        IsSystem = true;
    }

    public UserContext ToUserContext()
    {
        if (!IsAuthenticated || IsSystem) throw new UnauthorizedAccessException("An authenticated tenant context is required.");
        return UserContext.Authenticated(UserId, CompanyId, BranchId, DeviceId);
    }

    public void DemandTenant(Guid companyId, Guid branchId)
    {
        if (!IsAuthenticated || IsSystem || CompanyId != companyId || BranchId != branchId)
            throw new UnauthorizedAccessException("Cross-tenant access is forbidden.");
    }
}

public sealed class TenantPostgresSession
{
    private readonly ServerAccountingDbContext _db;
    private readonly IServerTenantContext _tenant;

    public TenantPostgresSession(ServerAccountingDbContext db, IServerTenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (_tenant.IsSystem) return;
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("PostgreSQL tenant session variables must be set inside a transaction.");

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {_tenant.CompanyId.ToString()}, true), set_config('app.branch_id', {_tenant.BranchId.ToString()}, true);",
            cancellationToken).ConfigureAwait(false);
    }
}
