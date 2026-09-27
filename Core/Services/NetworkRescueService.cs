using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HeroTweaker.Core.Services;

public record BrokenNetworkDevice(string Name, string DeviceId, string Status, int ErrorCode);

public static class NetworkRescueService
{
    // Поиск проблемных сетевых устройств через встроенный системный PnPUtil (без тяжелого WMI)
    public static async Task<List<BrokenNetworkDevice>> DetectBrokenNetworkAdaptersAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<BrokenNetworkDevice>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-devices /problem",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);

                    var blocks = output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var b in blocks)
                    {
                        if (b.Contains("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                            b.Contains("Net", StringComparison.OrdinalIgnoreCase) ||
                            b.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                            b.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
                            b.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                            b.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
                        {
                            string devId = Regex.Match(b, @"(?i)(?:Идентификатор экземпляра|Instance ID)\s*:\s*(.+)").Groups[1].Value.Trim();
                            string name = Regex.Match(b, @"(?i)(?:Описание устройства|Device Description)\s*:\s*(.+)").Groups[1].Value.Trim();
                            string problem = Regex.Match(b, @"(?i)(?:Код проблемы|Problem Code)\s*:\s*(\d+)").Groups[1].Value.Trim();

                            int code = int.TryParse(problem, out int c) ? c : 28;
                            if (string.IsNullOrEmpty(name)) name = "Сетевой контроллер (без драйвера)";

                            list.Add(new BrokenNetworkDevice(name, devId, "No Driver / Error", code));
                        }
                    }
                }
            }
            catch { }
            return list;
        });
    }

    // 1. Аппаратный рескан шины PnP (поиск нового оборудования)
    public static async Task<(bool Success, string Output)> RescanPnpDevicesAsync()
    {
        return await RunProcessAsync("pnputil.exe", "/scan-devices");
    }

    // 2. Жесткий сброс всего сетевого стека Microsoft (netcfg -d)
    public static async Task<(bool Success, string Output)> HardResetNetworkStackAsync()
    {
        return await RunProcessAsync("netcfg.exe", "-d");
    }

    // 3. Пакетная установка любых сторонних INF-драйверов из папки
    public static async Task<(bool Success, string Output)> InstallDriversFromFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return (false, "Папка с драйверами не найдена.");

        return await RunProcessAsync("pnputil.exe", $"/add-driver \"{Path.Combine(folderPath, "*.inf")}\" /subdirs /install");
    }

    // 4. Установка аварийного адаптера (Microsoft KM-TEST Loopback Adapter)
    public static async Task<(bool Success, string Output)> InstallEmergencyLoopbackAdapterAsync()
    {
        return await Task.Run(async () =>
        {
            string infPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "inf", "netloop.inf");
            if (!File.Exists(infPath))
                return (false, "Файл netloop.inf не найден в каталоге Windows.");

            var result = await RunProcessAsync("pnputil.exe", $"/add-driver \"{infPath}\" /install");
            return result;
        });
    }

    // 5. Разблокировка OOBE в Windows 11 при отсутствии сети
    public static void BypassWindows11NetworkOobe()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f",
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit();
        }
        catch { }
    }

    private static async Task<(bool Success, string Output)> RunProcessAsync(string fileName, string args)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Не удалось создать процесс.");

                string output = proc.StandardOutput.ReadToEnd();
                string error = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30000);

                string total = string.IsNullOrWhiteSpace(error) ? output : $"{output}\nОшибки:\n{error}";
                return (proc.ExitCode == 0, total.Trim());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }
}