using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

/// <summary>
/// Reads the display-driver inventory exposed by Windows. It intentionally does not alter
/// undocumented vendor driver switches: the audit is a safe basis for later ETW diagnostics.
/// </summary>
public static class GpuAuditService
{
    private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string UserGpuPreferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static Task<GpuAuditSnapshot> CollectAsync() => Task.Run(Collect);

    public static (bool HasAmd, bool HasNvidia) DetectSupportedVendors()
    {
        var adapters = ReadDisplayAdapters();
        return (
            adapters.Any(adapter => adapter.Vendor == GpuVendor.Amd),
            adapters.Any(adapter => adapter.Vendor == GpuVendor.Nvidia));
    }

    public static IReadOnlyList<GpuGameProfile> GetHighPerformanceProfiles()
    {
        var profiles = new List<GpuGameProfile>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesPath);
            if (key == null) return profiles;

            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name)?.ToString() ?? string.Empty;
                if (value.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase))
                    profiles.Add(new GpuGameProfile { ExecutablePath = name });
            }
        }
        catch { }

        return profiles.OrderBy(profile => profile.ExecutableName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void SetHighPerformanceProfile(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("Не указан путь к игре.", nameof(executablePath));
        using var key = Registry.CurrentUser.CreateSubKey(UserGpuPreferencesPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть настройки GPU Windows.");
        key.SetValue(executablePath, "GpuPreference=2;", RegistryValueKind.String);
    }

    public static void RemoveProfile(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesPath, writable: true);
        key?.DeleteValue(executablePath, throwOnMissingValue: false);
    }

    private static GpuAuditSnapshot Collect()
    {
        var adapters = ReadDisplayAdapters();
        var checks = new List<GpuAuditCheck>();
        var windowsChecks = new List<GpuAuditCheck>();

        var hags = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        checks.Add(hags switch
        {
            2 => new GpuAuditCheck("Hardware-Accelerated GPU Scheduling", "Включено", "Планирование GPU передано поддерживаемому драйверу WDDM. Эффект зависит от игры и версии драйвера.", GpuAuditSeverity.Good),
            1 => new GpuAuditCheck("Hardware-Accelerated GPU Scheduling", "Отключено", "Windows использует стандартный путь планирования. Это не ошибка; сравнивать стоит только в одной игре и на одной сцене.", GpuAuditSeverity.Info),
            _ => new GpuAuditCheck("Hardware-Accelerated GPU Scheduling", "Автоматически / недоступно", "Windows или драйвер не сообщили явный режим. HeroTweaker не включает неподдерживаемые режимы принудительно.", GpuAuditSeverity.Info)
        });

        windowsChecks.Add(hags switch
        {
            2 => new GpuAuditCheck("HAGS", "Включён", "HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers\\HwSchMode = 2. Windows использует аппаратное планирование GPU.", GpuAuditSeverity.Good),
            1 => new GpuAuditCheck("HAGS", "Отключён", "HwSchMode = 1. Это не ошибка: эффект HAGS зависит от игры и драйвера; сравнивайте только одним бенчмарком.", GpuAuditSeverity.Info),
            _ => new GpuAuditCheck("HAGS", "Не задан явно", "Windows не сообщила принудительный режим HwSchMode. HeroTweaker не подменяет его без измерения.", GpuAuditSeverity.Info)
        });

        var gameMode = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        windowsChecks.Add(gameMode switch
        {
            1 => new GpuAuditCheck("Game Mode", "Включён", "HKCU\\Software\\Microsoft\\GameBar\\AutoGameModeEnabled = 1.", GpuAuditSeverity.Good),
            0 => new GpuAuditCheck("Game Mode", "Отключён", "AutoGameModeEnabled = 0. Это может быть осознанной настройкой; HeroTweaker не включает режим автоматически.", GpuAuditSeverity.Info),
            _ => new GpuAuditCheck("Game Mode", "Настройка не задана", "В профиле пользователя нет явного значения AutoGameModeEnabled.", GpuAuditSeverity.Info)
        });

        var gameDvr = ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
        var appCapture = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        var captureEnabled = gameDvr == 1 || appCapture == 1;
        windowsChecks.Add(captureEnabled
            ? new GpuAuditCheck("Фоновый захват Game DVR", "Активен", "Обнаружено включённое значение GameDVR_Enabled или AppCaptureEnabled. Захват может влиять на frametime в отдельных играх.", GpuAuditSeverity.Warning)
            : new GpuAuditCheck("Фоновый захват Game DVR", gameDvr == 0 || appCapture == 0 ? "Отключён" : "Не задан явно", gameDvr == 0 || appCapture == 0 ? "Windows не должна выполнять фоновый захват по найденным ключам пользователя." : "Явные значения Game DVR не найдены; HeroTweaker не делает вывод о фактическом захвате.", GpuAuditSeverity.Info));

        var tdrDelay = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay");
        windowsChecks.Add(tdrDelay.HasValue
            ? new GpuAuditCheck("TDR watchdog", $"Пользовательский: {tdrDelay} c", "Найдено явное TdrDelay. Нестандартное значение может маскировать зависания драйвера; HeroTweaker не меняет TDR автоматически.", GpuAuditSeverity.Warning)
            : new GpuAuditCheck("TDR watchdog", "Системное значение", "Явный TdrDelay не найден — Windows использует своё стандартное поведение watchdog.", GpuAuditSeverity.Good));

        var discrete = adapters.Where(adapter => adapter.IsDiscrete).ToList();
        checks.Add(discrete.Count switch
        {
            0 => new GpuAuditCheck("Маршрут рендеринга", "Требует проверки", "Дискретный адаптер не найден в реестре драйверов. Для ноутбука это может быть штатная гибридная конфигурация.", GpuAuditSeverity.Warning),
            1 => new GpuAuditCheck("Маршрут рендеринга", "Один дискретный GPU", "Для игр HeroTweaker сможет назначать профиль высокой производительности только на уровне Windows, без фоновой службы.", GpuAuditSeverity.Good),
            _ => new GpuAuditCheck("Маршрут рендеринга", $"{discrete.Count} дискретных GPU", "Мульти-GPU обнаружен. Перед будущими профилями игры нужно будет явно выбрать адаптер.", GpuAuditSeverity.Info)
        });

        bool hasAmd = adapters.Any(adapter => adapter.Vendor == GpuVendor.Amd);
        bool hasNvidia = adapters.Any(adapter => adapter.Vendor == GpuVendor.Nvidia);
        if (hasAmd || hasNvidia)
        {
            checks.Add(new GpuAuditCheck(
                "Вендорный модуль",
                hasAmd && hasNvidia ? "AMD + NVIDIA" : hasAmd ? "AMD" : "NVIDIA",
                "Отображаются только вкладки обнаруженных в системе вендоров. Управление разгоном и скрытыми недокументированными флагами не включается автоматически.",
                GpuAuditSeverity.Good));
        }

        return new GpuAuditSnapshot { Adapters = adapters, Checks = checks, WindowsChecks = windowsChecks };
    }

    private static List<GpuAdapterInfo> ReadDisplayAdapters()
    {
        var result = new List<GpuAdapterInfo>();
        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (displayClass == null) return result;

            // GetSubKeyNames can stall on a protected driver key on some Windows builds.
            // Display miniport entries use four-digit keys, so probing the bounded range is
            // deterministic and lets one inaccessible entry not break GPU discovery.
            for (var index = 0; index < 128; index++)
            {
                var subKeyName = index.ToString("D4", CultureInfo.InvariantCulture);
                try
                {
                    using var key = displayClass.OpenSubKey(subKeyName);
                    if (key == null) continue;

                    var name = key.GetValue("DriverDesc")?.ToString()?.Trim();
                    var hardwareId = key.GetValue("MatchingDeviceId")?.ToString()
                        ?? key.GetValue("HardwareID")?.ToString()
                        ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(hardwareId)) continue;
                    var vendor = DetectVendor($"{name} {hardwareId}");
                    if (vendor == GpuVendor.Other && !LooksLikeDisplayAdapter(name, hardwareId)) continue;

                    var dedicatedMemory = ReadUnsignedValue(key.GetValue("HardwareInformation.MemorySize"));
                    result.Add(new GpuAdapterInfo
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "Видеоадаптер Windows" : name,
                        Vendor = vendor,
                        VendorName = GetVendorName(vendor),
                        DriverVersion = key.GetValue("DriverVersion")?.ToString() ?? "Не определена",
                        DriverDate = FormatDriverDate(key.GetValue("DriverDate")?.ToString()),
                        PnpDeviceId = hardwareId,
                        DedicatedMemoryBytes = dedicatedMemory,
                        IsDiscrete = vendor is GpuVendor.Amd or GpuVendor.Nvidia
                    });
                }
                catch (System.Security.SecurityException)
                {
                    // Another driver key is protected; continue with accessible adapters.
                }
                catch (UnauthorizedAccessException)
                {
                    // Another driver key is protected; continue with accessible adapters.
                }
            }
        }
        catch { }

        return result
            .OrderByDescending(adapter => adapter.IsDiscrete)
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeDisplayAdapter(string? name, string hardwareId) =>
        hardwareId.Contains("VEN_", StringComparison.OrdinalIgnoreCase) ||
        (name?.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (name?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (name?.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ?? false);

    private static GpuVendor DetectVendor(string value)
    {
        if (value.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || value.Contains("AMD", StringComparison.OrdinalIgnoreCase) || value.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (value.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || value.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (value.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || value.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        if (value.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Microsoft;
        return GpuVendor.Other;
    }

    private static string GetVendorName(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Amd => "AMD",
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Intel => "Intel",
        GpuVendor.Microsoft => "Microsoft",
        _ => "Неизвестный вендор"
    };

    private static int? ReadDword(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch { return null; }
    }

    private static ulong ReadUnsignedValue(object? value) => value switch
    {
        int intValue when intValue > 0 => (ulong)intValue,
        long longValue when longValue > 0 => (ulong)longValue,
        uint uintValue => uintValue,
        ulong ulongValue => ulongValue,
        _ => 0
    };

    private static string FormatDriverDate(string? raw)
    {
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(raw) ? "Не определена" : raw;
    }

}
