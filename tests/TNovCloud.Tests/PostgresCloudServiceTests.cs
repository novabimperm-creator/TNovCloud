using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TNovCloud.Api.Data;
using TNovCloud.Api.Services;
using TNovCloud.Shared;

namespace TNovCloud.Tests;

public sealed class PostgresCloudServiceTests : IAsyncLifetime
{
    private readonly string _database = "tnovcloud_it_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        "TNovCloud.Api.PgTests",
        Guid.NewGuid().ToString("N"));
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var admin = Environment.GetEnvironmentVariable("TNOV_PG_ADMIN")
                    ?? "Host=127.0.0.1;Port=55432;Username=tnovcloud;Database=postgres";
        await using var connection = new NpgsqlConnection(admin);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "PostgreSQL is not reachable. Start a cluster on 127.0.0.1:55432 or set TNOV_PG_ADMIN.",
                exception);
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{_database}\"";
            await create.ExecuteNonQueryAsync();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = _database
        }.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task Checkin_rejects_stale_major_version()
    {
        await using var db = CreateContext();
        var service = CreateService(db);
        var (user, _) = await SeedUsersAsync(db);
        await using var initial = new MemoryStream([1, 2, 3]);
        await service.UploadNewAsync("Projects/checkin.rvt", initial, null, user.Id, CancellationToken.None);
        await service.CheckoutAsync("Projects/checkin.rvt", user.Id, CancellationToken.None);
        await using var changed = new MemoryStream([4, 5, 6]);

        var error = await Assert.ThrowsAsync<CloudException>(
            () => service.CheckinAsync(
                "Projects/checkin.rvt",
                changed,
                "stale",
                user.Id,
                false,
                0,
                CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Soft_delete_restore_and_move_use_metadata_revision()
    {
        await using var db = CreateContext();
        var service = CreateService(db);
        var (user, _) = await SeedUsersAsync(db);
        await using var initial = new MemoryStream([1]);
        await service.UploadNewAsync("Projects/move.rvt", initial, null, user.Id, CancellationToken.None);
        await service.EnsureFolderAsync("Archive", user.Id, CancellationToken.None);
        var metadata = await service.GetMetadataAsync("Projects/move.rvt", false, CancellationToken.None);

        var moved = await service.MoveAsync(
            new MoveEntryRequest(metadata.Path, "Archive", metadata.Revision),
            user.Id,
            CancellationToken.None);
        Assert.Equal("Archive/move.rvt", moved.Path);

        await service.SoftDeleteAsync(
            new DeleteEntryRequest(moved.Path, moved.Revision),
            user.Id,
            CancellationToken.None);
        var deleted = await service.GetMetadataAsync(moved.Path, true, CancellationToken.None);
        Assert.True(deleted.IsDeleted);

        var restored = await service.RestoreDeletedAsync(
            new RestoreDeletedEntryRequest(deleted.Path, deleted.Revision),
            user.Id,
            CancellationToken.None);
        Assert.False(restored.IsDeleted);
        Assert.True(restored.Revision > moved.Revision);
    }

    [Fact]
    public async Task Soft_deleted_name_can_be_reused_on_postgresql()
    {
        await using var db = CreateContext();
        var service = CreateService(db);
        var (user, _) = await SeedUsersAsync(db);
        await using var first = new MemoryStream([1]);
        await service.UploadNewAsync("Projects/reuse.rvt", first, null, user.Id, CancellationToken.None);
        var metadata = await service.GetMetadataAsync("Projects/reuse.rvt", false, CancellationToken.None);
        await service.SoftDeleteAsync(
            new DeleteEntryRequest(metadata.Path, metadata.Revision),
            user.Id,
            CancellationToken.None);

        await using var second = new MemoryStream([2]);
        await service.UploadNewAsync("Projects/reuse.rvt", second, null, user.Id, CancellationToken.None);
        var replacement = await service.GetMetadataAsync("Projects/reuse.rvt", false, CancellationToken.None);
        Assert.NotEqual(metadata.Id, replacement.Id);
    }

    [Fact]
    public async Task Rename_is_blocked_while_file_is_in_approval()
    {
        await using var db = CreateContext();
        var service = CreateService(db);
        var (user, approver) = await SeedUsersAsync(db);
        await using var initial = new MemoryStream([1]);
        await service.UploadNewAsync("Projects/approval.rvt", initial, null, user.Id, CancellationToken.None);
        await service.StartProcessAsync(
            new CreateProcessRequest("Projects/approval.rvt", "Review", [approver.Id], null),
            user.Id,
            CancellationToken.None);
        var metadata = await service.GetMetadataAsync("Projects/approval.rvt", false, CancellationToken.None);

        var error = await Assert.ThrowsAsync<CloudException>(
            () => service.RenameAsync(
                new RenameEntryRequest(metadata.Path, "renamed.rvt", metadata.Revision),
                user.Id,
                CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
    }

    public async Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_storageRoot, true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
            return;

        var admin = Environment.GetEnvironmentVariable("TNOV_PG_ADMIN")
                    ?? "Host=127.0.0.1;Port=55432;Username=tnovcloud;Database=postgres";
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText =
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @name AND pid <> pg_backend_pid();
                """;
            terminate.Parameters.AddWithValue("name", _database);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_database}\"";
        await drop.ExecuteNonQueryAsync();
    }

    private CloudDbContext CreateContext()
        => new(new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private CloudService CreateService(CloudDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Root"] = _storageRoot
            })
            .Build();
        return new CloudService(db, new StorageService(configuration));
    }

    private async Task<(User User, User Approver)> SeedUsersAsync(CloudDbContext db)
    {
        if (await db.Users.AnyAsync())
        {
            var existing = await db.Users.OrderBy(x => x.Login).ToListAsync();
            return (
                existing.First(x => x.Login == "author"),
                existing.First(x => x.Login == "approver"));
        }

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Login = "author",
            DisplayName = "Author",
            PasswordHash = "",
            CreatedAt = now
        };
        var approver = new User
        {
            Id = Guid.NewGuid(),
            Login = "approver",
            DisplayName = "Approver",
            PasswordHash = "",
            CreatedAt = now
        };
        db.Users.AddRange(user, approver);
        db.Folders.Add(new Folder
        {
            Id = Guid.NewGuid(),
            Name = "",
            CreatedById = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        await db.SaveChangesAsync();
        return (user, approver);
    }
}
