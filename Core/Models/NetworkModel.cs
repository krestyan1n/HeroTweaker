using System.Net.NetworkInformation;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public class DnsServerItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PrimaryIp { get; set; } = string.Empty;
    public string SecondaryIp { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "🌐";

    private long _pingMs = -1;
    public long PingMs
    {
        get => _pingMs;
        set
        {
            if (SetField(ref _pingMs, value))
            {
                OnPropertyChanged(nameof(PingText));
                OnPropertyChanged(nameof(PingColorHex));
            }
        }
    }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetField(ref _isCurrent, value);
    }

    private bool _isFastest;
    public bool IsFastest
    {
        get => _isFastest;
        set => SetField(ref _isFastest, value);
    }

    public string PingText => PingMs switch
    {
        < 0 => "Ожидание теста...",
        >= 9000 => "Таймаут",
        _ => $"{PingMs} мс"
    };

    public string PingColorHex => PingMs switch
    {
        < 0 => "#64748B",
        < 20 => "#10B981",
        < 45 => "#38BDF8",
        < 80 => "#FBBF24",
        _ => "#F87171"
    };
}

public class NetworkAdapterInfo : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IpAddress { get; set; } = "0.0.0.0";
    public string CurrentDns { get; set; } = "DHCP (Автоматически)";
    public bool IsUp { get; set; }
    public NetworkInterfaceType InterfaceType { get; set; }

    public string DisplayTitle => $"{Name} ({Description})";
}