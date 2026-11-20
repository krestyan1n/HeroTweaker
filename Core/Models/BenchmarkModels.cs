namespace HeroTweaker.Core.Models;

public sealed class FrameTimeBenchmarkResult
{
    public string SourceFileName { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public double AverageFps { get; init; }
    public double OnePercentLowFps { get; init; }
    public double PointOnePercentLowFps { get; init; }
    public double AverageFrameTimeMs { get; init; }
    public double P99FrameTimeMs { get; init; }
    public DateTime CapturedAtLocal { get; init; } = DateTime.Now;
    public string? ExecutableName { get; init; }
    public string? TelemetryFilePath { get; init; }
    public double? AverageGameCpuPercent { get; init; }
    public double? AverageGameWorkingSetMb { get; init; }
    public double? AverageSystemCpuPercent { get; init; }
    public double? AverageSystemMemoryPercent { get; init; }
    public double? AverageSystemUsedMemoryGb { get; init; }
    public double? AverageGameGpuBusyMs { get; init; }
    public double? AverageGameGpuEnginePercent { get; init; }
    public double? AverageSystemGpuBusyPercent { get; init; }
    public double? AverageGameDedicatedGpuMemoryMb { get; init; }
    public double? AverageGameSharedGpuMemoryMb { get; init; }
    public double? AverageGpuTemperatureC { get; init; }
    public double? AverageGpuHotspotTemperatureC { get; init; }
    public double? AverageGpuMemoryTemperatureC { get; init; }
    public double? AverageGpuPowerWatts { get; init; }
    public double? AverageGpuClockMhz { get; init; }
    public double? AverageGpuFanPercent { get; init; }
    public double? AverageGpuLoadPercent { get; init; }
    public int TelemetrySampleCount { get; init; }
    public string AverageFpsText => $"{AverageFps:F1} FPS";
    public string OnePercentLowText => $"{OnePercentLowFps:F1} FPS";
    public string PointOnePercentLowText => $"{PointOnePercentLowFps:F1} FPS";
    public string AverageFrameTimeText => $"{AverageFrameTimeMs:F2} мс";
    public string P99FrameTimeText => $"{P99FrameTimeMs:F2} мс";
}

public sealed class BenchmarkTelemetrySample
{
    public DateTime TimestampLocal { get; init; }
    public int? ProcessId { get; init; }
    public double? GameCpuPercent { get; init; }
    public double? GameWorkingSetMb { get; init; }
    public double? GamePrivateMemoryMb { get; init; }
    public double? SystemCpuPercent { get; init; }
    public double? SystemMemoryPercent { get; init; }
    public double? SystemUsedMemoryGb { get; init; }
    public double? GameGpuEnginePercent { get; init; }
    public double? SystemGpuBusyPercent { get; init; }
    public double? GameDedicatedGpuMemoryMb { get; init; }
    public double? GameSharedGpuMemoryMb { get; init; }
    public double? GpuTemperatureC { get; init; }
    public double? GpuHotspotTemperatureC { get; init; }
    public double? GpuMemoryTemperatureC { get; init; }
    public double? GpuPowerWatts { get; init; }
    public double? GpuClockMhz { get; init; }
    public double? GpuFanPercent { get; init; }
    public double? GpuLoadPercent { get; init; }
    public string GpuSensorStatus { get; init; } = "Датчики не обнаружены или недоступны";
}
