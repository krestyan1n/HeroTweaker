using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Text.RegularExpressions;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

/// <summary>Imports measured PresentMon frame-time data; it never estimates FPS.</summary>
public static class FrameTimeBenchmarkService
{
    private static readonly string[] FrameTimeColumns = ["MsBetweenPresents", "MsBetweenDisplayChange", "MsBetweenDisplayChange_", "CPUFrameTime"];
    private static readonly string[] GpuBusyColumns = ["MsGPUBusy"];
    private static readonly object PresentMonLock = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(MemoryStatus buffer);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle, out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);

    public static string? FindPresentMon()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker", "Tools", "PresentMon-x64.exe"),
            Path.Combine(AppContext.BaseDirectory, "PresentMon-x64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "CNext", "CNext", "PresentMon-x64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon", "PresentMon.exe")
        };
        var installed = candidates.FirstOrDefault(File.Exists);
        if (installed != null) return installed;

        lock (PresentMonLock)
        {
            var bundledPath = candidates[0];
            if (File.Exists(bundledPath)) return bundledPath;
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("HeroTweaker.PresentMon-x64.exe");
            if (resource == null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
            var temporaryPath = bundledPath + ".tmp";
            using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                resource.CopyTo(output);
            File.Move(temporaryPath, bundledPath, overwrite: true);
            return bundledPath;
        }
    }

    public static async Task<FrameTimeBenchmarkResult> CaptureAsync(string executablePath, string outputPath, int seconds, string? gpuAdapterName = null)
    {
        var presentMon = FindPresentMon() ?? throw new FileNotFoundException("PresentMon не найден. Установите PresentMon или используйте CSV импорт.");
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName)) throw new ArgumentException("Не удалось определить имя процесса игры.", nameof(executablePath));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath)) File.Delete(outputPath);
        using var capture = new Process
        {
            StartInfo = new ProcessStartInfo { FileName = presentMon, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }
        };
        capture.StartInfo.ArgumentList.Add("--process_name");
        capture.StartInfo.ArgumentList.Add(processName);
        capture.StartInfo.ArgumentList.Add("--output_file");
        capture.StartInfo.ArgumentList.Add(outputPath);
        capture.StartInfo.ArgumentList.Add("--timed");
        capture.StartInfo.ArgumentList.Add(Math.Clamp(seconds, 15, 600).ToString(CultureInfo.InvariantCulture));
        capture.StartInfo.ArgumentList.Add("--terminate_after_timed");
        capture.StartInfo.ArgumentList.Add("--no_console_stats");
        capture.StartInfo.ArgumentList.Add("--v1_metrics");
        if (!capture.Start()) throw new InvalidOperationException("Не удалось запустить PresentMon.");
        var errorTask = capture.StandardError.ReadToEndAsync();
        using var telemetryCancellation = new CancellationTokenSource();
        var telemetryPath = Path.ChangeExtension(outputPath, ".telemetry.json");
        var telemetryTask = CollectTelemetryAsync(processName, gpuAdapterName, telemetryPath, telemetryCancellation.Token);
        await capture.WaitForExitAsync();
        if (capture.ExitCode != 0)
        {
            telemetryCancellation.Cancel();
            try { await telemetryTask; } catch (OperationCanceledException) { }
            var error = await errorTask;
            if (error.Contains("access denied", StringComparison.OrdinalIgnoreCase) || error.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Windows запретила ETW-захват. Запустите HeroTweaker от администратора или добавьте пользователя в группу «Performance Log Users» и войдите в Windows заново.");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"PresentMon завершился с кодом {capture.ExitCode}." : error.Trim());
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            telemetryCancellation.Cancel();
            try { await telemetryTask; } catch (OperationCanceledException) { }
            throw new InvalidDataException("PresentMon не записал кадры. Убедитесь, что игра запущена, и повторите замер в сцене с отрисовкой.");
        }
        telemetryCancellation.Cancel();
        var telemetry = await telemetryTask;
        var imported = ImportPresentMonCsv(outputPath);
        var gpuBusy = ReadAverageGpuBusy(outputPath);
        var enriched = new FrameTimeBenchmarkResult
        {
            SourceFileName = imported.SourceFileName,
            SampleCount = imported.SampleCount,
            AverageFps = imported.AverageFps,
            OnePercentLowFps = imported.OnePercentLowFps,
            PointOnePercentLowFps = imported.PointOnePercentLowFps,
            AverageFrameTimeMs = imported.AverageFrameTimeMs,
            P99FrameTimeMs = imported.P99FrameTimeMs,
            ExecutableName = processName,
            TelemetryFilePath = telemetryPath,
            AverageGameCpuPercent = Average(telemetry.Select(sample => sample.GameCpuPercent)),
            AverageGameWorkingSetMb = Average(telemetry.Select(sample => sample.GameWorkingSetMb)),
            AverageSystemCpuPercent = Average(telemetry.Select(sample => sample.SystemCpuPercent)),
            AverageSystemMemoryPercent = Average(telemetry.Select(sample => sample.SystemMemoryPercent)),
            AverageSystemUsedMemoryGb = Average(telemetry.Select(sample => sample.SystemUsedMemoryGb)),
            AverageGameGpuBusyMs = gpuBusy,
            AverageGameGpuEnginePercent = Average(telemetry.Select(sample => sample.GameGpuEnginePercent)),
            AverageSystemGpuBusyPercent = Average(telemetry.Select(sample => sample.SystemGpuBusyPercent)),
            AverageGameDedicatedGpuMemoryMb = Average(telemetry.Select(sample => sample.GameDedicatedGpuMemoryMb)),
            AverageGameSharedGpuMemoryMb = Average(telemetry.Select(sample => sample.GameSharedGpuMemoryMb)),
            AverageGpuTemperatureC = Average(telemetry.Select(sample => sample.GpuTemperatureC)),
            AverageGpuHotspotTemperatureC = Average(telemetry.Select(sample => sample.GpuHotspotTemperatureC)),
            AverageGpuMemoryTemperatureC = Average(telemetry.Select(sample => sample.GpuMemoryTemperatureC)),
            AverageGpuPowerWatts = Average(telemetry.Select(sample => sample.GpuPowerWatts)),
            AverageGpuClockMhz = Average(telemetry.Select(sample => sample.GpuClockMhz)),
            AverageGpuFanPercent = Average(telemetry.Select(sample => sample.GpuFanPercent)),
            AverageGpuLoadPercent = Average(telemetry.Select(sample => sample.GpuLoadPercent)),
            TelemetrySampleCount = telemetry.Count
        };
        return enriched;
    }

    private static async Task<List<BenchmarkTelemetrySample>> CollectTelemetryAsync(string processName, string? gpuAdapterName, string outputPath, CancellationToken cancellationToken)
    {
        var samples = new List<BenchmarkTelemetrySample>();
        var previousProcessTimes = new Dictionary<int, (TimeSpan Cpu, long Tick)>();
        GetSystemTimes(out var previousIdle, out var previousKernel, out var previousUser);
        using var gpuCounters = new WindowsGpuCounterReader();
        using var gpuSensors = new BenchmarkGpuSensorReader(gpuAdapterName);
        while (true)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            double? systemCpu = null;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var idleDelta = FileTime(idle) - FileTime(previousIdle);
                var kernelDelta = FileTime(kernel) - FileTime(previousKernel);
                var userDelta = FileTime(user) - FileTime(previousUser);
                var total = kernelDelta + userDelta;
                if (total > 0) systemCpu = Math.Clamp(100d * (total - idleDelta) / total, 0, 100);
                previousIdle = idle; previousKernel = kernel; previousUser = user;
            }

            double? memoryPercent = null, usedMemoryGb = null;
            var memory = new MemoryStatus();
            if (GlobalMemoryStatusEx(memory))
            {
                memoryPercent = memory.MemoryLoad;
                usedMemoryGb = (memory.TotalPhysical - memory.AvailablePhysical) / 1024d / 1024d / 1024d;
            }

            int? pid = null;
            double? gameCpu = null, workingSet = null, privateMemory = null;
            try
            {
                var candidate = Process.GetProcessesByName(processName)
                    .Where(process => !process.HasExited)
                    .OrderByDescending(process => process.WorkingSet64)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    using (candidate)
                    {
                        pid = candidate.Id;
                        workingSet = candidate.WorkingSet64 / 1024d / 1024d;
                        privateMemory = candidate.PrivateMemorySize64 / 1024d / 1024d;
                        var cpu = candidate.TotalProcessorTime;
                        var nowTicks = Environment.TickCount64;
                        if (previousProcessTimes.TryGetValue(candidate.Id, out var prior))
                        {
                            var secondsDelta = Math.Max(0.001, (nowTicks - prior.Tick) / 1000d);
                            gameCpu = Math.Clamp(100d * (cpu - prior.Cpu).TotalSeconds / (secondsDelta * Environment.ProcessorCount), 0, 100);
                        }
                        previousProcessTimes[candidate.Id] = (cpu, nowTicks);
                    }
                }
            }
            catch { /* Процесс мог завершиться между перечислением и чтением счётчиков. */ }

            var gpuMetrics = gpuCounters.Read(pid);
            var gpuSensor = gpuSensors.Read();

            samples.Add(new BenchmarkTelemetrySample
            {
                TimestampLocal = DateTime.Now, ProcessId = pid, GameCpuPercent = gameCpu,
                GameWorkingSetMb = workingSet, GamePrivateMemoryMb = privateMemory,
                SystemCpuPercent = systemCpu, SystemMemoryPercent = memoryPercent, SystemUsedMemoryGb = usedMemoryGb,
                GameGpuEnginePercent = gpuMetrics.GameEnginePercent, SystemGpuBusyPercent = gpuMetrics.SystemBusyPercent,
                GameDedicatedGpuMemoryMb = gpuMetrics.GameDedicatedMb, GameSharedGpuMemoryMb = gpuMetrics.GameSharedMb,
                GpuTemperatureC = gpuSensor.TemperatureC, GpuHotspotTemperatureC = gpuSensor.HotspotTemperatureC,
                GpuMemoryTemperatureC = gpuSensor.MemoryTemperatureC, GpuPowerWatts = gpuSensor.PowerWatts,
                GpuClockMhz = gpuSensor.ClockMhz, GpuFanPercent = gpuSensor.FanPercent,
                GpuLoadPercent = gpuSensor.LoadPercent, GpuSensorStatus = gpuSensor.Status
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return samples;
    }

    private static double? ReadAverageGpuBusy(string filePath)
    {
        using var reader = new StreamReader(filePath);
        var headerLine = reader.ReadLine();
        if (headerLine == null) return null;
        var separator = headerLine.Count(character => character == ';') > headerLine.Count(character => character == ',') ? ';' : ',';
        var headers = headerLine.Split(separator).Select(header => header.Trim().Trim('"')).ToArray();
        var index = Array.FindIndex(headers, header => GpuBusyColumns.Any(candidate => string.Equals(candidate, header, StringComparison.OrdinalIgnoreCase)));
        if (index < 0) return null;
        var values = new List<double>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var cells = line.Split(separator);
            if (index < cells.Length && double.TryParse(cells[index].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0)
                values.Add(value);
        }
        return values.Count == 0 ? null : values.Average();
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Average();
    }

    private static long FileTime(System.Runtime.InteropServices.ComTypes.FILETIME value) => ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    private sealed class WindowsGpuCounterReader : IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private static readonly Regex PidRegex = new(@"(?:^|_)pid_(\d+)(?:_|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EngineRegex = new(@"pid_\d+_(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private IntPtr _query;
        private IntPtr _engineCounter;
        private IntPtr _memoryCounter;
        private IntPtr _sharedMemoryCounter;

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterValue
        {
            public uint Status;
            public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterItem
        {
            public IntPtr Name;
            public CounterValue Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
        private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        public WindowsGpuCounterReader()
        {
            try
            {
                if (PdhOpenQueryW(null, UIntPtr.Zero, out _query) != 0) return;
                _ = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out _engineCounter);
                _ = PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", UIntPtr.Zero, out _memoryCounter);
                _ = PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Shared Usage", UIntPtr.Zero, out _sharedMemoryCounter);
                if (_engineCounter == IntPtr.Zero && _memoryCounter == IntPtr.Zero && _sharedMemoryCounter == IntPtr.Zero)
                {
                    PdhCloseQuery(_query);
                    _query = IntPtr.Zero;
                }
            }
            catch (DllNotFoundException) { _query = IntPtr.Zero; }
            catch (EntryPointNotFoundException) { _query = IntPtr.Zero; }
        }

        public (double? GameEnginePercent, double? SystemBusyPercent, double? GameDedicatedMb, double? GameSharedMb) Read(int? gamePid)
        {
            if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return (null, null, null, null);
            var engineValues = ReadCounter(_engineCounter);
            var gameEngines = engineValues.Where(item => gamePid.HasValue && GetPid(item.Name) == gamePid).ToArray();
            var systemBusy = engineValues
                .Select(item => (Engine: GetEngineKey(item.Name), item.Value))
                .Where(item => item.Engine != null)
                .GroupBy(item => item.Engine!, StringComparer.OrdinalIgnoreCase)
                .Select(group => Math.Min(100d, group.Sum(item => item.Value)))
                .DefaultIfEmpty().Max();

            var memoryValues = ReadCounter(_memoryCounter);
            var gameMemory = memoryValues.Where(item => gamePid.HasValue && GetPid(item.Name) == gamePid).ToArray();
            var dedicated = gameMemory.Length == 0 ? (double?)null : gameMemory.Sum(item => item.Value) / 1024d / 1024d;
            var sharedValues = ReadCounter(_sharedMemoryCounter);
            var sharedItems = sharedValues.Where(item => gamePid.HasValue && GetPid(item.Name) == gamePid).ToArray();
            var shared = sharedItems.Length == 0 ? (double?)null : sharedItems.Sum(item => item.Value) / 1024d / 1024d;
            var gameBusy = gameEngines.Length == 0 ? (double?)null : gameEngines.Max(item => item.Value);
            return (gameBusy, engineValues.Count == 0 ? null : systemBusy, dedicated, shared);
        }

        private static List<(string Name, double Value)> ReadCounter(IntPtr counter)
        {
            var result = new List<(string, double)>();
            if (counter == IntPtr.Zero) return result;
            uint bytes = 0, count = 0;
            var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bytes, ref count, IntPtr.Zero);
            if (bytes == 0 || count == 0 || (status != 0 && status != 0x800007D2)) return result;
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bytes, ref count, buffer);
                if (status != 0) return result;
                var itemSize = Marshal.SizeOf<CounterItem>();
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, index * itemSize));
                    if (item.Name == IntPtr.Zero || item.Value.Status > 1 || !double.IsFinite(item.Value.DoubleValue)) continue;
                    result.Add((Marshal.PtrToStringUni(item.Name) ?? string.Empty, item.Value.DoubleValue));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return result;
        }

        private static int? GetPid(string instance)
        {
            var match = PidRegex.Match(instance);
            return match.Success && int.TryParse(match.Groups[1].Value, out var pid) ? pid : null;
        }

        private static string? GetEngineKey(string instance)
        {
            var match = EngineRegex.Match(instance);
            return match.Success ? match.Groups[1].Value : null;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    public static FrameTimeBenchmarkResult ImportPresentMonCsv(string filePath)
    {
        using var reader = new StreamReader(filePath);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("CSV не содержит заголовок.");
        var separator = headerLine.Count(character => character == ';') > headerLine.Count(character => character == ',') ? ';' : ',';
        var headers = headerLine.Split(separator).Select(header => header.Trim().Trim('"')).ToArray();
        var columnIndex = Array.FindIndex(headers, header => FrameTimeColumns.Any(candidate => string.Equals(candidate, header, StringComparison.OrdinalIgnoreCase)));
        if (columnIndex < 0)
            throw new InvalidDataException("В CSV не найден столбец времени кадра PresentMon (MsBetweenPresents).");

        var frameTimes = new List<double>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var cells = line.Split(separator);
            if (columnIndex >= cells.Length) continue;
            var raw = cells[columnIndex].Trim().Trim('"');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.GetCultureInfo("ru-RU"), out value))
                continue;
            if (value is > 0 and < 1000) frameTimes.Add(value);
        }

        if (frameTimes.Count < 120)
            throw new InvalidDataException("Недостаточно валидных кадров: нужно минимум 120 строк замера.");

        frameTimes.Sort();
        var averageFrameTime = frameTimes.Average();
        var p99FrameTime = Percentile(frameTimes, 0.99);
        var p999FrameTime = Percentile(frameTimes, 0.999);
        return new FrameTimeBenchmarkResult
        {
            SourceFileName = Path.GetFileName(filePath),
            SampleCount = frameTimes.Count,
            AverageFps = 1000d / averageFrameTime,
            OnePercentLowFps = 1000d / p99FrameTime,
            PointOnePercentLowFps = 1000d / p999FrameTime,
            AverageFrameTimeMs = averageFrameTime,
            P99FrameTimeMs = p99FrameTime
        };
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var index = (values.Count - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return lower == upper ? values[lower] : values[lower] + (values[upper] - values[lower]) * (index - lower);
    }
}
