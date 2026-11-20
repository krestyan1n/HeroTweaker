using System;
using System.Collections.Generic;
using System.Linq;

namespace HeroTweaker.Core.Models;

public enum GpuVendor
{
    Amd,
    Nvidia,
    Intel,
    Microsoft,
    Other
}

public sealed class GpuAdapterInfo
{
    public string Name { get; init; } = "Неизвестный адаптер";
    public GpuVendor Vendor { get; init; }
    public string VendorName { get; init; } = "Неизвестный вендор";
    public string DriverVersion { get; init; } = "Не определена";
    public string DriverDate { get; init; } = "Не определена";
    public string PnpDeviceId { get; init; } = string.Empty;
    public ulong DedicatedMemoryBytes { get; init; }
    public bool IsDiscrete { get; init; }

    public string DedicatedMemoryFormatted => DedicatedMemoryBytes == 0
        ? "VRAM не определена"
        : $"{DedicatedMemoryBytes / 1024d / 1024d / 1024d:F1} ГБ";

    public string AdapterKind => IsDiscrete ? "Дискретная видеокарта" : "Встроенная / системная графика";
    public string HardwareIdShort => string.IsNullOrWhiteSpace(PnpDeviceId)
        ? "PCI ID недоступен"
        : PnpDeviceId.Replace("PCI\\", string.Empty, StringComparison.OrdinalIgnoreCase);
}

public sealed record GpuAuditCheck(string Title, string Value, string Details, GpuAuditSeverity Severity);

public enum GpuAuditSeverity
{
    Good,
    Info,
    Warning
}

public sealed class GpuGameProfile
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string ExecutableName => System.IO.Path.GetFileNameWithoutExtension(ExecutablePath);
    public string PreferenceText { get; init; } = "Высокая производительность";
}

public sealed class GpuAuditSnapshot
{
    public IReadOnlyList<GpuAdapterInfo> Adapters { get; init; } = Array.Empty<GpuAdapterInfo>();
    public IReadOnlyList<GpuAuditCheck> Checks { get; init; } = Array.Empty<GpuAuditCheck>();
    public IReadOnlyList<GpuAuditCheck> WindowsChecks { get; init; } = Array.Empty<GpuAuditCheck>();
    public bool HasAmd => Adapters.Any(adapter => adapter.Vendor == GpuVendor.Amd);
    public bool HasNvidia => Adapters.Any(adapter => adapter.Vendor == GpuVendor.Nvidia);
}
