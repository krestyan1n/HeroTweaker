using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Interfaces;

namespace HeroTweaker.Core.Models;

public class AppxTweak : ITweak
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Category => "Приложения";

    private readonly string _packagePattern;

    /// <param name="packagePattern">Шаблон имени пакета, например "Microsoft.BingWeather"</param>
    public AppxTweak(string id, string name, string description, string packagePattern)
    {
        Id = id;
        Name = name;
        Description = description;
        _packagePattern = packagePattern;
    }

    /// <summary>
    /// Проверяем наличие пакета в репозитории AppModel реестра (работает мгновенно без PowerShell)
    /// </summary>
    public bool IsApplied()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");

            if (key == null) return false;

            // Если пакет найден в установленных у пользователя — значит твик НЕ применен (приложение еще на месте)
            bool isInstalled = key.GetSubKeyNames().Any(name => name.Contains(_packagePattern, StringComparison.OrdinalIgnoreCase));

            return !isInstalled; // IsApplied = true, когда приложение успешно вырезано
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Удаление приложения для текущего пользователя и из системных предустановок
    /// </summary>
    public Task ApplyAsync() => Task.Run(() =>
    {
        string script =
            $"Get-AppxPackage -Name '*{_packagePattern}*' | Remove-AppxPackage -ErrorAction SilentlyContinue; " +
            $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -like '*{_packagePattern}*' }} | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";

        ExecutePowerShell(script);
    });

    /// <summary>
    /// Восстановление манифеста приложения из защищенной системной папки WindowsApps
    /// </summary>
    public Task RollbackAsync() => Task.Run(() =>
    {
        string script =
            $"Get-AppxPackage -AllUsers '*{_packagePattern}*' | Foreach {{ Add-AppxPackage -DisableDevelopmentMode -Register \"$($_.InstallLocation)\\AppXManifest.xml\" -ErrorAction SilentlyContinue }}";

        ExecutePowerShell(script);
    });

    private static void ExecutePowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        process?.WaitForExit();
    }
}