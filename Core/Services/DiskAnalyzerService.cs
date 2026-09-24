using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using HeroTweaker.Core.Models;
using SearchOption = System.IO.SearchOption;

namespace HeroTweaker.Core.Services;

public static class DiskAnalyzerService
{
    private static readonly string[] Palette = new[]
    {
        "#6366F1", "#EC4899", "#8B5CF6", "#10B981", "#F59E0B",
        "#3B82F6", "#14B8A6", "#F43F5E", "#84CC16", "#06B6D4",
        "#A855F7", "#EAB308", "#64748B"
    };

    public static async Task<(List<DiskItemModel> AllSlices, List<DiskItemModel> Level1Items, long TotalSize)> AnalyzeSunburstAsync(
        string directoryPath,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var level1Raw = new List<DiskItemModel>();

            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return (new List<DiskItemModel>(), new List<DiskItemModel>(), 0L);

            try
            {
                var dirOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };

                foreach (var dirPath in Directory.EnumerateDirectories(directoryPath, "*", dirOptions))
                {
                    if (ct.IsCancellationRequested) return (new List<DiskItemModel>(), new List<DiskItemModel>(), 0L);

                    long dirSize = CalculateDirectorySizeFast(dirPath, ct);
                    if (dirSize > 0)
                    {
                        level1Raw.Add(new DiskItemModel
                        {
                            Name = Path.GetFileName(dirPath),
                            FullPath = dirPath,
                            SizeBytes = dirSize,
                            IsFolder = true,
                            RingLevel = 1
                        });
                    }
                }
            }
            catch { }

            try
            {
                var fileOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false
                };

                foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", fileOptions))
                {
                    if (ct.IsCancellationRequested) return (new List<DiskItemModel>(), new List<DiskItemModel>(), 0L);

                    try
                    {
                        var fi = new FileInfo(filePath);
                        level1Raw.Add(new DiskItemModel
                        {
                            Name = fi.Name,
                            FullPath = filePath,
                            SizeBytes = fi.Length,
                            IsFolder = false,
                            RingLevel = 1
                        });
                    }
                    catch { }
                }
            }
            catch { }

            if (ct.IsCancellationRequested) return (new List<DiskItemModel>(), new List<DiskItemModel>(), 0L);

            long totalSize = level1Raw.Sum(i => i.SizeBytes);
            if (totalSize == 0) return (new List<DiskItemModel>(), new List<DiskItemModel>(), 0L);

            var orderedLevel1 = level1Raw.OrderByDescending(i => i.SizeBytes).ToList();
            var level1Significant = new List<DiskItemModel>();
            long otherSize = 0;

            foreach (var item in orderedLevel1)
            {
                double pct = (double)item.SizeBytes / totalSize * 100.0;
                item.Percentage = Math.Round(pct, 1);

                if (pct >= 1.5 || level1Significant.Count < 5)
                {
                    level1Significant.Add(item);
                }
                else
                {
                    otherSize += item.SizeBytes;
                }
            }

            if (otherSize > 0)
            {
                level1Significant.Add(new DiskItemModel
                {
                    Name = "Прочие файлы и папки",
                    FullPath = directoryPath,
                    SizeBytes = otherSize,
                    Percentage = Math.Round((double)otherSize / totalSize * 100.0, 1),
                    IsFolder = true,
                    ColorHex = "#475569",
                    RingLevel = 1
                });
            }

            double currentAngle = 0;
            var allSlices = new List<DiskItemModel>();

            for (int i = 0; i < level1Significant.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                var parent = level1Significant[i];
                if (string.IsNullOrEmpty(parent.ColorHex) || parent.ColorHex == "#6366F1")
                    parent.ColorHex = Palette[i % Palette.Length];

                double sweep = (double)parent.SizeBytes / totalSize * 360.0;
                if (sweep > 359.99) sweep = 359.99;

                parent.StartAngle = currentAngle;
                parent.SweepAngle = sweep;
                allSlices.Add(parent);

                if (parent.IsFolder && parent.Name != "Прочие файлы и папки" && parent.Percentage >= 5.0)
                {
                    var children = ScanLevel2Optimized(parent.FullPath, parent.ColorHex, ct);
                    long childrenTotal = children.Sum(c => c.SizeBytes);

                    if (childrenTotal > 0)
                    {
                        double childCurrentAngle = parent.StartAngle;
                        foreach (var child in children)
                        {
                            child.Parent = parent;
                            double childSweep = (double)child.SizeBytes / childrenTotal * parent.SweepAngle;
                            if (childSweep < 1.0) continue;

                            child.StartAngle = childCurrentAngle;
                            child.SweepAngle = childSweep;
                            child.Percentage = Math.Round((double)child.SizeBytes / totalSize * 100.0, 1);
                            childCurrentAngle += childSweep;

                            parent.Children.Add(child);
                            allSlices.Add(child);
                        }
                    }
                }

                currentAngle += sweep;
            }

            return (allSlices, level1Significant, totalSize);
        }, ct);
    }

    private static List<DiskItemModel> ScanLevel2Optimized(string parentPath, string baseColorHex, CancellationToken ct)
    {
        var list = new List<DiskItemModel>();
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var dir in Directory.EnumerateDirectories(parentPath, "*", options).Take(6))
            {
                if (ct.IsCancellationRequested) break;
                long size = CalculateDirectorySizeFast(dir, ct, maxDepth: 2);
                if (size > 0)
                {
                    list.Add(new DiskItemModel
                    {
                        Name = Path.GetFileName(dir),
                        FullPath = dir,
                        SizeBytes = size,
                        IsFolder = true,
                        RingLevel = 2,
                        ColorHex = AdjustBrightness(baseColorHex, 1.2)
                    });
                }
            }
        }
        catch { }

        return list.OrderByDescending(x => x.SizeBytes).ToList();
    }

    private static long CalculateDirectorySizeFast(string rootPath, CancellationToken ct, int maxDepth = 6)
    {
        long totalSize = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((rootPath, 0));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
        };

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return totalSize;
            var (currentDir, depth) = stack.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(currentDir, "*", options))
                {
                    try { totalSize += new FileInfo(file).Length; } catch { }
                }

                if (depth < maxDepth)
                {
                    foreach (var sub in Directory.EnumerateDirectories(currentDir, "*", options))
                    {
                        stack.Push((sub, depth + 1));
                    }
                }
            }
            catch { }
        }

        return totalSize;
    }

    private static string AdjustBrightness(string hexColor, double factor)
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
            byte r = (byte)Math.Clamp(c.R * factor, 0, 255);
            byte g = (byte)Math.Clamp(c.G * factor, 0, 255);
            byte b = (byte)Math.Clamp(c.B * factor, 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch { return hexColor; }
    }

    // ==========================================
    // БЕЗОПАСНОЕ ПЕРЕМЕЩЕНИЕ В КОРЗИНУ WINDOWS
    // ==========================================
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    public static bool MoveToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string cleanPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            if (Directory.Exists(cleanPath))
            {
                FileSystem.DeleteDirectory(cleanPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return true;
            }

            if (File.Exists(cleanPath))
            {
                FileSystem.DeleteFile(cleanPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return true;
            }
        }
        catch
        {
            try
            {
                var shf = new SHFILEOPSTRUCT
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FO_DELETE,
                    pFrom = cleanPath + '\0' + '\0',
                    pTo = null,
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
                    fAnyOperationsAborted = false,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = null
                };

                return SHFileOperation(ref shf) == 0 && !shf.fAnyOperationsAborted;
            }
            catch { return false; }
        }

        return false;
    }

    public static async Task<long> PerformDeepCleanAsync(bool cleanUpdates, bool cleanShaders, bool cleanDumps, bool cleanThumbnails)
    {
        return await Task.Run(() =>
        {
            long freedBytes = 0;
            if (cleanUpdates) freedBytes += CleanFolderFiles(@"C:\Windows\SoftwareDistribution\Download");

            if (cleanShaders)
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                freedBytes += CleanFolderFiles(Path.Combine(localApp, "D3DSCache"));
                freedBytes += CleanFolderFiles(Path.Combine(localApp, "NVIDIA", "DXCache"));
                freedBytes += CleanFolderFiles(Path.Combine(localApp, "AMD", "DxCache"));
            }

            if (cleanDumps)
            {
                freedBytes += CleanFolderFiles(@"C:\Windows\Minidump");
                string crashPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
                freedBytes += CleanFolderFiles(crashPath);
            }

            if (cleanThumbnails)
            {
                string thumbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
                freedBytes += CleanFilesMatching(thumbPath, "thumbcache_*.db");
            }

            return freedBytes;
        });
    }

    private static long CleanFolderFiles(string path)
    {
        long freed = 0;
        if (!Directory.Exists(path)) return 0;
        try
        {
            var di = new DirectoryInfo(path);
            foreach (var file in di.GetFiles("*", System.IO.SearchOption.AllDirectories))
            {
                try { long len = file.Length; file.Delete(); freed += len; } catch { }
            }
        }
        catch { }
        return freed;
    }

    private static long CleanFilesMatching(string path, string pattern)
    {
        long freed = 0;
        if (!Directory.Exists(path)) return 0;
        try
        {
            var di = new DirectoryInfo(path);
            foreach (var file in di.GetFiles(pattern, System.IO.SearchOption.TopDirectoryOnly))
            {
                try { long len = file.Length; file.Delete(); freed += len; } catch { }
            }
        }
        catch { }
        return freed;
    }
}