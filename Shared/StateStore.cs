using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TakeoutChomper.Shared;

internal sealed class StateStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public StateStore(string databasePath)
    {
        _dbPath = databasePath;
        _connectionString = $"Data Source={databasePath};Cache=Shared";
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS zips (
                zip_name TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                size_bytes INTEGER,
                started_at DATETIME,
                completed_at DATETIME,
                error TEXT
            );
            CREATE TABLE IF NOT EXISTS videos (
                sha256 TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                first_seen DATETIME NOT NULL,
                size_bytes INTEGER,
                original_name TEXT,
                original_created_at DATETIME
            );
            CREATE INDEX IF NOT EXISTS idx_zips_status ON zips(status);
            CREATE INDEX IF NOT EXISTS idx_videos_file_path ON videos(file_path);";
        await command.ExecuteNonQueryAsync();

        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE videos ADD COLUMN original_name TEXT";
            await alter.ExecuteNonQueryAsync();
        }
        catch (SqliteException)
        {
            // Column likely exists; ignore.
        }

        try
        {
            await using var alter2 = connection.CreateCommand();
            alter2.CommandText = "ALTER TABLE videos ADD COLUMN original_created_at DATETIME";
            await alter2.ExecuteNonQueryAsync();
        }
        catch (SqliteException)
        {
            // Column likely exists; ignore.
        }
    }

    public async Task ResetProcessingToDownloadedAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE zips SET status = 'downloaded', started_at = NULL, completed_at = NULL WHERE status = 'processing'";
        await command.ExecuteNonQueryAsync();
    }

    public async Task MarkDiscoveredAsync(string zipName)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO zips(zip_name, status) VALUES ($name, 'pending') ON CONFLICT(zip_name) DO NOTHING";
        command.Parameters.AddWithValue("$name", zipName);
        await command.ExecuteNonQueryAsync();
    }

    public async Task MarkDownloadingAsync(string zipName, long? sizeBytes)
    {
        await UpsertZip(zipName, "downloading", sizeBytes, startedAt: null, completedAt: null, error: null);
    }

    public async Task MarkDownloadedAsync(string zipName, long? sizeBytes)
    {
        await UpsertZip(zipName, "downloaded", sizeBytes, startedAt: null, completedAt: null, error: null);
    }

    public async Task MarkProcessingAsync(string zipName)
    {
        await UpsertZip(zipName, "processing", null, DateTime.UtcNow, null, null);
    }

    public async Task MarkDoneAsync(string zipName)
    {
        await UpsertZip(zipName, "done", null, null, DateTime.UtcNow, null);
    }

    public async Task MarkFailedAsync(string zipName, string? error)
    {
        await UpsertZip(zipName, "failed", null, null, null, error ?? "unknown error");
    }

    public async Task<string?> GetZipStatusAsync(string zipName)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM zips WHERE zip_name = $name";
        command.Parameters.AddWithValue("$name", zipName);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task<bool> ShouldSkipDownloadAsync(string zipName, string downloadsPath, string? altName = null)
    {
        var namesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { zipName };
        if (!string.IsNullOrWhiteSpace(altName))
        {
            namesToCheck.Add(altName);
        }

        foreach (var name in namesToCheck)
        {
            var status = await GetZipStatusAsync(name);
            if (status is "downloading" or "downloaded" or "processing" or "done")
            {
                return true;
            }
        }

        var onDisk = namesToCheck.Any(name => File.Exists(Path.Combine(downloadsPath, name)));
        return onDisk;
    }


    public async Task ReconcileExistingDownloadsAsync(string downloadsPath)
    {
        foreach (var file in Directory.EnumerateFiles(downloadsPath, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var size = new FileInfo(file).Length;
            await MarkDownloadedAsync(name, size);
        }
    }

    public async Task<ZipWork?> GetNextZipToProcessAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT zip_name, size_bytes FROM zips WHERE status = 'downloaded' ORDER BY (started_at IS NOT NULL), zip_name LIMIT 1";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var zipName = reader.GetString(0);
        var size = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        return new ZipWork(zipName, size);
    }

    public async Task<bool> IsVideoKnownAsync(string hash)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM videos WHERE sha256 = $hash LIMIT 1";
        command.Parameters.AddWithValue("$hash", hash);
        var result = await command.ExecuteScalarAsync();
        return result is not null;
    }

    public async Task<bool> RecordVideoAsync(string hash, string filePath, long sizeBytes, string? originalName, DateTime? originalCreatedAt)
    {
        var trimmedOriginal = string.IsNullOrWhiteSpace(originalName) ? null : originalName.Trim();

        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO videos (sha256, file_path, first_seen, size_bytes, original_name, original_created_at)
            VALUES ($hash, $path, $firstSeen, $size, $original, $created)
            ON CONFLICT(sha256) DO UPDATE SET
                file_path = excluded.file_path,
                size_bytes = excluded.size_bytes,
                original_name = CASE
                    WHEN excluded.original_name IS NOT NULL AND (videos.original_name IS NULL OR length(excluded.original_name) < length(videos.original_name))
                        THEN excluded.original_name
                    ELSE videos.original_name
                END,
                original_created_at = CASE
                    WHEN excluded.original_created_at IS NOT NULL AND (videos.original_created_at IS NULL OR excluded.original_created_at < videos.original_created_at)
                        THEN excluded.original_created_at
                    ELSE videos.original_created_at
                END";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$firstSeen", DateTime.UtcNow);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$original", (object?)trimmedOriginal ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", originalCreatedAt.HasValue ? originalCreatedAt.Value : (object)DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<IDictionary<string, string>> GetZipStatusesAsync()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT zip_name, status FROM zips";

        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task UpsertZip(string zipName, string status, long? sizeBytes, DateTime? startedAt, DateTime? completedAt, string? error)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO zips(zip_name, status, size_bytes, started_at, completed_at, error)
            VALUES ($name, $status, $size, $started, $completed, $error)
            ON CONFLICT(zip_name) DO UPDATE SET
                status = excluded.status,
                size_bytes = COALESCE(excluded.size_bytes, zips.size_bytes),
                started_at = COALESCE(excluded.started_at, zips.started_at),
                completed_at = excluded.completed_at,
                error = excluded.error";
        command.Parameters.AddWithValue("$name", zipName);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$size", sizeBytes.HasValue ? sizeBytes.Value : DBNull.Value);
        command.Parameters.AddWithValue("$started", startedAt.HasValue ? startedAt.Value : DBNull.Value);
        command.Parameters.AddWithValue("$completed", completedAt.HasValue ? completedAt.Value : DBNull.Value);
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }
}

internal record ZipWork(string ZipName, long? SizeBytes);
