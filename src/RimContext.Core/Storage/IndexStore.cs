using Microsoft.Data.Sqlite;
using RimContext.Core.Configuration;
using RimContext.Core.Contracts;
using RimContext.Core.Model;

namespace RimContext.Core.Storage;

public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly bool writable;
    private bool disposed;

    private IndexStore(SqliteConnection connection, bool writable, StoreMetadata metadata)
    {
        this.connection = connection;
        this.writable = writable;
        Metadata = metadata;
    }

    public StoreMetadata Metadata { get; }

    public static IndexStore CreateNew(string path, StoreMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(metadata);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var connection = new SqliteConnection(BuildConnectionString(path, SqliteOpenMode.ReadWriteCreate));
        try
        {
            connection.Open();
            ConfigureConnection(connection);
            using var transaction = connection.BeginTransaction();
            IndexSchema.Create(connection, transaction);
            WriteMetadata(connection, transaction, metadata);
            transaction.Commit();
            return new IndexStore(connection, writable: true, metadata);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static IndexStore OpenReadOnly(
        WorkspaceConfiguration configuration,
        int expectedSchemaVersion = IndexConstants.SchemaVersion)
    {
        return OpenExisting(configuration, SqliteOpenMode.ReadOnly, writable: false, expectedSchemaVersion);
    }

    public static IndexStore OpenWritable(
        WorkspaceConfiguration configuration,
        int expectedSchemaVersion = IndexConstants.SchemaVersion)
    {
        return OpenExisting(configuration, SqliteOpenMode.ReadWrite, writable: true, expectedSchemaVersion);
    }

    private static IndexStore OpenExisting(
        WorkspaceConfiguration configuration,
        SqliteOpenMode mode,
        bool writable,
        int expectedSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!File.Exists(configuration.StorePath))
        {
            throw ErrorFactory.IndexNotFound();
        }

        var connection = new SqliteConnection(BuildConnectionString(configuration.StorePath, mode));
        try
        {
            connection.Open();
            ConfigureConnection(connection);
            IndexSchema.Validate(connection, expectedSchemaVersion);
            var metadata = ReadMetadata(connection);
            if (metadata.SchemaVersion != expectedSchemaVersion)
            {
                throw ErrorFactory.IndexIncompatible(
                    $"The index metadata schema version is {metadata.SchemaVersion}, but version {expectedSchemaVersion} is required.",
                    new { expected = expectedSchemaVersion, actual = metadata.SchemaVersion });
            }
            if (!metadata.ToolVersion.Equals(IndexConstants.ToolVersion, StringComparison.Ordinal))
            {
                throw ErrorFactory.IndexIncompatible(
                    $"The index was created by tool version {metadata.ToolVersion}, but version {IndexConstants.ToolVersion} is required.",
                    new { expected = IndexConstants.ToolVersion, actual = metadata.ToolVersion });
            }
            if (!metadata.WorkspaceIdentity.Equals(configuration.WorkspaceIdentity, StringComparison.Ordinal))
            {
                throw ErrorFactory.RootMismatch(
                    "The index belongs to a different workspace root.",
                    new { expected = configuration.WorkspaceIdentity, actual = metadata.WorkspaceIdentity });
            }

            if (!metadata.ConfigurationFingerprint.Equals(configuration.ConfigurationFingerprint, StringComparison.Ordinal))
            {
                throw ErrorFactory.RootMismatch(
                    "The index configuration does not match the selected workspace.",
                    new { expected = configuration.ConfigurationFingerprint, actual = metadata.ConfigurationFingerprint });
            }

            return new IndexStore(connection, writable, metadata);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void WriteBatch(
        IEnumerable<IndexedFileRecord> files,
        IEnumerable<EntityRecord> entities,
        IEnumerable<RelationRecord> relations)
    {
        EnsureWritable();
        using var transaction = connection.BeginTransaction();

        foreach (var file in files)
        {
            InsertFile(file, transaction);
        }

        foreach (var entity in entities)
        {
            InsertEntity(entity, transaction);
        }

        foreach (var relation in relations)
        {
            InsertRelation(relation, transaction);
        }

        transaction.Commit();
    }

    public void ApplyIncremental(
        IEnumerable<IndexedFileRecord> addedOrChangedFiles,
        IEnumerable<IndexedFileRecord> metadataUpdates,
        IEnumerable<string> removedFileIds,
        IEnumerable<EntityRecord> replacementEntities,
        IEnumerable<RelationRecord> replacementRelations,
        StoreMetadata metadata,
        IEnumerable<string>? derivedRefreshFileIds = null,
        bool replaceAllRelations = false)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(addedOrChangedFiles);
        ArgumentNullException.ThrowIfNull(metadataUpdates);
        ArgumentNullException.ThrowIfNull(removedFileIds);
        ArgumentNullException.ThrowIfNull(replacementEntities);
        ArgumentNullException.ThrowIfNull(replacementRelations);
        ArgumentNullException.ThrowIfNull(metadata);

        var addedOrChanged = addedOrChangedFiles.ToArray();
        var metadataOnly = metadataUpdates.ToArray();
        var removed = removedFileIds.Distinct(StringComparer.Ordinal).ToArray();
        var derivedRefresh = (derivedRefreshFileIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cleanup = addedOrChanged
            .Select(file => file.Id)
            .Concat(removed)
            .Concat(derivedRefresh)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using var transaction = connection.BeginTransaction();
        DeleteDerivedRecordsForFiles(cleanup, transaction);

        foreach (var fileId in removed.Concat(addedOrChanged.Select(file => file.Id)).Distinct(StringComparer.Ordinal))
        {
            DeleteFile(fileId, transaction);
        }

        foreach (var file in metadataOnly)
        {
            UpdateFileMetadata(file, transaction);
        }

        foreach (var file in addedOrChanged)
        {
            InsertFile(file, transaction);
        }

        if (replaceAllRelations)
        {
            using var deleteRelations = connection.CreateCommand();
            deleteRelations.Transaction = transaction;
            deleteRelations.CommandText = "DELETE FROM relations;";
            deleteRelations.ExecuteNonQuery();
        }

        foreach (var entity in replacementEntities)
        {
            InsertEntity(entity, transaction);
        }

        foreach (var relation in replacementRelations)
        {
            InsertRelation(relation, transaction);
        }

        WriteMetadata(connection, transaction, metadata);
        transaction.Commit();
    }

    public void RemoveDerivedRecordsForFiles(IEnumerable<string> fileIds)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(fileIds);
        using var transaction = connection.BeginTransaction();
        DeleteDerivedRecordsForFiles(fileIds, transaction);
        transaction.Commit();
    }

    public IndexCounts GetCounts()
    {
        EnsureNotDisposed();
        return new IndexCounts(
            Count("files"),
            Count("entities"),
            Count("relations"));
    }

    public IReadOnlyList<IndexedFileRecord> GetFiles()
    {
        EnsureNotDisposed();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, path, content_hash, size_bytes, modified_utc_ticks,
                   workspace_identity, parse_status, diagnostic
            FROM files
            ORDER BY path COLLATE BINARY, id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<IndexedFileRecord>();
        while (reader.Read())
        {
            result.Add(new IndexedFileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }

    public IReadOnlyList<EntityRecord> GetEntities()
    {
        EnsureNotDisposed();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, identity_key, file_id, line, payload_json
            FROM entities
            ORDER BY id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<EntityRecord>();
        while (reader.Read())
        {
            result.Add(new EntityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetString(5)));
        }

        return result;
    }

    public IReadOnlyList<RelationRecord> GetRelations()
    {
        EnsureNotDisposed();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, from_id, to_id, kind, file_id, line, payload_json
            FROM relations
            ORDER BY id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<RelationRecord>();
        while (reader.Read())
        {
            result.Add(new RelationRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetString(6)));
        }

        return result;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connection.Dispose();
    }

    private static string BuildConnectionString(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        };
        return builder.ToString();
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static StoreMetadata ReadMetadata(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta ORDER BY key COLLATE BINARY;";
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        var required = new[]
        {
            "schema_version",
            "tool_version",
            "workspace_identity",
            "workspace_root",
            "configuration_fingerprint",
            "indexed_at_utc"
        };
        var missing = required.Where(key => !values.ContainsKey(key)).ToArray();
        if (missing.Length > 0 || !int.TryParse(values.GetValueOrDefault("schema_version"), out var schemaVersion))
        {
            throw ErrorFactory.IndexIncompatible("The index metadata is incomplete.", new { missing });
        }

        return new StoreMetadata(
            schemaVersion,
            values["tool_version"],
            values["workspace_identity"],
            values["workspace_root"],
            values["configuration_fingerprint"],
            values["indexed_at_utc"]);
    }

    private static void WriteMetadata(SqliteConnection connection, SqliteTransaction transaction, StoreMetadata metadata)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema_version"] = metadata.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["tool_version"] = metadata.ToolVersion,
            ["workspace_identity"] = metadata.WorkspaceIdentity,
            ["workspace_root"] = metadata.WorkspaceRoot,
            ["configuration_fingerprint"] = metadata.ConfigurationFingerprint,
            ["indexed_at_utc"] = metadata.IndexedAtUtc
        };

        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO meta (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", pair.Key);
            command.Parameters.AddWithValue("$value", pair.Value);
            command.ExecuteNonQuery();
        }
    }

    private void DeleteDerivedRecordsForFiles(IEnumerable<string> fileIds, SqliteTransaction transaction)
    {
        foreach (var fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            var entityIds = GetEntityIdsForFile(fileId, transaction);
            foreach (var entityId in entityIds)
            {
                DeleteRelationsForEntity(entityId, fileId, transaction);
            }

            DeleteRelationsForFile(fileId, transaction);

            using var deleteEntities = connection.CreateCommand();
            deleteEntities.Transaction = transaction;
            deleteEntities.CommandText = "DELETE FROM entities WHERE file_id = $file_id;";
            deleteEntities.Parameters.AddWithValue("$file_id", fileId);
            deleteEntities.ExecuteNonQuery();
        }
    }

    private List<string> GetEntityIdsForFile(string fileId, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM entities WHERE file_id = $file_id ORDER BY id COLLATE BINARY;";
        command.Parameters.AddWithValue("$file_id", fileId);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private void DeleteRelationsForEntity(string entityId, string fileId, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM relations
            WHERE file_id = $file_id OR from_id = $entity_id OR to_id = $entity_id;
            """;
        command.Parameters.AddWithValue("$file_id", fileId);
        command.Parameters.AddWithValue("$entity_id", entityId);
        command.ExecuteNonQuery();
    }

    private void DeleteRelationsForFile(string fileId, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM relations WHERE file_id = $file_id;";
        command.Parameters.AddWithValue("$file_id", fileId);
        command.ExecuteNonQuery();
    }

    private void DeleteFile(string fileId, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", fileId);
        command.ExecuteNonQuery();
    }

    private void UpdateFileMetadata(IndexedFileRecord file, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE files
            SET kind = $kind,
                path = $path,
                content_hash = $content_hash,
                size_bytes = $size_bytes,
                modified_utc_ticks = $modified_utc_ticks,
                workspace_identity = $workspace_identity,
                parse_status = $parse_status,
                diagnostic = $diagnostic
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", file.Id);
        command.Parameters.AddWithValue("$kind", file.Kind);
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$content_hash", file.ContentHash);
        command.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
        command.Parameters.AddWithValue("$modified_utc_ticks", file.ModifiedUtcTicks);
        command.Parameters.AddWithValue("$workspace_identity", file.WorkspaceIdentity);
        command.Parameters.AddWithValue("$parse_status", file.ParseStatus);
        command.Parameters.AddWithValue("$diagnostic", (object?)file.Diagnostic ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void InsertFile(IndexedFileRecord file, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO files (
                id, kind, path, content_hash, size_bytes, modified_utc_ticks,
                workspace_identity, parse_status, diagnostic)
            VALUES ($id, $kind, $path, $content_hash, $size_bytes, $modified_utc_ticks,
                    $workspace_identity, $parse_status, $diagnostic);
            """;
        command.Parameters.AddWithValue("$id", file.Id);
        command.Parameters.AddWithValue("$kind", file.Kind);
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$content_hash", file.ContentHash);
        command.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
        command.Parameters.AddWithValue("$modified_utc_ticks", file.ModifiedUtcTicks);
        command.Parameters.AddWithValue("$workspace_identity", file.WorkspaceIdentity);
        command.Parameters.AddWithValue("$parse_status", file.ParseStatus);
        command.Parameters.AddWithValue("$diagnostic", (object?)file.Diagnostic ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void InsertEntity(EntityRecord entity, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entities (id, kind, identity_key, file_id, line, payload_json)
            VALUES ($id, $kind, $identity_key, $file_id, $line, $payload_json);
            """;
        command.Parameters.AddWithValue("$id", entity.Id);
        command.Parameters.AddWithValue("$kind", entity.Kind);
        command.Parameters.AddWithValue("$identity_key", entity.IdentityKey);
        command.Parameters.AddWithValue("$file_id", (object?)entity.FileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$line", (object?)entity.Line ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_json", entity.PayloadJson);
        command.ExecuteNonQuery();
    }

    private void InsertRelation(RelationRecord relation, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO relations (id, from_id, to_id, kind, file_id, line, payload_json)
            VALUES ($id, $from_id, $to_id, $kind, $file_id, $line, $payload_json);
            """;
        command.Parameters.AddWithValue("$id", relation.Id);
        command.Parameters.AddWithValue("$from_id", relation.FromId);
        command.Parameters.AddWithValue("$to_id", (object?)relation.ToId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", relation.Kind);
        command.Parameters.AddWithValue("$file_id", (object?)relation.FileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$line", (object?)relation.Line ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_json", relation.PayloadJson);
        command.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void EnsureWritable()
    {
        EnsureNotDisposed();
        if (!writable)
        {
            throw ErrorFactory.StoreFailed("The index was opened read-only.");
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
