using System.Security.Claims;
using Accounting.Domain;
using Accounting.Infrastructure.Server;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Server.Api;

public sealed class TenantClaimsMiddleware
{
    private readonly RequestDelegate _next;
    public TenantClaimsMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ServerTenantContext tenant)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var company = RequiredGuid(context.User, "CompanyId", "company_id");
            var branch = RequiredGuid(context.User, "BranchId", "branch_id");
            var user = RequiredGuid(context.User, "UserId", ClaimTypes.NameIdentifier, "sub");
            var device = RequiredGuid(context.User, "DeviceId", "device_id");
            var roles = context.User.Claims
                .Where(x => x.Type is "RoleClaims" or "role" || x.Type == ClaimTypes.Role)
                .SelectMany(x => x.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            tenant.SetAuthenticated(company, branch, user, device, roles);
        }

        await _next(context).ConfigureAwait(false);
    }

    private static Guid RequiredGuid(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var type in claimTypes)
        {
            var value = principal.FindFirst(type)?.Value;
            if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty) return parsed;
        }
        throw new UnauthorizedAccessException($"Required JWT claim is missing or invalid: {string.Join("/", claimTypes)}.");
    }
}

public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;
    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (SyncProtocolMismatchException ex)
        {
            await WriteAsync(context, StatusCodes.Status426UpgradeRequired, "SYNC_PROTOCOL_UPGRADE_REQUIRED", ex.Message).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, "TENANT_ACCESS_DENIED", ex.Message).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Database write conflict.");
            await WriteAsync(context, StatusCodes.Status409Conflict, "DATABASE_CONFLICT", "The requested write conflicts with authoritative server state.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AccountingDomainException or InvalidOperationException or JsonException)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, "REQUEST_REJECTED", ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled server error.");
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "SERVER_ERROR", "The server could not complete the request.").ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string code, string detail)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = $"https://qodynex.tech/problems/{code.ToLowerInvariant()}",
            title = code,
            status,
            detail
        }).ConfigureAwait(false);
    }
}
