using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TakeoutChomper.Shared;

/// <summary>
/// Computes a stable hash that represents the contents of a ZIP without reading file payloads.
/// Uses entry names, uncompressed/compressed lengths, and last write time to avoid expensive hashing of large archives.
/// </summary>
internal static class ZipManifest
{
    public static string? ComputeManifestHash(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return null;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var manifest = archive.Entries
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(FormatEntry)
            .ToArray();

        var joined = string.Join("\n", manifest);
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string FormatEntry(ZipArchiveEntry entry)
    {
        var compressed = entry.CompressedLength;
        var uncompressed = entry.Length;
        var timestamp = entry.LastWriteTime.UtcDateTime.ToString("O");
        return $"{entry.FullName}|{uncompressed}|{compressed}|{timestamp}";
    }
}
