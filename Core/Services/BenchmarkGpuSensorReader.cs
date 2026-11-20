using LibreHardwareMonitor.Hardware;

namespace HeroTweaker.Core.Services;

/// <summary>Reads optional GPU sensors through LibreHardwareMonitor without requiring a particular vendor SDK.</summary>
internal sealed class BenchmarkGpuSensorReader : IDisposable
{
    private readonly Computer _computer = new() { IsGpuEnabled = true };
    private readonly string _adapterName;
    private bool _opened;

    public BenchmarkGpuSensorReader(string? adapterName)
    {
        _adapterName = adapterName ?? string.Empty;
        try
        {
            _computer.Open();
            _opened = true;
        }
        catch { }
    }

    public GpuSensorReading Read()
    {
        if (!_opened)
            return GpuSensorReading.Unavailable("Сенсоры не открылись; проверьте драйвер и права доступа.");

        try
        {
            var gpuHardware = Enumerate(_computer.Hardware)
                .Where(hardware => hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
                .ToArray();
            if (gpuHardware.Length == 0)
                return GpuSensorReading.Unavailable("LHM не обнаружил GPU с доступными датчиками.");

            var selected = SelectAdapter(gpuHardware);
            if (selected == null)
                return GpuSensorReading.Unavailable("Не удалось сопоставить датчики с выбранной видеокартой.");

            UpdateTree(selected);
            var sensors = EnumerateSensors(selected).Where(sensor => sensor.Value.HasValue).ToArray();
            double? temperature = Find(sensors, SensorType.Temperature, name =>
                !ContainsAny(name, "hot spot", "hotspot", "junction", "memory", "vram"), "gpu core", "gpu package", "gpu");
            double? hotspot = Find(sensors, SensorType.Temperature, name => ContainsAny(name, "hot spot", "hotspot", "junction"));
            double? memoryTemperature = Find(sensors, SensorType.Temperature, name => ContainsAny(name, "memory", "vram"));
            double? power = Find(sensors, SensorType.Power, _ => true, "gpu", "total board power");
            double? clock = Find(sensors, SensorType.Clock, _ => true, "gpu core", "gpu");
            double? fan = Find(sensors, SensorType.Fan, _ => true, "gpu");
            double? load = Find(sensors, SensorType.Load, name => !ContainsAny(name, "memory", "video", "copy"), "gpu core", "gpu");

            var hasSensor = temperature.HasValue || hotspot.HasValue || memoryTemperature.HasValue || power.HasValue || clock.HasValue || fan.HasValue || load.HasValue;
            return hasSensor
                ? new GpuSensorReading(temperature, hotspot, memoryTemperature, power, clock, fan, load,
                    $"Датчики: {selected.Name} (LibreHardwareMonitor). Отсутствующие метрики драйвер не предоставил.")
                : GpuSensorReading.Unavailable("GPU найден, но драйвер не отдал показания датчиков.");
        }
        catch (Exception ex)
        {
            return GpuSensorReading.Unavailable($"Ошибка чтения датчиков: {ex.Message}");
        }
    }

    private IHardware? SelectAdapter(IHardware[] hardware)
    {
        if (string.IsNullOrWhiteSpace(_adapterName)) return hardware.FirstOrDefault();
        var exact = hardware.FirstOrDefault(item => string.Equals(item.Name, _adapterName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return hardware
            .Select(item => (Hardware: item, Score: Similarity(item.Name, _adapterName)))
            .Where(item => item.Score >= 4)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Hardware)
            .FirstOrDefault();
    }

    private static int Similarity(string sensorName, string adapterName)
    {
        var normalizedSensor = Normalize(sensorName);
        var normalizedAdapter = Normalize(adapterName);
        if (normalizedSensor.Contains(normalizedAdapter, StringComparison.OrdinalIgnoreCase) || normalizedAdapter.Contains(normalizedSensor, StringComparison.OrdinalIgnoreCase)) return 10;
        var adapterTokens = normalizedAdapter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return adapterTokens.Count(token => token.Length >= 3 && normalizedSensor.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static double? Find(ISensor[] sensors, SensorType type, Func<string, bool> predicate, params string[] preferredNames)
    {
        var candidates = sensors.Where(sensor => sensor.SensorType == type && predicate(sensor.Name)).ToArray();
        foreach (var preferred in preferredNames)
        {
            var sensor = candidates.FirstOrDefault(item => item.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (sensor?.Value is float value && float.IsFinite(value)) return value;
        }
        return candidates.Select(sensor => sensor.Value).FirstOrDefault(value => value.HasValue && float.IsFinite(value.Value));
    }

    private static IEnumerable<IHardware> Enumerate(IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;
            foreach (var subItem in Enumerate(item.SubHardware)) yield return subItem;
        }
    }

    private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors) yield return sensor;
        foreach (var subHardware in hardware.SubHardware)
        foreach (var sensor in EnumerateSensors(subHardware)) yield return sensor;
    }

    private static void UpdateTree(IHardware hardware)
    {
        hardware.Update();
        foreach (var child in hardware.SubHardware) UpdateTree(child);
    }

    private static bool ContainsAny(string value, params string[] needles) => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (!_opened) return;
        try { _computer.Close(); } catch { }
        _opened = false;
    }
}

internal sealed record GpuSensorReading(
    double? TemperatureC,
    double? HotspotTemperatureC,
    double? MemoryTemperatureC,
    double? PowerWatts,
    double? ClockMhz,
    double? FanPercent,
    double? LoadPercent,
    string Status)
{
    public static GpuSensorReading Unavailable(string status) => new(null, null, null, null, null, null, null, status);
}
