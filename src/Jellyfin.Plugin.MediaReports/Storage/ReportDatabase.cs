using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaReports.Storage;

/// <summary>
/// The plugin's own SQLite file. Plugins can't add tables to the server's database, so
/// reports, issues and their history live here, under the server's data path.
/// </summary>
public sealed class ReportDatabase
{
    /// <summary>
    /// The schema version this build writes.
    /// </summary>
    public const int SchemaVersion = 4;

    /// <summary>
    /// The schema changes in order; entry <c>n</c> moves a database from version <c>n</c> to <c>n + 1</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> Migrations =
    [
        // 1: reports, the issues they are grouped into, and the event log.
        """
        CREATE TABLE issue (
            id INTEGER PRIMARY KEY,
            item_id TEXT NOT NULL,
            reason_code TEXT NOT NULL,
            status TEXT NOT NULL,
            admin_notes TEXT,
            resolution_text TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            closed_at TEXT
        );
        CREATE INDEX ix_issue_item ON issue (item_id, reason_code);
        CREATE INDEX ix_issue_closed ON issue (closed_at);
        CREATE TABLE report (
            id INTEGER PRIMARY KEY,
            created_at TEXT NOT NULL,
            reporter_user_id TEXT,
            reporter_name TEXT,
            item_id TEXT NOT NULL,
            item_type TEXT NOT NULL,
            item_name TEXT NOT NULL,
            provider_ids TEXT NOT NULL DEFAULT '{}',
            series_name TEXT,
            season_number INTEGER,
            episode_number INTEGER,
            file_path TEXT,
            reason_code TEXT NOT NULL,
            note TEXT,
            issue_id INTEGER NOT NULL REFERENCES issue (id),
            snapshot TEXT,
            play_method TEXT,
            transcode_reasons TEXT,
            client TEXT,
            device TEXT,
            reporter_seen_at TEXT
        );
        CREATE INDEX ix_report_issue ON report (issue_id, created_at);
        CREATE INDEX ix_report_reporter ON report (reporter_user_id);
        CREATE TABLE event (
            id INTEGER PRIMARY KEY,
            at TEXT NOT NULL,
            issue_id INTEGER,
            report_id INTEGER,
            actor_user_id TEXT,
            kind TEXT NOT NULL,
            detail TEXT
        );
        CREATE INDEX ix_event_issue ON event (issue_id);
        """,

        // 2: private notes only, one open issue per title and reason, what re-linking needs,
        // and when each admin last looked.
        """
        ALTER TABLE issue DROP COLUMN resolution_text;
        ALTER TABLE issue ADD COLUMN checked_file_size INTEGER;
        ALTER TABLE issue ADD COLUMN checked_file_modified TEXT;
        CREATE UNIQUE INDEX ux_issue_open ON issue (item_id, reason_code) WHERE closed_at IS NULL;
        ALTER TABLE report ADD COLUMN series_id TEXT;
        ALTER TABLE report ADD COLUMN series_provider_ids TEXT NOT NULL DEFAULT '{}';
        ALTER TABLE report ADD COLUMN file_size INTEGER;
        ALTER TABLE report ADD COLUMN file_modified TEXT;
        CREATE INDEX ix_report_item ON report (item_id);
        CREATE TABLE admin_seen (
            user_id TEXT PRIMARY KEY,
            seen_at TEXT NOT NULL
        );
        """,

        // 3: when a reporter removed a closed report from their list.
        """
        ALTER TABLE report ADD COLUMN reporter_hidden_at TEXT;
        """,

        // 4: where the Report button last loaded and web playback last started, for the
        // stopped-loading warning, and the few values the plugin keeps across restarts.
        """
        CREATE TABLE client_seen (
            client TEXT PRIMARY KEY,
            version TEXT,
            first_seen TEXT NOT NULL,
            last_load TEXT,
            last_play TEXT
        );
        CREATE TABLE plugin_state (
            key TEXT PRIMARY KEY,
            value TEXT
        );
        """,
    ];

    private readonly ILogger<ReportDatabase> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportDatabase"/> class at the server's data path.
    /// </summary>
    /// <param name="paths">The server's application paths.</param>
    /// <param name="logger">The logger.</param>
    public ReportDatabase(IApplicationPaths paths, ILogger<ReportDatabase> logger)
        : this(Path.Combine(paths.DataPath, "media-reports", "media-reports.db"), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportDatabase"/> class.
    /// </summary>
    /// <param name="filePath">The database file.</param>
    /// <param name="logger">The logger.</param>
    public ReportDatabase(string filePath, ILogger<ReportDatabase> logger)
    {
        FilePath = filePath;
        _logger = logger;
    }

    /// <summary>
    /// Gets the database file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the daily consistent copy, beside the database.
    /// </summary>
    public string CopyPath => Path.Combine(Path.GetDirectoryName(FilePath)!, "media-reports.backup.db");

    /// <summary>
    /// Gets a value indicating whether the database opened at the current schema version.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Creates the file if needed and brings it to the current schema version. A database
    /// newer than this build is left untouched and storage stays unavailable.
    /// </summary>
    public void Initialize()
    {
        IsAvailable = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath))
            {
                // Read the version without writing, so a newer file is never changed.
                using var check = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadOnly));
                check.Open();
                var found = ReadVersion(check);
                if (found > SchemaVersion)
                {
                    _logger.LogError(
                        "The Media Reports database at {Path} is at schema version {Found}, newer than this plugin understands ({Supported}). It was left untouched and reports are unavailable; install a newer Media Reports.",
                        FilePath,
                        found,
                        SchemaVersion);
                    return;
                }
            }

            using var connection = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadWriteCreate));
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL;");
            var version = ReadVersion(connection);
            for (var next = (int)version; next < SchemaVersion; next++)
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = Migrations[next] + $"\nPRAGMA user_version = {next + 1};";
                command.ExecuteNonQuery();
                transaction.Commit();
                _logger.LogInformation("Media Reports database migrated to schema version {Version}", next + 1);
            }

            IsAvailable = true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The Media Reports database at {Path} could not be opened; reports are unavailable", FilePath);
        }
    }

    /// <summary>
    /// Opens a read-write connection.
    /// </summary>
    /// <returns>The open connection.</returns>
    /// <exception cref="StorageUnavailableException">The database did not open at startup.</exception>
    public SqliteConnection OpenConnection()
    {
        if (!IsAvailable)
        {
            throw new StorageUnavailableException();
        }

        var connection = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadWrite));
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;");
        return connection;
    }

    private string ConnectionString(SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = FilePath,
        Mode = mode,
        Pooling = false,
    }.ToString();

    private static long ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Thrown when the database did not open at startup.
/// </summary>
public sealed class StorageUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StorageUnavailableException"/> class.
    /// </summary>
    public StorageUnavailableException()
        : base("The Media Reports database is unavailable.")
    {
    }
}
