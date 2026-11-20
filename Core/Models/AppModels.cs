using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public class CatalogAppModel : ObservableObject
{
    public string WingetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "📦";
    public string IconUrl { get; set; } = string.Empty;

    private bool _isInstalling;
    public bool IsInstalling
    {
        get => _isInstalling;
        set => SetField(ref _isInstalling, value);
    }
}

public class InstalledProgramModel : ObservableObject
{
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string EstimatedSize { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string RegistryKeyPath { get; set; } = string.Empty;
    public bool SupportsQuietUninstall => !string.IsNullOrWhiteSpace(QuietUninstallString);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    private string _removalStatus = "Не выбрано";
    public string RemovalStatus
    {
        get => _removalStatus;
        set => SetField(ref _removalStatus, value);
    }
}

public sealed class AppLeftoverModel : ObservableObject
{
    public string ProgramName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}
