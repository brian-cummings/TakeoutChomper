using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using TakeoutChomper.Shared;

var paths = PathLayout.CreateFromEnvironment();
paths.EnsureDirectories();
CleanupPartialDownloads(paths.DownloadsPath);

await using var state = new StateStore(paths.DatabasePath);
await state.InitializeAsync();
await state.ReconcileExistingDownloadsAsync(paths.DownloadsPath);

var userDataDir = ResolveUserDataDir();
var targetUrl = Environment.GetEnvironmentVariable("TAKEOUTCHOMPER_URL") ??
                "https://takeout.google.com/settings/takeout/downloads";
Console.WriteLine($"Using Chrome profile at: {userDataDir}");
Console.WriteLine($"Downloads will be saved to: {paths.DownloadsPath}");
Console.WriteLine($"Target page: {targetUrl}");

Console.WriteLine("Starting Playwright and launching Chrome...");
using var playwright = await Playwright.CreateAsync();
IBrowserContext? browser = null;

try
{
    browser = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, new BrowserTypeLaunchPersistentContextOptions
    {
        Headless = false,
        Channel = "chrome",
        AcceptDownloads = true,
        DownloadsPath = paths.DownloadsPath,
        Timeout = 120_000,
        IgnoreDefaultArgs = new[] { "--enable-automation" },
        Args = new[]
        {
            "--disable-blink-features=AutomationControlled"
        }
    });
    Console.WriteLine("Chrome context created.");
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to launch Chrome context: {ex}");
    return;
}

Console.WriteLine("Chrome launched. Preparing page...");
var page = browser.Pages.FirstOrDefault() ?? await browser.NewPageAsync();
using var progressCts = new CancellationTokenSource();
var progressTask = TrackDownloadProgressAsync(paths.DownloadsPath, progressCts.Token);
page.Request += (_, req) =>
{
    if (req.IsNavigationRequest)
    {
        Console.WriteLine($"[page request] {req.Method} {req.Url}");
    }
};
page.Response += (_, resp) =>
{
    if (resp.Request.IsNavigationRequest)
    {
        Console.WriteLine($"[page response] {resp.Status} {resp.Url}");
    }
};
page.Console += (_, msg) => Console.WriteLine($"[page console] {msg.Text}");
page.PageError += (_, err) => Console.WriteLine($"[page error] {err}");
await page.BringToFrontAsync();
await RunNavigationProbeAsync(page, targetUrl);

if (IsOnAccountsLogin(page.Url))
{
    Console.WriteLine("Detected Google sign-in page. Please complete login in the opened Chrome window, then re-run the downloader.");
    progressCts.Cancel();
    await progressTask;
    await browser.CloseAsync();
    return;
}

await NavigateToArchiveDetailIfNeeded(page);

Console.WriteLine("Scanning for downloads...");
var attemptCounter = 0;
var idleCycles = 0;
const int maxIdleCycles = 10;
var targetJob = Environment.GetEnvironmentVariable("TAKEOUTCHOMPER_JOB_FILTER");

while (idleCycles < maxIdleCycles)
{
    var downloadButtons = await FindDownloadButtonsAsync(page, targetJob);
    if (downloadButtons.Count == 0)
    {
        idleCycles++;
        Console.WriteLine($"No Download buttons yet (idle {idleCycles}/{maxIdleCycles}). URL: {page.Url}");
        await page.WaitForTimeoutAsync(3000);
        continue;
    }

    idleCycles = 0;
    Console.WriteLine($"Found {downloadButtons.Count} download candidates.");

    foreach (var candidate in downloadButtons)
    {
        await state.MarkDiscoveredAsync(candidate.PlaceholderName);
    }

    var startedThisPass = 0;
    foreach (var candidate in downloadButtons)
    {
        // Skip upfront if we already have it on disk or recorded.
        if (await state.ShouldSkipDownloadAsync(candidate.PlaceholderName, paths.DownloadsPath))
        {
            Console.WriteLine($"[skip] {candidate.PlaceholderName} already downloaded/recorded.");
            continue;
        }

        var attemptNumber = Interlocked.Increment(ref attemptCounter);
        var result = await TryDownloadAsync(page, candidate, attemptNumber);
        startedThisPass++;
        if (!result)
        {
            // Likely hit login or failure; stop further clicks this pass.
            break;
        }
    }

    if (startedThisPass == 0)
    {
        // Nothing to do; avoid looping forever.
        break;
    }

    // After clicking, wait a bit for the page to update (e.g., buttons disable).
    await page.WaitForTimeoutAsync(2000);
}

Console.WriteLine("Download sweep finished. Close the browser to exit.");
progressCts.Cancel();
await progressTask;
await browser.CloseAsync();

async Task<bool> TryDownloadAsync(IPage page, DownloadCandidate candidate, int attemptNumber)
{
    try
    {
        await candidate.Locator.ScrollIntoViewIfNeededAsync();
        Console.WriteLine($"[{attemptNumber}] Triggering download via {candidate.PlaceholderName}...");

        var download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            await candidate.Locator.ClickAsync(new LocatorClickOptions { Timeout = 120_000 });
        }, new PageRunAndWaitForDownloadOptions { Timeout = 600_000 });

        // Stick to the placeholder name for tracking, but preserve Google's suggested filename on disk.
        var zipName = candidate.PlaceholderName;
        var targetFileName = download.SuggestedFilename ?? zipName;

        await state.MarkDiscoveredAsync(zipName);
        if (await state.ShouldSkipDownloadAsync(zipName, paths.DownloadsPath, candidate.PlaceholderName))
        {
            Console.WriteLine($"[{attemptNumber}] Skipping {zipName} because it already exists or is recorded.");
            try
            {
                await download.DeleteAsync();
            }
            catch (PlaywrightException)
            {
                // Swallow; delete is best-effort.
            }

            return true;
        }

        await state.MarkDownloadingAsync(zipName, null, download.SuggestedFilename);
        var destination = Path.Combine(paths.DownloadsPath, targetFileName);
        await download.SaveAsAsync(destination);

        if (!File.Exists(destination))
        {
            Console.WriteLine($"[{attemptNumber}] Download file missing after save: {destination}");
            return false;
        }

        var size = new FileInfo(destination).Length;
        string? manifestHash = null;
        try
        {
            manifestHash = ZipManifest.ComputeManifestHash(destination);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{attemptNumber}] Unable to compute manifest hash for {zipName}: {ex.Message}");
        }

        await state.MarkDownloadedAsync(zipName, size, download.SuggestedFilename, manifestHash);
        Console.WriteLine($"[{attemptNumber}] Saved {zipName} ({size} bytes){(manifestHash is null ? string.Empty : $" manifest={manifestHash}")}");
        return true;
    }
    catch (TimeoutException ex)
    {
        Console.WriteLine($"[{attemptNumber}] Timeout waiting for download: {ex.Message}");
        if (IsOnAccountsLogin(page.Url))
        {
            Console.WriteLine("Detected Google sign-in page after clicking. Please complete login in the browser, then re-run the downloader.");
            return false;
        }
    }
    catch (PlaywrightException ex)
    {
        Console.WriteLine($"[{attemptNumber}] Playwright error: {ex.Message}");
    }
    catch (SqliteException ex)
    {
        Console.WriteLine($"[{attemptNumber}] Database error while tracking download: {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{attemptNumber}] Unexpected error: {ex}");
    }

    return false;
}

static async Task<IReadOnlyList<DownloadCandidate>> FindDownloadButtonsAsync(IPage page, string? jobFilter)
{
    // Narrow to archive detail downloads (not the archive list).
    var hrefLocs = await page.Locator("a[href*=\"takeout.google.com/takeout/download\" i][href*=\"/takeout/download\" i]").ElementHandlesAsync();
    var hrefMap = new List<DownloadCandidate>();
    var fallbackIndex = 1;

    foreach (var handle in hrefLocs)
    {
        var href = await handle.GetAttributeAsync("href");
        if (string.IsNullOrWhiteSpace(href))
        {
            continue;
        }

        var placeholder = BuildPlaceholderName(href, fallbackIndex);
        if (!IsAllowedJob(placeholder, jobFilter))
        {
            continue;
        }

        hrefMap.Add(new DownloadCandidate(page.Locator($"a[href=\"{href}\"]"), href, placeholder));
        fallbackIndex++;
    }

    if (hrefMap.Count == 0)
    {
        var regex = new Regex("download", RegexOptions.IgnoreCase);
        var candidates = new List<ILocator>();
        candidates.AddRange(await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = regex }).AllAsync());
        candidates.AddRange(await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = regex }).AllAsync());
        candidates.AddRange(await page.Locator("text=Download").AllAsync());
        candidates.AddRange(await page.Locator("text=Download again").AllAsync());

        foreach (var loc in candidates)
        {
            if (!await loc.IsVisibleAsync())
            {
                continue;
            }

            var href = await loc.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var placeholder = BuildPlaceholderName(href, fallbackIndex);
            if (!IsAllowedJob(placeholder, jobFilter))
            {
                continue;
            }

            hrefMap.Add(new DownloadCandidate(loc, href, placeholder));
            fallbackIndex++;
        }
    }

    Console.WriteLine($"[finder] visible download links: {hrefMap.Count}");
    return hrefMap;
}

static async Task RunNavigationProbeAsync(IPage page, string targetUrl)
{
    var probeUrl = "https://example.com/";
    Console.WriteLine($"Probing navigation with {probeUrl} ...");
    await NavigateAsync(page, probeUrl);

    Console.WriteLine($"Navigating to target {targetUrl} ...");
    await NavigateAsync(page, targetUrl);
    Console.WriteLine($"Current page after navigation: {page.Url}");
}

static async Task NavigateAsync(IPage page, string url)
{
    try
    {
        Console.WriteLine($"Goto start: {url}");
        var response = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 120_000
        });

        Console.WriteLine($"Goto done: {url} (status {(response?.Status ?? 0)})");
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 60_000 });
            Console.WriteLine($"Load state: networkidle reached for {url}");
        }
        catch (PlaywrightException)
        {
            // Network idle might not happen; continue.
            Console.WriteLine($"Load state: networkidle timed out for {url}");
        }
    }
    catch (PlaywrightException ex)
    {
        Console.WriteLine($"Navigation error for {url}: {ex.Message}");
    }
}

static async Task NavigateToArchiveDetailIfNeeded(IPage page)
{
    if (page.Url.Contains("takeout.google.com/manage/archive", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    if (!page.Url.Contains("takeout.google.com/manage", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    Console.WriteLine("On archive list page; looking for an archive detail link...");
    var archiveLink = page.Locator("a[href*=\"/manage/archive/\"]").First;
    if (await archiveLink.IsVisibleAsync())
    {
        var href = await archiveLink.GetAttributeAsync("href");
        if (string.IsNullOrWhiteSpace(href))
        {
            Console.WriteLine("Found archive link but no href; set TAKEOUTCHOMPER_URL to a specific archive detail page.");
            return;
        }

        var absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? href
            : new Uri(new Uri(page.Url), href).ToString();

        Console.WriteLine($"Navigating to archive detail: {absolute}");
        await NavigateAsync(page, absolute);
        Console.WriteLine($"Current page after archive detail navigation: {page.Url}");
    }
    else
    {
        Console.WriteLine("No archive detail link found; set TAKEOUTCHOMPER_URL to a specific archive detail page.");
    }
}

static bool IsOnAccountsLogin(string url) =>
    url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
    && (url.Contains("signin", StringComparison.OrdinalIgnoreCase) || url.Contains("login", StringComparison.OrdinalIgnoreCase));

static void CleanupPartialDownloads(string downloadsPath)
{
    try
    {
        var partials = Directory.EnumerateFiles(downloadsPath, "*.crdownload").ToList();
        foreach (var file in partials)
        {
            try
            {
                File.Delete(file);
                Console.WriteLine($"[cleanup] removed partial download {Path.GetFileName(file)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[cleanup] could not remove {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }
    catch (DirectoryNotFoundException)
    {
        // Downloads folder missing; nothing to clean.
    }
}

static async Task TrackDownloadProgressAsync(string downloadsPath, CancellationToken token)
{
    var lastSizes = new Dictionary<string, (long Size, DateTime Timestamp)>(StringComparer.OrdinalIgnoreCase);
    var lastSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    while (!token.IsCancellationRequested)
    {
        try
        {
            var active = Directory.EnumerateFiles(downloadsPath, "*.crdownload").ToList();
            var activeSet = new HashSet<string>(active.Select(f => Path.GetFileName(f) ?? string.Empty), StringComparer.OrdinalIgnoreCase);

            foreach (var file in active)
            {
                var name = Path.GetFileName(file) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var size = new FileInfo(file).Length;
                var now = DateTime.UtcNow;

                if (lastSizes.TryGetValue(name, out var prev))
                {
                    var deltaBytes = size - prev.Size;
                    var elapsed = now - prev.Timestamp;
                    var deltaSeconds = Math.Max(1, elapsed.TotalSeconds);
                    var speed = deltaBytes > 0 ? $"{FormatBytes((long)(deltaBytes / deltaSeconds))}/s" : "stalled";

                    if (deltaBytes > 0 || elapsed.TotalSeconds >= 10)
                    {
                        Console.WriteLine($"[download] {name} {FormatBytes(size)} ({speed})");
                        lastSizes[name] = (size, now);
                    }
                }
                else
                {
                    Console.WriteLine($"[download] {name} {FormatBytes(size)} (new)");
                    lastSizes[name] = (size, now);
                }
            }

            foreach (var finished in lastSeen.Except(activeSet))
            {
                if (lastSizes.TryGetValue(finished, out var info))
                {
                    Console.WriteLine($"[download] {finished} completed at {FormatBytes(info.Size)}");
                }
                else
                {
                    Console.WriteLine($"[download] {finished} completed");
                }

                lastSizes.Remove(finished);
            }

            lastSeen = activeSet;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[download] progress watcher error: {ex.Message}");
        }

        try
        {
            await Task.Delay(2000, token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

static string FormatBytes(long bytes)
{
    string[] units = { "B", "KB", "MB", "GB" };
    double value = bytes;
    int unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }
    return $"{value:0.##} {units[unit]}";
}

static string ResolveUserDataDir()
{
    var env = Environment.GetEnvironmentVariable("TAKEOUTCHOMPER_PROFILE");
    if (!string.IsNullOrWhiteSpace(env))
    {
        return Path.GetFullPath(env);
    }

    var defaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".takeoutchomper",
        "chrome-profile");

    if (OperatingSystem.IsMacOS())
    {
        return defaultDir;
    }

    if (OperatingSystem.IsWindows())
    {
        return defaultDir;
    }

    return defaultDir;
}

static string BuildPlaceholderName(string? href, int index)
{
    if (!string.IsNullOrWhiteSpace(href))
    {
        try
        {
            var uri = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(href)
                : new Uri(new Uri("https://takeout.google.com"), href);

            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            string? jobId = null;
            string? part = null;
            foreach (var kv in query)
            {
                var pieces = kv.Split('=', 2);
                if (pieces.Length != 2) continue;
                var key = pieces[0];
                var value = Uri.UnescapeDataString(pieces[1]);
                if (key == "j" && !string.IsNullOrWhiteSpace(value)) jobId = value;
                if (key == "i" && !string.IsNullOrWhiteSpace(value)) part = value;
            }

            if (jobId is not null && part is not null && int.TryParse(part, out var partNum))
            {
                return $"takeout-{jobId}-part-{partNum:D3}.zip";
            }
        }
        catch
        {
            // Fallback below.
        }
    }

    return $"takeout-part-{index:D4}.zip";
}

static bool IsAllowedJob(string placeholderName, string? jobFilter)
{
    if (string.IsNullOrWhiteSpace(jobFilter))
    {
        return true;
    }

    if (!placeholderName.StartsWith("takeout-", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var parts = placeholderName.Split('-', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 3)
    {
        return false;
    }

    var jobId = parts[1];
    return jobId.Equals(jobFilter, StringComparison.OrdinalIgnoreCase);
}
