using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public enum StartupImpact
{
    Low,
    Medium,
    High
}

public class StartupItemModel : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Publisher { get; set; } = "Неизвестно";
    public string Location { get; set; } = string.Empty;
    public string RegistryKeyPath { get; set; } = string.Empty;
    public bool IsRegistry { get; set; }
    public bool IsCurrentUser { get; set; }
    public string ShortcutFilePath { get; set; } = string.Empty;

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public StartupImpact Impact { get; set; } = StartupImpact.Low;

    public string ImpactText => Impact switch
    {
        StartupImpact.High => "ВЫСОКОЕ ВЛИЯНИЕ",
        StartupImpact.Medium => "СРЕДНЕЕ ВЛИЯНИЕ",
        _ => "НИЗКОЕ ВЛИЯНИЕ"
    };

    public string ImpactColorHex => Impact switch
    {
        StartupImpact.High => "#EF4444",
        StartupImpact.Medium => "#F59E0B",
        _ => "#10B981"
    };
}