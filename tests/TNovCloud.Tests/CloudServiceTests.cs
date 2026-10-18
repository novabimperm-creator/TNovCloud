using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TNovCloud.Api.Data;
using TNovCloud.Api.Services;
using TNovCloud.Shared;

namespace TNovCloud.Tests;

public sealed class CloudServiceTests : IDisposable
{
    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        "TNovCloud.Api.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Checkin_rejects_stale_major_version()
    {
        await using var db = CreateDatabase(out var user, out _);
        var service = CreateService(db);
        await using var initial = new MemoryStream([1, 2, 3]);
        await service.UploadNewAsync("Projects/model.rvt", initial, null, user.Id, CancellationToken.None);
        await service.CheckoutAsync("Projects/model.rvt", user.Id, CancellationToken.None);
        await using var changed = new MemoryStream([4, 5, 6]);

        var error = await Assert.ThrowsAsync<CloudException>(
            () => service.CheckinAsync(
                "Projects/model.rvt",
                changed,
                "stale",
                user.Id,
                false,
                0,
                CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Soft_delete_and_restore_use_metadata_revision()
    {
        await using var db = CreateDatabase(out var user, out _);
        var service = CreateService(db);
        await using var initial = new MemoryStream([1]);
        await service.UploadNewAsync("Projects/model.rvt", initial, null, user.Id, CancellationToken.None);
        var metadata = await service.GetMetadataAsync("Projects/model.rvt", false, CancellationToken.None);

        await service.SoftDeleteAsync(
            new DeleteEntryRequest(metadata.Path, metadata.Revision),
            user.Id,
            CancellationToken.None);
        var deleted = await service.GetMetadataAsync(metadata.Path, true, CancellationToken.None);
        Assert.True(deleted.IsDeleted);

        await service.RestoreDeletedAsync(
            new RestoreDeletedEntryRequest(deleted.Path, deleted.Revision),
            user.Id,
            CancellationToken.None);
        var restored = await service.GetMetadataAsync(metadata.Path, false, CancellationToken.None);
        Assert.False(restored.IsDeleted);
        Assert.True(restored.Revision > metadata.Revision);
    }

    [Fact]
    public async Task Rename_is_blocked_while_file_is_in_approval()
    {
        await using var db = CreateDatabase(out var user, out var approver);
        var service = CreateService(db);
        await using var initial = new MemoryStream([1]);
        await service.UploadNewAsync("Projects/model.rvt", initial, null, user.Id, CancellationToken.None);
        await service.StartProcessAsync(
            new CreateProcessRequest(
                "Projects/model.rvt",
                "Review",
                [approver.Id],
                null),
            user.Id,
            CancellationToken.None);
        var metadata = await service.GetMetadataAsync("Projects/model.rvt", false, CancellationToken.None);

        var error = await Assert.ThrowsAsync<CloudException>(
            () => service.RenameAsync(
                new RenameEntryRequest(metadata.Path, "renamed.rvt", metadata.Revision),
                user.Id,
                CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Directory_children_are_paginated_with_cursor()
    {
        await using var db = CreateDatabase(out var user, out _);
        var service = CreateService(db);
        await using var firstContent = new MemoryStream([1]);
        await using var secondContent = new MemoryStream([2]);
        await service.UploadNewAsync("Projects/a.rvt", firstContent, null, user.Id, CancellationToken.None);
        await service.UploadNewAsync("Projects/b.rvt", secondContent, null, user.Id, CancellationToken.None);

        var first = await service.GetChildrenAsync(
            "Projects", false, null, 1, CancellationToken.None);
        var second = await service.GetChildrenAsync(
            "Projects", false, first.NextCursor, 1, CancellationToken.None);

        Assert.Single(first.Children);
        Assert.NotNull(first.NextCursor);
        Assert.Single(second.Children);
        Assert.NotEqual(first.Children[0].Id, second.Children[0].Id);
        Assert.Null(second.NextCursor);
    }

    private CloudDbContext CreateDatabase(out User user, out User approver)
    {
        var options = new DbContextOptionsBuilder<CloudDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new CloudDbContext(options);
        var now = DateTimeOffset.UtcNow;
        user = new User
        {
            Id = Guid.NewGuid(),
            Login = "author",
            DisplayName = "Author",
            PasswordHash = "",
            CreatedAt = now
        };
        approver = new User
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
        db.SaveChanges();
        return db;
    }

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

    public void Dispose()
    {
        try
        {
            Directory.Delete(_storageRoot, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
