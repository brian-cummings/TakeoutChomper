using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using TakeoutChomper.Shared;
using IOFile = System.IO.File;
using IODirectory = System.IO.Directory;
using IOPath = System.IO.Path;
using IOFileInfo = System.IO.FileInfo;

var paths = PathLayout.CreateFromEnvironment();
paths.EnsureDirectories();

await using var state = new StateStore(paths.DatabasePath);
await state.InitializeAsync();
await state.ResetProcessingToDownloadedAsync();
await state.ReconcileExistingDownloadsAsync(paths.DownloadsPath);

Console.WriteLine("TakeoutChomper Processor starting...");
Console.WriteLine($"Watching downloads folder: {paths.DownloadsPath}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("Cancellation requested. Finishing current work...");
};

try
{
    while (!cts.IsCancellationRequested)
    {
        var snapshot = await state.GetZipStatusesAsync();
        var totalCount = snapshot.Count;
        var completedCount = snapshot.Values.Count(v => v == "done");

        var work = await state.GetNextZipToProcessAsync(cts.Token);
        if (work is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            continue;
        }

        var currentIndex = completedCount + 1;
        Console.WriteLine($"Processing {currentIndex} of {totalCount} ({Math.Round((double)currentIndex / Math.Max(1, totalCount) * 100)}%) : {work.ZipName}");
        await ProcessZipAsync(work.ZipName, paths, state, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Graceful shutdown.
}
finally
{
    await PrintFinalSummaryAsync(state, paths);
}

static async Task ProcessZipAsync(string zipName, PathLayout paths, StateStore state, CancellationToken cancellationToken)
{
    var zipPath = await ResolveZipPathAsync(zipName, paths, state);
    Console.WriteLine($"Processing {zipName}...");

    if (zipPath is null || !File.Exists(zipPath))
    {
        await state.MarkFailedAsync(zipName, "Zip missing from downloads folder.");
        return;
    }

    await state.MarkProcessingAsync(zipName);

    try
    {
        ResetTempDirectory(paths.TempPath);
        ZipFile.ExtractToDirectory(zipPath, paths.TempPath, overwriteFiles: true);

        foreach (var videoFile in EnumerateVideoFiles(paths.TempPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HandleVideoAsync(videoFile, paths, state);
        }

        if (await DeleteWithRetryAsync(zipPath, cancellationToken))
        {
            await state.MarkDoneAsync(zipName);
            Console.WriteLine($"Completed {zipName}");
        }
        else
        {
            await state.MarkFailedAsync(zipName, "Processed but could not delete zip file.");
            Console.WriteLine($"Processed {zipName} but failed to delete it; status set to failed for retry.");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"Cancelled while processing {zipName}");
        throw;
    }
    catch (Exception ex)
    {
        await state.MarkFailedAsync(zipName, ex.Message);
        Console.WriteLine($"Failed {zipName}: {ex.Message}");
    }
    finally
    {
        TryClearTempDirectory(paths.TempPath);
    }
}

static async Task HandleVideoAsync(string filePath, PathLayout paths, StateStore state)
{
    var hash = await ComputeSha256Async(filePath);
    var extension = Path.GetExtension(filePath);
    var originalName = Path.GetFileName(filePath)?.Trim();
    var safeName = BuildSafeName(originalName, hash, extension);
    var destinationPath = Path.Combine(paths.VideosPath, safeName);
    if (string.IsNullOrWhiteSpace(originalName))
    {
        originalName = null;
    }
    var originalCreatedAt = GetOriginalCreatedUtc(filePath);

    IODirectory.CreateDirectory(IOPath.GetDirectoryName(destinationPath)!);

    var videoKnown = await state.IsVideoKnownAsync(hash);
    if (File.Exists(destinationPath))
    {
        if (!videoKnown)
        {
            var existingSize = new FileInfo(destinationPath).Length;
            await state.RecordVideoAsync(hash, destinationPath, existingSize, originalName, originalCreatedAt);
        }

        Console.WriteLine($"Already have {safeName}");
        return;
    }

    File.Move(filePath, destinationPath);
    var sizeBytes = new FileInfo(destinationPath).Length;
    await state.RecordVideoAsync(hash, destinationPath, sizeBytes, originalName, originalCreatedAt);
    Console.WriteLine($"Stored {safeName}");
}

static IEnumerable<string> EnumerateVideoFiles(string root)
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".3gp", ".mts"
    };

    return IODirectory.EnumerateFiles(root, "*.*", System.IO.SearchOption.AllDirectories)
        .Where(path => allowed.Contains(IOPath.GetExtension(path)));
}

static void ResetTempDirectory(string tempPath)
{
    if (IODirectory.Exists(tempPath))
    {
        IODirectory.Delete(tempPath, recursive: true);
    }

    IODirectory.CreateDirectory(tempPath);
}

static void TryClearTempDirectory(string tempPath)
{
    try
    {
        ResetTempDirectory(tempPath);
    }
    catch
    {
        // Best-effort cleanup.
    }
}

static async Task<string> ComputeSha256Async(string filePath)
{
    await using var stream = File.OpenRead(filePath);
    using var sha = SHA256.Create();
    var hash = await sha.ComputeHashAsync(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static async Task PrintFinalSummaryAsync(StateStore state, PathLayout paths)
{
    var statuses = await state.GetZipStatusesAsync();
    var grouped = statuses
        .GroupBy(kv => kv.Value)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    var orderedStatuses = new[] { "pending", "downloading", "downloaded", "processing", "done", "failed" };
    Console.WriteLine("Final status summary:");
    foreach (var status in orderedStatuses)
    {
        if (grouped.TryGetValue(status, out var count))
        {
            Console.WriteLine($"  {status}: {count}");
        }
    }

    var onDisk = IODirectory.Exists(paths.DownloadsPath)
        ? IODirectory.EnumerateFiles(paths.DownloadsPath, "*.zip", System.IO.SearchOption.TopDirectoryOnly).Count()
        : 0;
    Console.WriteLine($"  zips on disk: {onDisk}");
}

static async Task<string?> ResolveZipPathAsync(string zipName, PathLayout paths, StateStore state)
{
    var primaryPath = IOPath.Combine(paths.DownloadsPath, zipName);
    if (IOFile.Exists(primaryPath))
    {
        return primaryPath;
    }

    var meta = await state.GetZipMetadataAsync(zipName);
    if (!string.IsNullOrWhiteSpace(meta?.LastSuggestedName))
    {
        var suggestedPath = IOPath.Combine(paths.DownloadsPath, meta.LastSuggestedName!);
        if (IOFile.Exists(suggestedPath))
        {
            return suggestedPath;
        }
    }

    if (!string.IsNullOrWhiteSpace(meta?.ManifestHash))
    {
        foreach (var file in IODirectory.EnumerateFiles(paths.DownloadsPath, "*.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var manifest = ZipManifest.ComputeManifestHash(file);
                if (string.Equals(manifest, meta.ManifestHash, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch
            {
                // Ignore hash failures on individual files.
            }
        }
    }

    return null;
}

static string BuildSafeName(string? originalName, string hash, string extension)
{
    var baseName = string.IsNullOrWhiteSpace(originalName)
        ? "video"
        : SanitizeFileName(IOPath.GetFileNameWithoutExtension(originalName));

    if (string.IsNullOrWhiteSpace(baseName))
    {
        baseName = "video";
    }

    var ext = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.ToLowerInvariant();
    var suffix = hash.Length >= 8 ? hash[..8] : hash;
    return $"{baseName}-{suffix}{ext}";
}

static string SanitizeFileName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    return cleaned.Length > 100 ? cleaned[..100] : cleaned;
}

static DateTime? GetOriginalCreatedUtc(string filePath)
{
    var candidates = new List<DateTime>();

    static void AddIfValid(List<DateTime> collector, DateTime? dt)
    {
        if (!dt.HasValue) return;
        var normalized = dt.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Local)
            : dt.Value;
        collector.Add(normalized.ToUniversalTime());
    }

    try
    {
        var metadata = ImageMetadataReader.ReadMetadata(filePath);

        foreach (var exif in metadata.OfType<ExifSubIfdDirectory>())
        {
            AddIfValid(candidates, SafeExifDate(exif, ExifDirectoryBase.TagDateTimeOriginal));
            AddIfValid(candidates, SafeExifDate(exif, ExifDirectoryBase.TagDateTimeDigitized));
        }

        foreach (var qt in metadata.OfType<QuickTimeMovieHeaderDirectory>())
        {
            AddIfValid(candidates, SafeQuickTimeMovieDate(qt, QuickTimeMovieHeaderDirectory.TagCreated));
        }

        foreach (var qt in metadata.OfType<QuickTimeTrackHeaderDirectory>())
        {
            AddIfValid(candidates, SafeQuickTimeTrackDate(qt, QuickTimeTrackHeaderDirectory.TagCreated));
        }
    }
    catch
    {
        // Ignore metadata errors; fall back to filesystem times.
    }

    try
    {
        var info = new IOFileInfo(filePath);
        AddIfValid(candidates, info.CreationTimeUtc);
        AddIfValid(candidates, info.LastWriteTimeUtc);
    }
    catch
    {
        // Ignore filesystem errors.
    }

    var valid = candidates.Where(dt => dt > DateTime.MinValue.AddYears(1)).ToList();
    if (valid.Count == 0)
    {
        return null;
    }

    return valid.Min();
}

static DateTime? SafeExifDate(ExifSubIfdDirectory dir, int tag)
{
    try { return dir.GetDateTime(tag); } catch { return null; }
}

static DateTime? SafeQuickTimeMovieDate(QuickTimeMovieHeaderDirectory dir, int tag)
{
    try { return dir.GetDateTime(tag); } catch { return null; }
}

static DateTime? SafeQuickTimeTrackDate(QuickTimeTrackHeaderDirectory dir, int tag)
{
    try { return dir.GetDateTime(tag); } catch { return null; }
}

static async Task<bool> DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
{
    const int attempts = 3;
    for (var i = 1; i <= attempts; i++)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!File.Exists(path))
            {
                return true;
            }
        }
        catch (IOException)
        {
            // Retry
        }
        catch (UnauthorizedAccessException)
        {
            // Retry
        }

        await Task.Delay(TimeSpan.FromSeconds(2 * i), cancellationToken);
    }

    return !File.Exists(path);
}
