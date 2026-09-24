using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public class DiskCacheData
{
    public string Path { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public long TotalSize { get; set; }
    public List<DiskItemModel> Slices { get; set; } = new();
    public List<DiskItemModel> Level1 { get; set; } = new();
}

public static class DiskCacheService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeroEngineering", "HeroTweaker", "DiskCache");

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeroEngineering", "HeroTweaker", "cache_config.json");

    public static bool IsCachePermissionAsked { get; private set; }
    public static bool IsCacheEnabled { get; private set; }

    static DiskCacheService()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Asked", out var asked))
                    IsCachePermissionAsked = asked.GetBoolean();
                if (doc.RootElement.TryGetProperty("Enabled", out var enabled))
                    IsCacheEnabled = enabled.GetBoolean();
            }
        }
        catch { }
    }

    public static void SavePermission(bool enabled)
    {
        try
        {
            IsCachePermissionAsked = true;
            IsCacheEnabled = enabled;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var json = JsonSerializer.Serialize(new { Asked = true, Enabled = enabled });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    private static string GetCacheFilePath(string path)
    {
        using var md5 = MD5.Create();
        var hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
        return Path.Combine(CacheDir, $"{hash}.json");
    }

    public static async Task SaveSnapshotAsync(string path, List<DiskItemModel> slices, List<DiskItemModel> level1, long totalSize)
    {
        if (!IsCacheEnabled) return;

        try
        {
            var data = new DiskCacheData
            {
                Path = path,
                Timestamp = DateTime.UtcNow,
                TotalSize = totalSize,
                Slices = slices,
                Level1 = level1
            };

            string file = GetCacheFilePath(path);
            var json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(file, json);
        }
        catch { }
    }

    public static async Task<(List<DiskItemModel> Slices, List<DiskItemModel> Level1, long TotalSize)?> LoadSnapshotAsync(string path)
    {
        if (!IsCacheEnabled) return null;

        try
        {
            string file = GetCacheFilePath(path);
            if (!File.Exists(file)) return null;

            // Кэш актуален 2 часа
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(2))
                return null;

            var json = await File.ReadAllTextAsync(file);
            var data = JsonSerializer.Deserialize<DiskCacheData>(json);
            if (data != null && data.Slices.Count > 0)
                return (data.Slices, data.Level1, data.TotalSize);
        }
        catch { }

        return null;
    }

    public static long GetTotalCacheSize()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return 0;
            long size = 0;
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.json"))
            {
                try { size += new FileInfo(f).Length; } catch { }
            }
            return size;
        }
        catch { return 0; }
    }

    public static void ClearAllCache()
    {
        try
        {
            if (Directory.Exists(CacheDir))
            {
                foreach (var f in Directory.EnumerateFiles(CacheDir))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }
}