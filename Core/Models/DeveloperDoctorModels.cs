using System.Collections.Generic;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public enum DevComponentStatus
{
    Healthy,   // Установлен и корректно настроен в PATH
    Warning,   // Установлен, но обнаружены конфликты/старая версия/не в PATH
    Missing    // Не обнаружен в системе
}

public class DevEnvironmentComponent : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "⚙️";

    private DevComponentStatus _status = DevComponentStatus.Missing;
    public DevComponentStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColorHex));
            }
        }
    }

    private string _version = "Не найдено";
    public string Version { get => _version; set => SetField(ref _version, value); }

    private string _executablePath = string.Empty;
    public string ExecutablePath { get => _executablePath; set => SetField(ref _executablePath, value); }

    private string _details = string.Empty;
    public string Details { get => _details; set => SetField(ref _details, value); }

    public string StatusText => Status switch
    {
        DevComponentStatus.Healthy => "ГОТОВ",
        DevComponentStatus.Warning => "ВНИМАНИЕ",
        DevComponentStatus.Missing => "НЕ УСТАНОВЛЕН",
        _ => "НЕИЗВЕСТНО"
    };

    public string StatusColorHex => Status switch
    {
        DevComponentStatus.Healthy => "#10B981",
        DevComponentStatus.Warning => "#F59E0B",
        DevComponentStatus.Missing => "#64748B",
        _ => "#64748B"
    };
}

public class PathEntryItem : ObservableObject
{
    public string Path { get; set; } = string.Empty;
    public bool IsUserPath { get; set; } // true = User, false = System
    public bool ExistsOnDisk { get; set; }
    public bool IsDuplicate { get; set; }

    public string TypeText => IsUserPath ? "USER" : "SYSTEM";
    public string StatusText => !ExistsOnDisk ? "НЕ СУЩЕСТВУЕТ" : (IsDuplicate ? "ДУБЛИКАТ" : "OK");
    public string StatusColorHex => !ExistsOnDisk ? "#EF4444" : (IsDuplicate ? "#F59E0B" : "#10B981");
}

public class CommandResolutionResult : ObservableObject
{
    public string Command { get; set; } = string.Empty;
    public string ResolvedExecutablePath { get; set; } = string.Empty;
    public string VersionOutput { get; set; } = string.Empty;
    public bool Found { get; set; }
    public List<string> AllMatchingPaths { get; set; } = new();
}