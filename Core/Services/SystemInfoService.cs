using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HeroTweaker.Core.Services;

public class SystemInfoModel
{
    public string OsName { get; set; } = "Windows 11 Pro";
    public string OsBuild { get; set; } = "26100";
    public string CpuName { get; set; } = "AMD Ryzen / Intel Core";
    public int CpuThreads { get; set; } = 16;
    public string TotalRamFormatted { get; set; } = "32 ГБ";
    public string GpuName { get; set; } = "NVIDIA GeForce";
}

public static class SystemInfoService
{
    public static async Task<SystemInfoModel> GetSystemInfoAsync()
    {
        return await Task.Run(() =>
        {
            var info = new SystemInfoModel();

            // 1. Точное определение Windows через Реестр
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    string productName = key.GetValue("ProductName") as string ?? "Windows 11";
                    string currentBuild = key.GetValue("CurrentBuild") as string ?? "22631";
                    string ubr = key.GetValue("UBR")?.ToString() ?? "0";

                    if (productName.Contains("Windows 10") && int.TryParse(currentBuild, out int buildNum) && buildNum >= 22000)
                    {
                        productName = productName.Replace("Windows 10", "Windows 11");
                    }

                    info.OsName = productName;
                    info.OsBuild = $"{currentBuild}.{ubr}";
                }
            }
            catch
            {
                info.OsName = Environment.OSVersion.VersionString;
                info.OsBuild = Environment.OSVersion.Version.ToString();
            }

            // 2. Определение процессора и потоков через Реестр
            try
            {
                using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (cpuKey != null)
                {
                    string cpuStr = cpuKey.GetValue("ProcessorNameString") as string ?? "";
                    if (!string.IsNullOrWhiteSpace(cpuStr))
                    {
                        info.CpuName = cpuStr.Trim();
                    }
                }
                info.CpuThreads = Environment.ProcessorCount;
            }
            catch { }

            // 3. Объем оперативной памяти через Win32 API
            try
            {
                long totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (totalBytes > 0)
                {
                    double gb = totalBytes / (1024.0 * 1024.0 * 1024.0);
                    // Округляем до стандартных планок RAM (8, 16, 32, 64 ГБ)
                    int roundedGb = (int)(Math.Round(gb / 4.0) * 4.0);
                    info.TotalRamFormatted = $"{Math.Max(roundedGb, gb):F0} ГБ";
                }
            }
            catch { }

            // 4. Определение видеокарты через реестр DirectX
            try
            {
                using var dxKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
                if (dxKey != null)
                {
                    string driverDesc = dxKey.GetValue("DriverDesc") as string ?? "";
                    if (!string.IsNullOrWhiteSpace(driverDesc))
                    {
                        info.GpuName = driverDesc.Trim();
                    }
                }
            }
            catch { }

            return info;
        });
    }
}