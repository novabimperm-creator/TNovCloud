using Microsoft.Data.Sqlite;
using TNovCloud.FileSystemHost;

namespace TNovCloud.Tests;

public sealed class ManifestStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TNovCloud.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(@"\Проекты\Model.rvt", "Проекты/Model.rvt")]
    [InlineData("Проекты//Раздел/", "Проекты/Раздел")]
    [InlineData("", "")]
    public void Normalize_produces_safe_volume_paths(string input, string expected)
        => Assert.Equal(expected, ManifestStore.Normalize(input));

    [Theory]
    [InlineData("../secret")]
    [InlineData("folder/./file")]
    [InlineData("folder/<bad>")]
    public void Normalize_rejects_unsafe_paths(string input)
        => Assert.Throws<UnauthorizedAccessException>(() => ManifestStore.Normalize(input));

    [Fact]
    public void New_file_state_survives_dirty_rename_and_delete_sequence()
    {
        var store = CreateStore();
        store.CreateLocal("Проекты", true);
        store.CreateLocal("Проекты/model.rvt", false);

        File.WriteAllText(store.CachePath("Проекты/model.rvt"), "content");
        store.MarkDirty("Проекты/model.rvt");
        Assert.Equal(EntryState.New, store.Get("Проекты/model.rvt")!.State);

        store.RenameLocal("Проекты/model.rvt", "Проекты/model-v2.rvt", false);
        Assert.Null(store.Get("Проекты/model.rvt"));
        Assert.Equal(EntryState.New, store.Get("Проекты/model-v2.rvt")!.State);

        store.DeleteLocal("Проекты/model-v2.rvt");
        Assert.Null(store.Get("Проекты/model-v2.rvt"));
    }

    [Fact]
    public void CachePath_cannot_escape_cache_root()
    {
        var store = CreateStore();
        Assert.Throws<UnauthorizedAccessException>(() => store.CachePath("../../outside"));
    }

    [Fact]
    public void Existing_manifest_is_migrated_with_revision_and_access_time()
    {
        var database = Path.Combine(_root, "legacy.db");
        Directory.CreateDirectory(_root);
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE entries (
                    path TEXT PRIMARY KEY COLLATE NOCASE,
                    parent_path TEXT NOT NULL COLLATE NOCASE,
                    name TEXT NOT NULL COLLATE NOCASE,
                    is_folder INTEGER NOT NULL,
                    remote_id TEXT NULL,
                    version INTEGER NULL,
                    checked_out_by TEXT NULL,
                    size_bytes INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    original_path TEXT NULL COLLATE NOCASE
                );
                INSERT INTO entries VALUES (
                    '', '', '', 1, '00000000-0000-0000-0000-000000000000',
                    NULL, NULL, 0,
                    '2026-01-01T00:00:00+00:00',
                    '2026-01-02T00:00:00+00:00',
                    0, NULL);
                """;
            command.ExecuteNonQuery();
        }

        var store = new ManifestStore(Path.Combine(_root, "cache"), database);
        var root = store.Get("");

        Assert.NotNull(root);
        Assert.Equal(0, root.Revision);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-02T00:00:00+00:00"),
            root.AccessedAt);
    }

    private ManifestStore CreateStore()
        => new(
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "manifest.db"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // SQLite can briefly retain a mapped page after a failed test.
        }
    }
}
