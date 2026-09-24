using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HeroTweaker.Core.Models;
using HeroTweaker.Core.Models.Transactions;

namespace HeroTweaker.Core.Services;

public static class DiskCleanupService
{
    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    public static List<CleanupCategoryModel> GetDefaultCategories()
    {
        return new List<CleanupCategoryModel>
        {
            new() { Id = "user_temp", Title = "Временные файлы пользователя (User Temp)", Description = "Кэш запущенных приложений и временные распаковщики (%TEMP%).", IconGlyph = "📁", Risk = RiskLevel.Safe, IsSelected = true },
            new() { Id = "win_temp", Title = "Системные временные файлы Windows", Description = "Логи установщиков и временные файлы служб ОС (C:\\Windows\\Temp).", IconGlyph = "⚙️", Risk = RiskLevel.Safe, IsSelected = true },
            new() { Id = "gpu_shaders", Title = "Кэш шейдеров GPU (DirectX, NVIDIA, AMD)", Description = "Скомпилированные шейдеры игр. Удаление устраняет статтеры при багах кэша.", IconGlyph = "🎮", Risk = RiskLevel.Safe, IsSelected = true },
            new() { Id = "wu_cache", Title = "Загрузки Центра обновления Windows", Description = "Уже установленные пакеты обновлений из папки SoftwareDistribution.", IconGlyph = "🔄", Risk = RiskLevel.Safe, IsSelected = true },
            new() { Id = "crash_dumps", Title = "Системные дампы сбоев и отчеты WER", Description = "Файлы аварийных дампов памяти (Memory Dumps) и логи ошибок Windows.", IconGlyph = "💥", Risk = RiskLevel.Safe, IsSelected = true },
            new() { Id = "thumb_cache", Title = "Кэш эскизов Проводника (Thumbnails)", Description = "База предварительного просмотра картинок и видео в проводнике.", IconGlyph = "🖼️", Risk = RiskLevel.Safe, IsSelected = false },
            new() { Id = "recycle_bin", Title = "Корзина Windows", Description = "Удаленные файлы со всех локальных дисков системы.", IconGlyph = "🗑️", Risk = RiskLevel.Safe, IsSelected = true },
        };
    }

    public static async Task ScanCategoryAsync(CleanupCategoryModel cat)
    {
        await Task.Run(() =>
        {
            try
            {
                long bytes = 0;
                int count = 0;

                var dirs = GetTargetDirectories(cat.Id);
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    var di = new DirectoryInfo(dir);
                    try
                    {
                        foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                bytes += fi.Length;
                                count++;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (cat.Id == "recycle_bin")
                {
                    foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                    {
                        string rPath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                        if (Directory.Exists(rPath))
                        {
                            try
                            {
                                var di = new DirectoryInfo(rPath);
                                foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                                {
                                    try { bytes += fi.Length; count++; } catch { }
                                }
                            }
                            catch { }
                        }
                    }
                }

                cat.SizeBytes = bytes;
                cat.FilesCount = count;
            }
            catch { }
        });
    }

    public static async Task<long> CleanCategoriesAsync(IEnumerable<CleanupCategoryModel> categories)
    {
        return await Task.Run(() =>
        {
            long freed = 0;

            foreach (var cat in categories.Where(c => c.IsSelected))
            {
                if (cat.Id == "recycle_bin")
                {
                    freed += cat.SizeBytes;
                    try { SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND); } catch { }
                    cat.SizeBytes = 0;
                    cat.FilesCount = 0;
                    continue;
                }

                var dirs = GetTargetDirectories(cat.Id);
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    var di = new DirectoryInfo(dir);

                    foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            long len = fi.Length;
                            fi.Attributes = FileAttributes.Normal;
                            fi.Delete();
                            freed += len;
                        }
                        catch { }
                    }

                    foreach (var sub in di.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                    {
                        try { sub.Delete(true); } catch { }
                    }
                }

                cat.SizeBytes = 0;
                cat.FilesCount = 0;
            }

            return freed;
        });
    }

    private static List<string> GetTargetDirectories(string categoryId)
    {
        var list = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        switch (categoryId)
        {
            case "user_temp":
                list.Add(Path.GetTempPath());
                break;

            case "win_temp":
                list.Add(Path.Combine(winDir, "Temp"));
                break;

            case "gpu_shaders":
                list.Add(Path.Combine(localAppData, "D3DSCache"));
                list.Add(Path.Combine(localAppData, "NVIDIA", "DXCache"));
                list.Add(Path.Combine(localAppData, "NVIDIA", "GLCache"));
                list.Add(Path.Combine(localAppData, "AMD", "DxCache"));
                break;

            case "wu_cache":
                list.Add(Path.Combine(winDir, "SoftwareDistribution", "Download"));
                break;

            case "crash_dumps":
                list.Add(Path.Combine(localAppData, "CrashDumps"));
                list.Add(Path.Combine(winDir, "Minidump"));
                list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER"));
                break;

            case "thumb_cache":
                list.Add(Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"));
                break;
        }

        return list;
    }
}