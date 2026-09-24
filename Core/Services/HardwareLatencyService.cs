using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class HardwareLatencyService
{
    /// <summary>
    /// Автоматически активирует режим MSI Mode (Message Signaled Interrupts) и приоритет High
    /// для видеокарты, сетевого адаптера, звукового контроллера и USB-хабов.
    /// </summary>
    public static async Task<(int ConfiguredCount, string Details)> OptimizeMsiModeAsync(Action<string, LogLevel>? log = null)
    {
        return await Task.Run(() =>
        {
            int configured = 0;
            try
            {
                using var pciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI", true);
                if (pciKey == null) return (0, "Не удалось открыть ветку шины PCI в реестре.");

                foreach (var deviceKeyName in pciKey.GetSubKeyNames())
                {
                    using var devKey = pciKey.OpenSubKey(deviceKeyName);
                    if (devKey == null) continue;

                    foreach (var instanceName in devKey.GetSubKeyNames())
                    {
                        using var instanceKey = devKey.OpenSubKey(instanceName);
                        if (instanceKey == null) continue;

                        var desc = instanceKey.GetValue("DeviceDesc")?.ToString() ?? string.Empty;
                        var classGuid = instanceKey.GetValue("ClassGUID")?.ToString() ?? string.Empty;

                        // Интересуют классы: Display (GPU), Net (Сеть), MEDIA (Звук), USB
                        bool isTarget = classGuid.Equals("{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase) || // GPU
                                        classGuid.Equals("{4d36e972-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase) || // Net
                                        classGuid.Equals("{4d36e96c-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase) || // Audio
                                        classGuid.Equals("{36fc9e60-c465-11cf-8056-444553540000}", StringComparison.OrdinalIgnoreCase);   // USB

                        if (!isTarget) continue;

                        string msiPath = $@"SYSTEM\CurrentControlSet\Enum\PCI\{deviceKeyName}\{instanceName}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                        string affinityPath = $@"SYSTEM\CurrentControlSet\Enum\PCI\{deviceKeyName}\{instanceName}\Device Parameters\Interrupt Management\Affinity Policy";

                        try
                        {
                            using var msiReg = Registry.LocalMachine.CreateSubKey(msiPath);
                            if (msiReg != null)
                            {
                                msiReg.SetValue("MSISupported", 1, RegistryValueKind.DWord);

                                using var affReg = Registry.LocalMachine.CreateSubKey(affinityPath);
                                // 1 = Low, 2 = Normal, 3 = High
                                affReg?.SetValue("DevicePriority", 3, RegistryValueKind.DWord);

                                configured++;
                                string cleanName = desc.Split(';')[^1];
                                log?.Invoke($"[MSI Mode] Включен режим прерываний MSI для: {cleanName}", LogLevel.Success);
                            }
                        }
                        catch { }
                    }
                }

                return (configured, $"Успешно переведено устройств в MSI Mode: {configured}");
            }
            catch (Exception ex)
            {
                return (0, $"Ошибка настройки MSI: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Настраивает системные таймеры ядра bcdedit (Disabledynamictick, TscSyncPolicy, Useplatformtick)
    /// </summary>
    public static async Task ConfigureKernelTimersAsync(bool enableLowLatency, Action<string, LogLevel>? log = null)
    {
        await Task.Run(() =>
        {
            try
            {
                if (enableLowLatency)
                {
                    // 1. Отключаем динамический тик Windows (устраняет микрофризы планировщика при смене частот)
                    RunBcdCommand("/set disabledynamictick yes");
                    // 2. Включаем принудительную синхронизацию таймера процессора TSC Enhanced
                    RunBcdCommand("/set tscsyncpolicy Enhanced");
                    // 3. Отключаем принудительный медленный опрос HPET через шину, опираясь на высокоскоростной инвариантный TSC процессора
                    RunBcdCommand("/deletevalue useplatformclock");

                    log?.Invoke("Таймеры ядра оптимизированы: Dynamic Tick отключен, TSC Sync установлен в Enhanced.", LogLevel.Success);
                }
                else
                {
                    RunBcdCommand("/set disabledynamictick no");
                    RunBcdCommand("/deletevalue tscsyncpolicy");
                    log?.Invoke("Настройки таймеров ядра возвращены к значениям по умолчанию Windows.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Ошибка настройки таймеров: {ex.Message}", LogLevel.Error);
            }
        });
    }

    private static void RunBcdCommand(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(1500);
        }
        catch { }
    }
}