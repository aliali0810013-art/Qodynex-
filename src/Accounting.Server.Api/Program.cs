using System.Security.Claims;
using Accounting.Domain;
using Accounting.Infrastructure.Server;
using Accounting.Server.Api;
using Accounting.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("AccountingServer")
    ?? throw new InvalidOperationException("ConnectionStrings:AccountingServer is required.");
var adminConnectionString = builder.Configuration.GetConnectionString("AccountingServerAdmin") ?? connectionString;
var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority is required.");
var audience = builder.Configuration["Authentication:Audience"]
    ?? throw new InvalidOperationException("Authentication:Audience is required.");

builder.Services.AddSingleton<ServerImmutabilityInterceptor>();
builder.Services.AddScoped<ServerTenantContext>();
builder.Services.AddScoped<IServerTenantContext>(sp => sp.GetRequiredService<ServerTenantContext>());
builder.Services.AddDbContext<ServerAccountingDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(5));
    options.AddInterceptors(sp.GetRequiredService<ServerImmutabilityInterceptor>());
});
builder.Services.AddScoped<TenantPostgresSession>();
builder.Services.AddScoped<OfficialSequenceGenerator>();
builder.Services.AddScoped<SyncPushCommandHandler>();
builder.Services.AddScoped<SyncPullQueryHandler>();
builder.Services.AddScoped<DeviceRegistrationService>();
builder.Services.AddScoped<TenantAdministrationService>();
builder.Services.AddScoped<TenantBootstrapService>();
builder.Services.AddScoped<ServerDatabaseBootstrapper>();
builder.Services.AddSingleton<ICurrencyPolicyService, CurrencyPolicyService>();
builder.Services.AddSingleton<IClock, SystemClock>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "UserId",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("TenantAdmin", policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            context.User.Claims.Any(c =>
                (c.Type is "RoleClaims" or "role" || c.Type == ClaimTypes.Role)
                && c.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(v => string.Equals(v, "tenant.admin", StringComparison.OrdinalIgnoreCase)))));
});

var app = builder.Build();

{
    var systemTenant = new ServerTenantContext();
    systemTenant.SetSystem();
    var adminOptions = new DbContextOptionsBuilder<ServerAccountingDbContext>()
        .UseNpgsql(adminConnectionString)
        .AddInterceptors(new ServerImmutabilityInterceptor())
        .Options;
    await using var adminDb = new ServerAccountingDbContext(adminOptions, systemTenant);
    await new ServerDatabaseBootstrapper(adminDb).InitializeAsync().ConfigureAwait(false);
}

await using (var runtimeConnection = new NpgsqlConnection(connectionString))
{
    await runtimeConnection.OpenAsync().ConfigureAwait(false);
    await using var roleCheck = runtimeConnection.CreateCommand();
    roleCheck.CommandText = """
SELECT rolsuper OR rolbypassrls
FROM pg_roles
WHERE rolname = current_user;
""";
    var canBypassRls = (bool?)await roleCheck.ExecuteScalarAsync().ConfigureAwait(false) ?? true;
    if (canBypassRls)
        throw new InvalidOperationException(
            "AccountingServer runtime PostgreSQL role must be NOSUPERUSER and NOBYPASSRLS. Use AccountingServerAdmin for schema/RLS bootstrap.");
}

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<TenantClaimsMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    service = "Qodynex Accounting Server",
    protocol = SyncProtocol.CurrentProtocolVersion,
    schema = SyncProtocol.CurrentClientSchemaVersion
}));

app.MapPost("/api/v1/sync/push", async (SyncPushBatch batch, SyncPushCommandHandler handler, CancellationToken ct) =>
    Results.Ok(await handler.HandleAsync(batch, ct).ConfigureAwait(false))).RequireAuthorization();

app.MapPost("/api/v1/sync/pull", async (SyncPullRequest request, SyncPullQueryHandler handler, CancellationToken ct) =>
    Results.Ok(await handler.HandleAsync(request, ct).ConfigureAwait(false))).RequireAuthorization();

app.MapPost("/api/v1/devices/register", async (DeviceRegistrationRequest request, DeviceRegistrationService service, CancellationToken ct) =>
{
    var device = await service.RegisterAsync(request, ct).ConfigureAwait(false);
    return Results.Ok(new
    {
        device.DeviceId,
        device.DeviceName,
        device.Platform,
        device.ClientVersion,
        device.ClientSchemaVersion,
        device.ProtocolVersion,
        device.IsActive,
        device.RegisteredAtUtc,
        device.LastSeenAtUtc
    });
}).RequireAuthorization();

app.MapPost("/api/v1/tenant/bootstrap", async (TenantBootstrapRequest request, TenantBootstrapService service, CancellationToken ct) =>
    Results.Ok(await service.GetAsync(request, ct).ConfigureAwait(false))).RequireAuthorization();

app.MapPost("/api/v1/admin/tenant/configure", async (TenantConfigurationRequest request, TenantAdministrationService service, CancellationToken ct) =>
    Results.Ok(await service.ConfigureAsync(request, ct).ConfigureAwait(false))).RequireAuthorization("TenantAdmin");

app.MapGet("/api/v1/admin/devices", async (ServerAccountingDbContext db, TenantPostgresSession session, CancellationToken ct) =>
{
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
    await session.ApplyAsync(ct).ConfigureAwait(false);
    var rows = await db.DeviceRegistrations.AsNoTracking().OrderBy(x => x.DeviceName).ToListAsync(ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);
    return Results.Ok(rows.Select(x => new
    {
        x.DeviceId, x.DeviceName, x.Platform, x.ClientVersion, x.ClientSchemaVersion, x.ProtocolVersion, x.IsActive, x.LastSeenAtUtc
    }));
}).RequireAuthorization("TenantAdmin");

app.MapGet("/api/v1/sync/conflicts", async (ServerAccountingDbContext db, TenantPostgresSession session, CancellationToken ct) =>
{
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
    await session.ApplyAsync(ct).ConfigureAwait(false);
    var rows = await db.SyncConflicts.AsNoTracking().Where(x => x.RequiresResolution).OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);
    return Results.Ok(rows.Select(x => new
    {
        x.Id, x.OperationId, x.DocumentId, x.DeviceId, x.ConflictCode, x.Message, x.CreatedAtUtc, x.RequiresResolution
    }));
}).RequireAuthorization();

app.Run();

public partial class Program;
