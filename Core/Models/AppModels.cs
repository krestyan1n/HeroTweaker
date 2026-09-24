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
}