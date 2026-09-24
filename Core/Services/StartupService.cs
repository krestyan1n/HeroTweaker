using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class StartupService
{
    private const string ApprovedRunPathUser = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRunPathMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static async Task<List<StartupItemModel>> GetStartupItemsAsync()
    {
        return await Task.Run(() =>
        {
            var items = new List<StartupItemModel>();

            // 1. HKCU Run
            ScanRegistryRun(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", items, true);

            // 2. HKLM Run
            ScanRegistryRun(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", items, false);

            // 3. HKLM WOW6432Node Run
            ScanRegistryRun(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", items, false);

            // 4. Папки Startup
            ScanFolderStartup(Environment.GetFolderPath(Environment.SpecialFolder.Startup), items, true);
            ScanFolderStartup(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), items, false);

            return items.OrderBy(i => i.Impact == StartupImpact.High ? 0 : (i.Impact == StartupImpact.Medium ? 1 : 2))
                        .ThenBy(i => i.Name)
                        .ToList();
        });
    }

    private static void ScanRegistryRun(RegistryHive hive, string path, List<StartupItemModel> list, bool isCurrentUser)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(path);
            if (key == null) return;

            string approvedPath = isCurrentUser ? ApprovedRunPathUser : ApprovedRunPathMachine;
            using var approvedKey = baseKey.OpenSubKey(approvedPath);

            foreach (var valName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valName)) continue;

                string command = key.GetValue(valName)?.ToString() ?? string.Empty;
                bool isEnabled = true;

                if (approvedKey != null)
                {
                    var approvedVal = approvedKey.GetValue(valName) as byte[];
                    if (approvedVal != null && approvedVal.Length > 0 && approvedVal[0] % 2 != 0)
                    {
                        isEnabled = false;
                    }
                }

                var item = new StartupItemModel
                {
                    Id = $"{hive}_{path}_{valName}",
                    Name = valName,
                    Command = command,
                    Location = isCurrentUser ? "Реестр (User Run)" : "Реестр (System Run)",
                    RegistryKeyPath = path,
                    IsRegistry = true,
                    IsCurrentUser = isCurrentUser,
                    IsEnabled = isEnabled,
                    Impact = EvaluateImpact(valName, command),
                    Publisher = ExtractPublisher(command)
                };

                if (!list.Any(i => i.Name.Equals(valName, StringComparison.OrdinalIgnoreCase) && i.Command.Equals(command, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(item);
                }
            }
        }
        catch { }
    }

    private static void ScanFolderStartup(string folderPath, List<StartupItemModel> list, bool isUser)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return;

            var di = new DirectoryInfo(folderPath);
            foreach (var file in di.EnumerateFiles())
            {
                if (file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    bool isEnabled = !file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    string name = Path.GetFileNameWithoutExtension(file.Name.Replace(".disabled", ""));

                    list.Add(new StartupItemModel
                    {
                        Id = file.FullName,
                        Name = name,
                        Command = file.FullName,
                        Location = isUser ? "Папка автозагрузки (User)" : "Папка автозагрузки (All Users)",
                        IsRegistry = false,
                        ShortcutFilePath = file.FullName,
                        IsEnabled = isEnabled,
                        Impact = StartupImpact.Medium,
                        Publisher = "Локальный ярлык"
                    });
                }
            }
        }
        catch { }
    }

    public static async Task SetStartupItemStateAsync(StartupItemModel item, bool enable)
    {
        await Task.Run(() =>
        {
            if (item.IsRegistry)
            {
                var hive = item.IsCurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                string approvedPath = item.IsCurrentUser ? ApprovedRunPathUser : ApprovedRunPathMachine;

                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var approvedKey = baseKey.CreateSubKey(approvedPath);
                if (approvedKey != null)
                {
                    byte[] val = enable
                        ? new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }
                        : new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                    approvedKey.SetValue(item.Name, val, RegistryValueKind.Binary);
                }
            }
            else if (!string.IsNullOrEmpty(item.ShortcutFilePath))
            {
                if (enable && item.ShortcutFilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    string newPath = item.ShortcutFilePath.Substring(0, item.ShortcutFilePath.Length - 9);
                    File.Move(item.ShortcutFilePath, newPath, true);
                    item.ShortcutFilePath = newPath;
                }
                else if (!enable && !item.ShortcutFilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    string newPath = item.ShortcutFilePath + ".disabled";
                    File.Move(item.ShortcutFilePath, newPath, true);
                    item.ShortcutFilePath = newPath;
                }
            }

            item.IsEnabled = enable;
        });
    }

    private static StartupImpact EvaluateImpact(string name, string command)
    {
        string combined = $"{name} {command}".ToLowerInvariant();
        if (combined.Contains("epicgames") || combined.Contains("steam") || combined.Contains("discord") ||
            combined.Contains("spotify") || combined.Contains("telegram") || combined.Contains("update") ||
            combined.Contains("edge") || combined.Contains("chrome"))
        {
            return StartupImpact.High;
        }
        if (combined.Contains("onedrive") || combined.Contains("security") || combined.Contains("audio") || combined.Contains("realtek"))
        {
            return StartupImpact.Medium;
        }
        return StartupImpact.Low;
    }

    private static string ExtractPublisher(string command)
    {
        try
        {
            string clean = command.Trim('\"');
            int exeIdx = clean.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx != -1) clean = clean.Substring(0, exeIdx + 4);

            if (File.Exists(clean))
            {
                var info = FileVersionInfo.GetVersionInfo(clean);
                if (!string.IsNullOrWhiteSpace(info.CompanyName))
                    return info.CompanyName;
            }
        }
        catch { }
        return "Сторонний разработчик";
    }
}