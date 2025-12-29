using System;
using System.IO;

namespace TakeoutChomper.Shared;

internal sealed class PathLayout
{
    private const string DataRootEnvVar = "TAKEOUTCHOMPER_DATA";

    private PathLayout(string dataRoot)
    {
        DataRoot = dataRoot;
        DownloadsPath = Path.Combine(dataRoot, "downloads");
        TempPath = Path.Combine(dataRoot, "temp");
        VideosPath = Path.Combine(dataRoot, "videos");
        DatabasePath = Path.Combine(dataRoot, "state.db");
    }

    public string DataRoot { get; }
    public string DownloadsPath { get; }
    public string TempPath { get; }
    public string VideosPath { get; }
    public string DatabasePath { get; }

    public static PathLayout CreateFromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable(DataRootEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new PathLayout(Path.GetFullPath(env));
        }

        var downloadsHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "TakeoutChomperDownloads");
        return new PathLayout(Path.GetFullPath(downloadsHome));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(DownloadsPath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(VideosPath);
    }
}
