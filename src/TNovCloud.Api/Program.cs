using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TNovCloud.Api.Data;
using TNovCloud.Api.Services;
using TNovCloud.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddDbContext<CloudDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddScoped<CloudService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<JwtTokenService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "dev-only-change-me-to-a-long-secret-key!";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "TNovCloud",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "TNovCloud",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1024L * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024L * 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

await Seed.EnsureAsync(app.Services);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/auth/login", async (LoginRequest body, CloudService cloud, JwtTokenService jwt, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await cloud.LoginAsync(body, jwt, ct));
    }
    catch (CloudException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/auth/me", async (ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
{
    var id = UserId(user);
    var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    return row is null ? Results.Unauthorized() : Results.Ok(new UserDto(row.Id, row.Login, row.DisplayName, row.Email));
});

api.MapGet("/users", async (CloudService cloud, CancellationToken ct) => Results.Ok(await cloud.ListUsersAsync(ct)));
api.MapGet("/fs/tree", async (CloudService cloud, CancellationToken ct) => Results.Ok(await cloud.GetTreeAsync(ct)));

api.MapPost("/fs/mkdir", async (string path, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        await cloud.EnsureFolderAsync(path, UserId(user), ct);
        return Results.Ok();
    });
});

api.MapPost("/fs/upload", async (string path, string? comment, IFormFile file, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        await using var stream = file.OpenReadStream();
        await cloud.UploadNewAsync(path, stream, comment, UserId(user), ct);
        return Results.Ok();
    });
}).DisableAntiforgery();

api.MapPost("/fs/checkout", async (string path, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        await cloud.CheckoutAsync(path, UserId(user), ct);
        return Results.Ok();
    });
});

api.MapPost("/fs/checkin", async (string path, string? comment, bool keepCheckout, int expectedVersion, IFormFile file, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        await using var stream = file.OpenReadStream();
        await cloud.CheckinAsync(path, stream, comment, UserId(user), keepCheckout, expectedVersion, ct);
        return Results.Ok();
    });
}).DisableAntiforgery();

api.MapPost("/fs/cancel", async (string path, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        await cloud.CancelCheckoutAsync(path, UserId(user), ct);
        return Results.Ok();
    });
});

api.MapGet("/fs/details", async (string path, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.GetDetailsAsync(path, ct)));
});

api.MapGet("/fs/children", async (
    string? path,
    bool includeDeleted,
    string? cursor,
    int? pageSize,
    HttpContext http,
    CloudService cloud,
    CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        var result = await cloud.GetChildrenAsync(
            path ?? "",
            includeDeleted,
            cursor,
            pageSize ?? 200,
            ct);
        http.Response.Headers.ETag = $"\"{result.Directory.Revision}\"";
        return Results.Ok(result);
    });
});

api.MapGet("/fs/metadata", async (
    string? path,
    bool includeDeleted,
    HttpContext http,
    CloudService cloud,
    CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        var result = await cloud.GetMetadataAsync(path ?? "", includeDeleted, ct);
        http.Response.Headers.ETag = $"\"{result.Revision}\"";
        return Results.Ok(result);
    });
});

api.MapPost("/fs/rename", async (RenameEntryRequest body, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.RenameAsync(body, UserId(user), ct)));
});

api.MapPost("/fs/move", async (MoveEntryRequest body, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.MoveAsync(body, UserId(user), ct)));
});

api.MapPost("/fs/delete", async (DeleteEntryRequest body, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        await cloud.SoftDeleteAsync(body, UserId(user), ct);
        return Results.Ok();
    });
});

api.MapPost("/fs/restore-deleted", async (RestoreDeletedEntryRequest body, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.RestoreDeletedAsync(body, UserId(user), ct)));
});

api.MapGet("/fs/audit", async (string? path, int? limit, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.GetAuditAsync(path, limit ?? 100, ct)));
});

api.MapGet("/fs/content", async (string path, int? version, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        var (file, ver) = await cloud.OpenContentAsync(path, version, ct);
        return Results.Stream(cloud.OpenStorage(ver.StorageKey), "application/octet-stream", file.Name);
    });
});

api.MapPost("/fs/restore", async (string path, int version, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () =>
    {
        await cloud.RestoreAsync(path, version, UserId(user), ct);
        return Results.Ok();
    });
});

api.MapPost("/processes", async (CreateProcessRequest body, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.StartProcessAsync(body, UserId(user), ct)));
});

api.MapGet("/processes/{id:guid}", async (Guid id, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.GetProcessAsync(id, ct)));
});

api.MapGet("/tasks/inbox", async (ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.InboxAsync(UserId(user), ct)));
});

api.MapPost("/tasks/{id:guid}/decide", async (Guid id, DecideRequest body, ClaimsPrincipal user, CloudService cloud, CancellationToken ct) =>
{
    return await Guard(async () => Results.Ok(await cloud.DecideAsync(id, body, UserId(user), ct)));
});

app.Run();

static Guid UserId(ClaimsPrincipal user)
{
    var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(value, out var id) ? id : throw new CloudException(401, "Нет идентификатора пользователя.");
}

static async Task<IResult> Guard(Func<Task<IResult>> action)
{
    try
    {
        return await action();
    }
    catch (CloudException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
}

internal static class Seed
{
    public static async Task EnsureAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        await BootstrapLegacyMigrationAsync(db);
        await db.Database.MigrateAsync();

        if (!await db.Users.AnyAsync())
        {
            var admin = NewUser("admin", "Администратор", "admin");
            var designer = NewUser("designer", "Проектировщик", "designer");
            db.Users.AddRange(admin, designer);

            var root = new Folder
            {
                Id = Guid.NewGuid(),
                Name = "",
                CreatedById = admin.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var projects = new Folder
            {
                Id = Guid.NewGuid(),
                ParentId = root.Id,
                Name = "Проекты",
                CreatedById = admin.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var demo = new Folder
            {
                Id = Guid.NewGuid(),
                ParentId = projects.Id,
                Name = "Демо",
                CreatedById = admin.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Folders.AddRange(root, projects, demo);
            await db.SaveChangesAsync();
        }
    }

    private static async Task BootstrapLegacyMigrationAsync(CloudDbContext db)
    {
        if (!await db.Database.CanConnectAsync())
            return;

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );

            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            SELECT '20260902091003_InitialLegacySchema', '10.0.0'
            WHERE to_regclass('"Users"') IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "__EFMigrationsHistory"
                  WHERE "MigrationId" = '20260902091003_InitialLegacySchema'
              );
            """);
    }

    private static User NewUser(string login, string name, string password) => new()
    {
        Id = Guid.NewGuid(),
        Login = login,
        DisplayName = name,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        CreatedAt = DateTimeOffset.UtcNow
    };
}
