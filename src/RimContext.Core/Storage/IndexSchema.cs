using Microsoft.Data.Sqlite;
using RimContext.Core.Contracts;
using RimContext.Core.Model;

namespace RimContext.Core.Storage;

internal static class IndexSchema
{
    public static void Create(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "PRAGMA user_version = " + IndexConstants.SchemaVersion + ";");
        Execute(connection, transaction, """
            CREATE TABLE meta (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE files (
                id TEXT NOT NULL PRIMARY KEY,
                kind TEXT NOT NULL,
                path TEXT NOT NULL UNIQUE,
                content_hash TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                modified_utc_ticks INTEGER NOT NULL,
                workspace_identity TEXT NOT NULL,
                parse_status TEXT NOT NULL,
                diagnostic TEXT NULL
            );

            CREATE TABLE entities (
                id TEXT NOT NULL PRIMARY KEY,
                kind TEXT NOT NULL,
                identity_key TEXT NOT NULL,
                file_id TEXT NULL,
                line INTEGER NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id)
            );

            CREATE TABLE relations (
                id TEXT NOT NULL PRIMARY KEY,
                from_id TEXT NOT NULL,
                to_id TEXT NULL,
                kind TEXT NOT NULL,
                file_id TEXT NULL,
                line INTEGER NULL,
                payload_json TEXT NOT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id)
            );

            CREATE INDEX idx_files_path ON files(path);
            CREATE INDEX idx_files_kind ON files(kind);
            CREATE INDEX idx_entities_kind ON entities(kind);
            CREATE INDEX idx_entities_file ON entities(file_id);
            CREATE INDEX idx_relations_from ON relations(from_id);
            CREATE INDEX idx_relations_to ON relations(to_id);
            CREATE INDEX idx_relations_kind ON relations(kind);
            """);
    }

    public static void Validate(SqliteConnection connection, int expectedSchemaVersion)
    {
        var actual = ReadUserVersion(connection);
        if (actual != expectedSchemaVersion)
        {
            throw ErrorFactory.IndexIncompatible(
                $"The index schema version is {actual}, but version {expectedSchemaVersion} is required.",
                new { expected = expectedSchemaVersion, actual });
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('meta', 'files', 'entities', 'relations');";
            var tableCount = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (tableCount != 4)
            {
                throw ErrorFactory.IndexIncompatible("The index is missing one or more required tables.");
            }
        }
        catch (RimContextException)
        {
            throw;
        }
        catch (SqliteException ex)
        {
            throw ErrorFactory.IndexIncompatible("The index schema could not be inspected.", new { error = ex.SqliteErrorCode });
        }
    }

    public static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
