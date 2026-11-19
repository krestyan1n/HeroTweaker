using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class DiskAnalyzerService
{
    private static readonly string[] SunburstPalette =
    {
        "#38BDF8", // Sky blue
        "#0EA5E9", // Blue
        "#6366F1", // Indigo
        "#14B8A6", // Teal
        "#22D3EE", // Cyan
        "#3B82F6", // Azure
        "#2DD4BF", // Turquoise
        "#818CF8", // Periwinkle
        "#06B6D4", // Deep cyan
        "#60A5FA", // Soft blue
        "#5EEAD4", // Mint
        "#4F46E5"  // Violet
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    public static class RecycleBinHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;      // Отправлять именно в Корзину
        private const ushort FOF_NOCONFIRMATION = 0x0010;  // Не показывать системное окно (у нас уже есть свой MessageBox)
        private const ushort FOF_SILENT = 0x0004;         // Без системного прогресс-бара
        private const ushort FOF_NOERRORUI = 0x0400;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation([In] ref SHFILEOPSTRUCT lpFileOp);

        public static bool SendToRecycleBin(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            // Проверяем существование файла или папки
            bool isDir = Directory.Exists(path);
            bool isFile = File.Exists(path);
            if (!isDir && !isFile) return false;

            try
            {
                // КРИТИЧЕСКИ ВАЖНО: Win32 требует двойной нуль в конце строки пути (\0\0)
                string nullTerminatedPath = path.TrimEnd('\\') + "\0\0";

                var fileOp = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = nullTerminatedPath,
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
                };

                int result = SHFileOperation(ref fileOp);
                return result == 0 && !fileOp.fAnyOperationsAborted;
            }
            catch
            {
                // Fallback: штатный метод .NET Framework / VisualBasic
                try
                {
                    if (Directory.Exists(path))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        return true;
                    }
                    if (File.Exists(path))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        return true;
                    }
                }
                catch { }

                return false;
            }
        }
    }

    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FindFirstExLargeFetch = 2;

    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400; // Пропуск симлинков и Junctions

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private class Level1ScanNode
    {
        public DiskItemModel Item { get; set; } = new();
        public List<DiskItemModel> Children { get; set; } = new();
    }

    public static async Task<(List<DiskItemModel> allSlices, List<DiskItemModel> level1Items, long totalSize)> AnalyzeSunburstAsync(
        string rootPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string cleanRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // 1. Получаем список элементов 1-го уровня
            var (topDirs, looseFilesSize) = EnumerateTopLevel(cleanRoot);
            ct.ThrowIfCancellationRequested();

            var scannedNodes = new ConcurrentBag<Level1ScanNode>();

            // Если в самом корне есть отдельные файлы
            if (looseFilesSize > 0)
            {
                var filesNode = new Level1ScanNode
                {
                    Item = new DiskItemModel
                    {
                        Name = "Файлы в корне",
                        FullPath = string.Empty,
                        SizeBytes = looseFilesSize,
                        IsFolder = false,
                        IsSynthetic = true,
                        Level = 1,
                        ColorHex = "#94A3B8"
                    }
                };
                scannedNodes.Add(filesNode);
            }

            // 2. Параллельный сбор данных Level 1 и дочерних Level 2
            var parallelOpts = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
            };

            Parallel.ForEach(topDirs, parallelOpts, dirPath =>
            {
                parallelOpts.CancellationToken.ThrowIfCancellationRequested();
                string dirName = Path.GetFileName(dirPath);
                if (string.IsNullOrEmpty(dirName)) dirName = dirPath;

                // Получаем список подпапок Level 2 для построения внешнего кольца Sunburst
                var (subDirs, subLooseFiles) = EnumerateTopLevel(dirPath + Path.DirectorySeparatorChar);
                var level2Children = new List<DiskItemModel>();
                long calculatedDirSize = subLooseFiles;

                if (subLooseFiles > 0)
                {
                    level2Children.Add(new DiskItemModel
                    {
                        Name = "Файлы",
                        FullPath = dirPath,
                        SizeBytes = subLooseFiles,
                        IsFolder = false,
                        Level = 2
                    });
                }

                foreach (var subDirPath in subDirs)
                {
                    parallelOpts.CancellationToken.ThrowIfCancellationRequested();
                    long subSize = FastCalculateDirectorySize(subDirPath, parallelOpts.CancellationToken);
                    if (subSize > 0)
                    {
                        calculatedDirSize += subSize;
                        level2Children.Add(new DiskItemModel
                        {
                            Name = Path.GetFileName(subDirPath),
                            FullPath = subDirPath,
                            SizeBytes = subSize,
                            IsFolder = true,
                            Level = 2
                        });
                    }
                }

                if (calculatedDirSize > 0)
                {
                    var node = new Level1ScanNode
                    {
                        Item = new DiskItemModel
                        {
                            Name = dirName,
                            FullPath = dirPath,
                            SizeBytes = calculatedDirSize,
                            IsFolder = true,
                            Level = 1
                        },
                        Children = level2Children.OrderByDescending(x => x.SizeBytes).ToList()
                    };
                    scannedNodes.Add(node);
                }
            });

            ct.ThrowIfCancellationRequested();

            // 3. Расчет геометрии дуг Sunburst (StartAngle и SweepAngle от 0 до 360°)
            var sortedNodes = scannedNodes.OrderByDescending(n => n.Item.SizeBytes).ToList();
            long totalSize = sortedNodes.Sum(n => n.Item.SizeBytes);
            if (totalSize <= 0) totalSize = 1;

            var allSlices = new List<DiskItemModel>();
            var level1List = new List<DiskItemModel>();

            double currentAngle = 0.0;
            int paletteIdx = 0;

            foreach (var node in sortedNodes)
            {
                var l1 = node.Item;
                l1.Percentage = Math.Round((double)l1.SizeBytes / totalSize * 100.0, 1);
                l1.SweepAngle = ((double)l1.SizeBytes / totalSize) * 360.0;
                l1.StartAngle = currentAngle;

                if (l1.IsFolder)
                {
                    l1.ColorHex = SunburstPalette[paletteIdx % SunburstPalette.Length];
                    paletteIdx++;
                }

                level1List.Add(l1);
                allSlices.Add(l1);

                // Расчет секторов для внешнего кольца Level 2
                if (node.Children.Count > 0 && l1.SizeBytes > 0)
                {
                    double childAngle = currentAngle;
                    int childIdx = 0;

                    foreach (var child in node.Children)
                    {
                        child.Percentage = Math.Round((double)child.SizeBytes / totalSize * 100.0, 1);
                        child.SweepAngle = ((double)child.SizeBytes / l1.SizeBytes) * l1.SweepAngle;
                        child.StartAngle = childAngle;
                        child.ColorHex = TintColor(l1.ColorHex, childIdx);

                        allSlices.Add(child);
                        childAngle += child.SweepAngle;
                        childIdx++;
                    }
                }

                currentAngle += l1.SweepAngle;
            }

            return (allSlices, level1List, totalSize);
        }, ct);
    }

    private static string TintColor(string hexColor, int index)
    {
        try
        {
            if (hexColor.StartsWith("#") && hexColor.Length == 7)
            {
                byte r = Convert.ToByte(hexColor.Substring(1, 2), 16);
                byte g = Convert.ToByte(hexColor.Substring(3, 2), 16);
                byte b = Convert.ToByte(hexColor.Substring(5, 2), 16);

                double factor = 0.72 + (index % 4) * 0.09;
                byte nr = (byte)Math.Clamp((int)(r * factor), 0, 255);
                byte ng = (byte)Math.Clamp((int)(g * factor), 0, 255);
                byte nb = (byte)Math.Clamp((int)(b * factor), 0, 255);

                return $"#{nr:X2}{ng:X2}{nb:X2}";
            }
        }
        catch { }
        return hexColor;
    }

    private static (List<string> Dirs, long LooseFilesSize) EnumerateTopLevel(string root)
    {
        var dirs = new List<string>();
        long filesSize = 0;

        string searchPattern = Path.Combine(root, "*");
        IntPtr hFind = FindFirstFileExW(searchPattern, FindExInfoBasic, out var findData, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);

        if (hFind == InvalidHandleValue) return (dirs, filesSize);

        try
        {
            do
            {
                string name = findData.cFileName;
                if (name == "." || name == "..") continue;

                // Пропуск символических ссылок и junctions во избежание рекурсивных петель
                if ((findData.dwFileAttributes & FileAttributeReparsePoint) != 0) continue;

                if ((findData.dwFileAttributes & FileAttributeDirectory) != 0)
                {
                    dirs.Add(Path.Combine(root, name));
                }
                else
                {
                    long size = ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;
                    filesSize += size;
                }
            }
            while (FindNextFileW(hFind, out findData));
        }
        finally
        {
            FindClose(hFind);
        }

        return (dirs, filesSize);
    }

    private static long FastCalculateDirectorySize(string rootDir, CancellationToken ct)
    {
        long totalBytes = 0;
        var stack = new Stack<string>(64);
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return totalBytes;

            string currentDir = stack.Pop();
            string pattern = Path.Combine(currentDir, "*");

            IntPtr hFind = FindFirstFileExW(pattern, FindExInfoBasic, out var findData, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);
            if (hFind == InvalidHandleValue) continue;

            try
            {
                do
                {
                    string name = findData.cFileName;
                    if (name == "." || name == "..") continue;

                    if ((findData.dwFileAttributes & FileAttributeReparsePoint) != 0) continue;

                    if ((findData.dwFileAttributes & FileAttributeDirectory) != 0)
                    {
                        stack.Push(Path.Combine(currentDir, name));
                    }
                    else
                    {
                        totalBytes += ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;
                    }
                }
                while (FindNextFileW(hFind, out findData));
            }
            finally
            {
                FindClose(hFind);
            }
        }

        return totalBytes;
    }

    public static bool MoveToRecycleBin(string path)
    {
        return RecycleBinHelper.SendToRecycleBin(path);
    }
}
