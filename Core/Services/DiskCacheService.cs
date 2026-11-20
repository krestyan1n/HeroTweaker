using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class DiskCacheService
{
    private const uint CacheMagicHeader = 0x48544443; // "HTDC"
    private const byte CacheFormatVersion = 4; // Добавлено время сканирования
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private const int MaxCachedItems = 100_000;
    private const long MaxCacheFileBytes = 64L * 1024 * 1024;

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeroTweaker",
        "DiskTreeCache");

    // L1: Оперативный кэш в RAM (отклик 0 мс)
    private static readonly ConcurrentDictionary<string, (DateTime CachedAt, List<DiskItemModel> Slices, List<DiskItemModel> Level1, long TotalSize)> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsCachePermissionAsked
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\HeroTweaker\Settings");
                return key?.GetValue("DiskCachePermissionAsked") != null;
            }
            catch { return false; }
        }
    }

    public static bool IsCacheEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\HeroTweaker\Settings");
                return (key?.GetValue("DiskCacheEnabled") as int? ?? 1) == 1;
            }
            catch { return true; }
        }
    }

    public static void SavePermission(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\HeroTweaker\Settings");
            key?.SetValue("DiskCachePermissionAsked", 1, RegistryValueKind.DWord);
            key?.SetValue("DiskCacheEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static async Task<(List<DiskItemModel> Slices, List<DiskItemModel> Level1, long TotalSize)?> LoadSnapshotAsync(string path)
    {
        if (!IsCacheEnabled || string.IsNullOrWhiteSpace(path)) return null;

        string normPath = NormalizePath(path);

        // 1. Проверка оперативного кэша L1
        if (MemoryCache.TryGetValue(normPath, out var memEntry))
        {
            var age = DateTime.UtcNow - memEntry.CachedAt;
            if (age >= TimeSpan.Zero && age < CacheLifetime)
            {
                return (memEntry.Slices, memEntry.Level1, memEntry.TotalSize);
            }
            MemoryCache.TryRemove(normPath, out _);
        }

        // 2. Чтение бинарного кэша L2
        string cacheFile = GetCacheFilePath(normPath);
        if (!File.Exists(cacheFile)) return null;

        return await Task.Run<(List<DiskItemModel> Slices, List<DiskItemModel> Level1, long TotalSize)?>(() =>
        {
            try
            {
                using var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                using var br = new BinaryReader(fs);

                if (fs.Length > MaxCacheFileBytes) { TryDelete(cacheFile); return null; }
                if (br.ReadUInt32() != CacheMagicHeader) { TryDelete(cacheFile); return null; }
                if (br.ReadByte() != CacheFormatVersion) { TryDelete(cacheFile); return null; }

                string storedPath = br.ReadString();
                if (!normPath.Equals(storedPath, StringComparison.OrdinalIgnoreCase)) return null;

                var cachedAt = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
                var age = DateTime.UtcNow - cachedAt;
                if (age < TimeSpan.Zero || age >= CacheLifetime) { TryDelete(cacheFile); return null; }

                long totalSize = br.ReadInt64();
                if (totalSize < 0 || fs.Position + sizeof(int) > fs.Length) { TryDelete(cacheFile); return null; }

                int slicesCount = br.ReadInt32();
                if (slicesCount < 0 || slicesCount > MaxCachedItems) { TryDelete(cacheFile); return null; }
                var slices = new List<DiskItemModel>(slicesCount);
                for (int i = 0; i < slicesCount; i++)
                {
                    slices.Add(ReadItem(br));
                }

                int level1Count = br.ReadInt32();
                if (level1Count < 0 || level1Count > MaxCachedItems) { TryDelete(cacheFile); return null; }
                var level1 = new List<DiskItemModel>(level1Count);
                for (int i = 0; i < level1Count; i++)
                {
                    level1.Add(ReadItem(br));
                }
                if (fs.Position != fs.Length) { TryDelete(cacheFile); return null; }

                MemoryCache[normPath] = (cachedAt, slices, level1, totalSize);
                return (slices, level1, totalSize);
            }
            catch
            {
                TryDelete(cacheFile);
                return null;
            }
        });
    }

    public static async Task SaveSnapshotAsync(string path, IEnumerable<DiskItemModel> allSlices, IEnumerable<DiskItemModel> level1Items, long totalSize)
    {
        if (!IsCacheEnabled || string.IsNullOrWhiteSpace(path)) return;

        string normPath = NormalizePath(path);
        var slicesList = allSlices.ToList();
        var level1List = level1Items.ToList();

        MemoryCache[normPath] = (DateTime.UtcNow, slicesList, level1List, totalSize);

        await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                    Directory.CreateDirectory(CacheDirectory);

                string cacheFile = GetCacheFilePath(normPath);
                string tempFile = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    using (var fs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(CacheMagicHeader);
                        bw.Write(CacheFormatVersion);
                        bw.Write(normPath);
                        bw.Write(DateTime.UtcNow.Ticks);
                        bw.Write(totalSize);

                        bw.Write(slicesList.Count);
                        foreach (var s in slicesList) WriteItem(bw, s);

                        bw.Write(level1List.Count);
                        foreach (var l in level1List) WriteItem(bw, l);
                    }

                    File.Move(tempFile, cacheFile, true);
                }
                finally { TryDelete(tempFile); }
            }
            catch { }
        });
    }

    public static void ClearAllCache()
    {
        MemoryCache.Clear();
        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, true);
                Directory.CreateDirectory(CacheDirectory);
            }
        }
        catch { }
    }

    public static long GetTotalCacheSize()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return 0;
            long total = 0;
            foreach (var file in new DirectoryInfo(CacheDirectory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    DateTime.UtcNow - file.LastWriteTimeUtc >= CacheLifetime ||
                    file.LastWriteTimeUtc - DateTime.UtcNow > TimeSpan.FromMinutes(5))
                {
                    TryDelete(file.FullName);
                    continue;
                }
                total += file.Length;
            }
            return total;
        }
        catch { return 0; }
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string GetCacheFilePath(string path)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in path.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return Path.Combine(CacheDirectory, $"cache_{hash:X16}.htc");
    }

    private static void WriteItem(BinaryWriter bw, DiskItemModel item)
    {
        bw.Write(item.Name ?? string.Empty);
        bw.Write(item.FullPath ?? string.Empty);
        bw.Write(item.SizeBytes);
        bw.Write(item.IsFolder);
        bw.Write(item.Percentage);
        bw.Write(item.ColorHex ?? "#38BDF8");
        bw.Write(item.Level);
        bw.Write(item.StartAngle);
        bw.Write(item.SweepAngle);
    }

    private static DiskItemModel ReadItem(BinaryReader br)
    {
        return new DiskItemModel
        {
            Name = br.ReadString(),
            FullPath = br.ReadString(),
            SizeBytes = br.ReadInt64(),
            IsFolder = br.ReadBoolean(),
            Percentage = br.ReadDouble(),
            ColorHex = br.ReadString(),
            Level = br.ReadInt32(),
            StartAngle = br.ReadDouble(),
            SweepAngle = br.ReadDouble()
        };
    }
}
