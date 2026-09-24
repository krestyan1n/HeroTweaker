using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HeroTweaker.Core.Services;

public static class SystemUtility
{
    public static async Task<(bool Success, string Message)> CreateRestorePointAsync(Action<string>? statusCallback = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                statusCallback?.Invoke("Снятие 24-часового ограничения Windows на создание точек...");

                // 1. Снимаем встроенный лимит Windows на частые точки восстановления
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
                    key?.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                }
                catch { }

                statusCallback?.Invoke("Проверка защиты диска C: и создание снимка системы...");

                // 2. Активируем защиту на диске C: (если отключена) и вызываем Checkpoint-Computer
                string script = @"
                    $ErrorActionPreference = 'Stop'
                    try {
                        Enable-ComputerRestore -Drive 'C:\' -ErrorAction SilentlyContinue
                    } catch {}
                    Checkpoint-Computer -Description 'HeroTweaker System Restore Point' -RestorePointType 'MODIFY_SETTINGS'
                    $rp = Get-ComputerRestorePoint | Select-Object -Last 1
                    if ($rp) { Write-Output ""SUCCESS:$($rp.Description)"" } else { Write-Output 'FAILED' }
                ";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return (false, "Не удалось инициализировать процесс PowerShell.");

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (stdout.Contains("SUCCESS"))
                {
                    return (true, "Контрольная точка восстановления «HeroTweaker System Restore Point» успешно создана в системе!");
                }
                else
                {
                    string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    if (err.Contains("1058") || err.Contains("disabled") || err.Contains("отключена"))
                    {
                        return (false, "Служба «Теневое копирование тома» (VSS) или защита системы отключена в службах Windows.");
                    }
                    return (false, $"Не удалось зарегистрировать точку. Системный вывод:\n{err.Trim()}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Исключение при создании точки: {ex.Message}");
            }
        });
    }

    public static async Task<long> CleanTempFilesAsync()
    {
        return await Task.Run(() =>
        {
            long totalFreed = 0;
            string[] paths = {
                Path.GetTempPath(),
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Temp")
            };

            foreach (var path in paths)
            {
                if (!Directory.Exists(path)) continue;
                var di = new DirectoryInfo(path);

                foreach (var file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        long len = file.Length;
                        file.Delete();
                        totalFreed += len;
                    }
                    catch { }
                }

                foreach (var dir in di.EnumerateDirectories())
                {
                    try { dir.Delete(true); } catch { }
                }
            }

            return totalFreed;
        });
    }

    public static async Task RestartExplorerAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); p.WaitForExit(1000); } catch { }
                }
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                });
            }
            catch { }
        });
    }
}