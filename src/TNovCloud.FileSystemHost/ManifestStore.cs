using Microsoft.Data.Sqlite;

namespace TNovCloud.FileSystemHost;

internal sealed class ManifestStore
{
    private readonly object _gate = new();
    private readonly string _cacheRoot;
    private readonly string _connectionString;

    public ManifestStore(string cacheRoot, string databasePath)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
    }

    public ManifestEntry? Get(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM entries WHERE path = $path COLLATE NOCASE";
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }
    }

    public IReadOnlyList<ManifestEntry> GetChildren(string parentPath)
    {
        parentPath = Normalize(parentPath);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT * FROM entries WHERE parent_path = $parent COLLATE NOCASE " +
                "AND state <> $deleted ORDER BY name COLLATE NOCASE";
            command.Parameters.AddWithValue("$parent", parentPath);
            command.Parameters.AddWithValue("$deleted", (int)EntryState.Deleted);
            using var reader = command.ExecuteReader();
            var result = new List<ManifestEntry>();
            while (reader.Read())
                result.Add(ReadEntry(reader));
            return result;
        }
    }

    public ManifestEntry CreateLocal(string path, bool isFolder)
    {
        path = Normalize(path);
        if (path.Length == 0)
            throw new IOException("The volume root already exists.");

        lock (_gate)
        {
            if (GetLocked(path) is not null)
                throw new IOException("An entry with the same name already exists.");
            var parent = GetLocked(Parent(path));
            if (parent is null || !parent.IsFolder)
                throw new DirectoryNotFoundException("Parent directory does not exist.");

            var localPath = CachePath(path);
            if (isFolder)
                Directory.CreateDirectory(localPath);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                using var unused = new FileStream(
                    localPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }

            var now = DateTimeOffset.UtcNow;
            var entry = new ManifestEntry(
                path, isFolder, null, null, null, 0, now, now, EntryState.New, null)
            {
                AccessedAt = now
            };
            UpsertLocked(entry);
            return entry;
        }
    }

    public void MarkDirty(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            var state = entry.State == EntryState.New ? EntryState.New : EntryState.Dirty;
            var size = entry.IsFolder ? 0 : new System.IO.FileInfo(CachePath(path)).Length;
            UpsertLocked(entry with
            {
                State = state,
                SizeBytes = size,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    public void UpdateMetadata(
        string path,
        DateTimeOffset? creationTime,
        DateTimeOffset? accessTime,
        DateTimeOffset? writeTime)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            var local = CachePath(path);
            if (creationTime.HasValue)
                File.SetCreationTimeUtc(local, creationTime.Value.UtcDateTime);
            if (accessTime.HasValue)
                File.SetLastAccessTimeUtc(local, accessTime.Value.UtcDateTime);
            if (writeTime.HasValue)
                File.SetLastWriteTimeUtc(local, writeTime.Value.UtcDateTime);
            UpsertLocked(entry with
            {
                CreatedAt = creationTime ?? entry.CreatedAt,
                AccessedAt = accessTime ?? entry.AccessedAt,
                UpdatedAt = writeTime ?? entry.UpdatedAt
            });
        }
    }

    public void TouchAccess(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            UpsertLocked(entry with { AccessedAt = DateTimeOffset.UtcNow });
        }
    }

    public void DeleteLocal(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path);
            if (entry is null)
                return;
            var local = CachePath(path);
            if (entry.IsFolder)
                Directory.Delete(local, false);
            else
                File.Delete(local);

            using var connection = Open();
            using var command = connection.CreateCommand();
            if (entry.IsRemote)
            {
                command.CommandText =
                    "UPDATE entries SET state = $state, updated_at = $updated WHERE path = $path COLLATE NOCASE";
                command.Parameters.AddWithValue("$state", (int)EntryState.Deleted);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            }
            else
                command.CommandText = "DELETE FROM entries WHERE path = $path COLLATE NOCASE";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public void RenameLocal(string path, string newPath, bool replace)
    {
        path = Normalize(path);
        newPath = Normalize(newPath);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            var destination = GetLocked(newPath);
            if (destination is not null)
            {
                if (!replace || destination.IsFolder != entry.IsFolder)
                    throw new IOException("The destination already exists.");
                DeleteLocal(newPath);
            }

            var parent = GetLocked(Parent(newPath));
            if (parent is null || !parent.IsFolder)
                throw new DirectoryNotFoundException("Destination parent does not exist.");

            var sourceLocal = CachePath(path);
            var destinationLocal = CachePath(newPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationLocal)!);
            if (entry.IsFolder)
                Directory.Move(sourceLocal, destinationLocal);
            else
                File.Move(sourceLocal, destinationLocal);

            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var affected = GetDescendantsLocked(connection, transaction, path);
            foreach (var current in affected.OrderBy(x => x.Path.Length))
            {
                var suffix = current.Path.Length == path.Length ? "" : current.Path[path.Length..];
                var movedPath = newPath + suffix;
                DeleteRow(connection, transaction, current.Path);
                var original = current.OriginalPath ??
                               (current.IsRemote ? current.Path : null);
                Upsert(connection, transaction, current with
                {
                    Path = movedPath,
                    State = current.IsRemote ? EntryState.Renamed : current.State,
                    OriginalPath = original,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            transaction.Commit();
        }
    }

    public string CachePath(string path)
    {
        path = Normalize(path);
        var result = Path.GetFullPath(Path.Combine(
            _cacheRoot,
            path.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(result, _cacheRoot, StringComparison.OrdinalIgnoreCase) &&
            !result.StartsWith(_cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes the file cache.");
        return result;
    }

    public async Task SynchronizeAsync(
        CloudApi api,
        Guid currentUserId,
        CancellationToken cancellationToken)
    {
        foreach (var pending in GetPendingNewEntries().OrderBy(x => Depth(x.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.IsFolder)
                await api.CreateDirectoryAsync(pending.Path, cancellationToken);
            else
                await api.UploadNewAsync(pending.Path, CachePath(pending.Path), cancellationToken);
            MarkUploaded(pending.Path);
        }

        var tree = await api.GetTreeAsync(cancellationToken);
        var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };

        foreach (var dto in tree.OrderBy(x => Depth(x.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Normalize(dto.Path);
            remotePaths.Add(path);
            var existing = Get(path);
            if (existing is null && HasPendingOriginal(path))
                continue;

            if (dto.IsFolder)
            {
                Directory.CreateDirectory(CachePath(path));
                UpsertRemote(dto, path, 0, existing);
                continue;
            }

            var local = CachePath(path);
            var localIsProtected = existing?.State is EntryState.New
                or EntryState.Dirty
                or EntryState.Renamed
                or EntryState.Deleted;
            if (!localIsProtected &&
                existing?.Version is not null &&
                existing.Version != dto.Version &&
                File.Exists(local))
            {
                File.Delete(local);
            }

            UpsertRemote(dto, path, dto.SizeBytes ?? 0, existing);
        }

        RemoveStaleRemoteEntries(remotePaths);
    }

    public void MarkUploaded(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            UpsertLocked(entry with
            {
                RemoteId = entry.RemoteId ?? Guid.Empty,
                State = EntryState.Remote,
                Version = entry.IsFolder ? null : 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    public void MarkRemoteMutation(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            UpsertLocked(entry with
            {
                State = EntryState.Remote,
                OriginalPath = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    public void DeleteRemoteConfirmed(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path);
            if (entry is null)
                return;
            var local = CachePath(path);
            if (entry.IsFolder)
                Directory.Delete(local, false);
            else
                File.Delete(local);
            using var connection = Open();
            DeleteRow(connection, path);
        }
    }

    public void MarkCommitted(
        string path,
        int version,
        long revision,
        Guid? checkedOutById)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            var local = CachePath(path);
            UpsertLocked(entry with
            {
                Version = version,
                Revision = revision,
                CheckedOutById = checkedOutById,
                SizeBytes = File.Exists(local) ? new System.IO.FileInfo(local).Length : entry.SizeBytes,
                State = EntryState.Remote,
                OriginalPath = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    public void DiscardLocalChanges(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            var entry = GetLocked(path) ?? throw new FileNotFoundException();
            if (!entry.IsRemote)
                throw new InvalidOperationException("Новый неотправленный файл нельзя отменить как checkout.");
            var local = CachePath(path);
            if (!entry.IsFolder)
                File.Delete(local);
            UpsertLocked(entry with
            {
                CheckedOutById = null,
                State = EntryState.Remote,
                OriginalPath = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    public static string Normalize(string path)
    {
        path = (path ?? "").Replace('\\', '/').Trim('/');
        if (path.Length == 0)
            return "";
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(x => x is "." or ".." || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new UnauthorizedAccessException("Invalid volume path.");
        return string.Join('/', components);
    }

    public static string Parent(string path)
    {
        path = Normalize(path);
        var separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS entries (
                    path TEXT PRIMARY KEY COLLATE NOCASE,
                    parent_path TEXT NOT NULL COLLATE NOCASE,
                    name TEXT NOT NULL COLLATE NOCASE,
                    is_folder INTEGER NOT NULL,
                    remote_id TEXT NULL,
                    version INTEGER NULL,
                    revision INTEGER NOT NULL DEFAULT 0,
                    checked_out_by TEXT NULL,
                    size_bytes INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    accessed_at TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    original_path TEXT NULL COLLATE NOCASE
                );
                CREATE INDEX IF NOT EXISTS ix_entries_parent
                    ON entries(parent_path COLLATE NOCASE, name COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS ix_entries_original
                    ON entries(original_path COLLATE NOCASE);
                """;
            command.ExecuteNonQuery();
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(entries)";
                using var reader = columns.ExecuteReader();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                    names.Add(reader.GetString(reader.GetOrdinal("name")));
                reader.Close();
                if (!names.Contains("revision"))
                {
                    using var migrate = connection.CreateCommand();
                    migrate.CommandText =
                        "ALTER TABLE entries ADD COLUMN revision INTEGER NOT NULL DEFAULT 0";
                    migrate.ExecuteNonQuery();
                }
                if (!names.Contains("accessed_at"))
                {
                    using var migrate = connection.CreateCommand();
                    migrate.CommandText =
                        "ALTER TABLE entries ADD COLUMN accessed_at TEXT NOT NULL " +
                        "DEFAULT '1970-01-01T00:00:00.0000000+00:00'";
                    migrate.ExecuteNonQuery();
                    using var initializeAccess = connection.CreateCommand();
                    initializeAccess.CommandText =
                        "UPDATE entries SET accessed_at = updated_at";
                    initializeAccess.ExecuteNonQuery();
                }
            }
            Directory.CreateDirectory(_cacheRoot);
            if (GetLocked("") is null)
            {
                var now = DateTimeOffset.UtcNow;
                UpsertLocked(new ManifestEntry(
                    "", true, Guid.Empty, null, null, 0, now, now, EntryState.Remote, null)
                {
                    AccessedAt = now
                });
            }
        }
    }

    private void UpsertRemote(FsEntryDto dto, string path, long size, ManifestEntry? existing)
    {
        if (existing?.State is EntryState.New
            or EntryState.Dirty
            or EntryState.Renamed
            or EntryState.Deleted)
            return;

        lock (_gate)
        {
            UpsertLocked(new ManifestEntry(
                path,
                dto.IsFolder,
                dto.Id,
                dto.Version,
                dto.CheckedOutById,
                size,
                existing?.CreatedAt ?? dto.UpdatedAt,
                dto.UpdatedAt,
                EntryState.Remote,
                null)
            {
                Revision = dto.Revision,
                AccessedAt = existing?.AccessedAt ?? dto.UpdatedAt
            });
        }
    }

    private bool HasPendingOriginal(string path)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM entries WHERE original_path = $path COLLATE NOCASE " +
                "AND state IN ($renamed, $deleted) LIMIT 1";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$renamed", (int)EntryState.Renamed);
            command.Parameters.AddWithValue("$deleted", (int)EntryState.Deleted);
            return command.ExecuteScalar() is not null;
        }
    }

    private void RemoveStaleRemoteEntries(HashSet<string> remotePaths)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM entries WHERE state = $remote AND path <> ''";
            command.Parameters.AddWithValue("$remote", (int)EntryState.Remote);
            using var reader = command.ExecuteReader();
            var stale = new List<ManifestEntry>();
            while (reader.Read())
            {
                var entry = ReadEntry(reader);
                if (!remotePaths.Contains(entry.Path))
                    stale.Add(entry);
            }
            reader.Close();

            foreach (var entry in stale.OrderByDescending(x => x.Path.Length))
            {
                try
                {
                    if (entry.IsFolder)
                        Directory.Delete(CachePath(entry.Path), false);
                    else
                        File.Delete(CachePath(entry.Path));
                }
                catch (IOException)
                {
                    continue;
                }
                DeleteRow(connection, entry.Path);
            }
        }
    }

    private IReadOnlyList<ManifestEntry> GetPendingNewEntries()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM entries WHERE state = $state";
            command.Parameters.AddWithValue("$state", (int)EntryState.New);
            using var reader = command.ExecuteReader();
            var result = new List<ManifestEntry>();
            while (reader.Read())
                result.Add(ReadEntry(reader));
            return result;
        }
    }

    private List<ManifestEntry> GetDescendantsLocked(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT * FROM entries WHERE path = $path COLLATE NOCASE " +
            "OR path LIKE $prefix ESCAPE '\\' COLLATE NOCASE";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue(
            "$prefix",
            path.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%");
        using var reader = command.ExecuteReader();
        var result = new List<ManifestEntry>();
        while (reader.Read())
            result.Add(ReadEntry(reader));
        return result;
    }

    private ManifestEntry? GetLocked(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM entries WHERE path = $path COLLATE NOCASE";
        command.Parameters.AddWithValue("$path", Normalize(path));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private void UpsertLocked(ManifestEntry entry)
    {
        using var connection = Open();
        Upsert(connection, entry);
    }

    private static void Upsert(SqliteConnection connection, ManifestEntry entry)
        => Upsert(connection, null, entry);

    private static void Upsert(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ManifestEntry entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entries (
                path, parent_path, name, is_folder, remote_id, version, revision, checked_out_by,
                size_bytes, created_at, updated_at, accessed_at, state, original_path)
            VALUES (
                $path, $parent, $name, $folder, $remote, $version, $revision, $checkout,
                $size, $created, $updated, $accessed, $state, $original)
            ON CONFLICT(path) DO UPDATE SET
                parent_path = excluded.parent_path,
                name = excluded.name,
                is_folder = excluded.is_folder,
                remote_id = excluded.remote_id,
                version = excluded.version,
                revision = excluded.revision,
                checked_out_by = excluded.checked_out_by,
                size_bytes = excluded.size_bytes,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                accessed_at = excluded.accessed_at,
                state = excluded.state,
                original_path = excluded.original_path
            """;
        command.Parameters.AddWithValue("$path", entry.Path);
        command.Parameters.AddWithValue("$parent", Parent(entry.Path));
        command.Parameters.AddWithValue("$name", Path.GetFileName(entry.Path.Replace('/', '\\')));
        command.Parameters.AddWithValue("$folder", entry.IsFolder ? 1 : 0);
        command.Parameters.AddWithValue("$remote", (object?)entry.RemoteId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)entry.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$checkout", (object?)entry.CheckedOutById?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", entry.SizeBytes);
        command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", entry.UpdatedAt.ToString("O"));
        var accessedAt = entry.AccessedAt == default ? entry.UpdatedAt : entry.AccessedAt;
        command.Parameters.AddWithValue("$accessed", accessedAt.ToString("O"));
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$original", (object?)entry.OriginalPath ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void DeleteRow(SqliteConnection connection, string path)
        => DeleteRow(connection, null, path);

    private static void DeleteRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM entries WHERE path = $path COLLATE NOCASE";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private static ManifestEntry ReadEntry(SqliteDataReader reader)
        => new ManifestEntry(
            reader.GetString(reader.GetOrdinal("path")),
            reader.GetInt64(reader.GetOrdinal("is_folder")) != 0,
            ParseGuid(reader, "remote_id"),
            reader.IsDBNull(reader.GetOrdinal("version"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("version")),
            ParseGuid(reader, "checked_out_by"),
            reader.GetInt64(reader.GetOrdinal("size_bytes")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            (EntryState)reader.GetInt32(reader.GetOrdinal("state")),
            reader.IsDBNull(reader.GetOrdinal("original_path"))
                ? null
                : reader.GetString(reader.GetOrdinal("original_path")))
        {
            Revision = reader.GetInt64(reader.GetOrdinal("revision")),
            AccessedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("accessed_at")))
        };

    private static Guid? ParseGuid(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static int Depth(string path) => path.Count(c => c is '/' or '\\');
}
