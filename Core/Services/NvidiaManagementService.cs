using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

/// <summary>
/// Reads NVIDIA telemetry from NVML shipped with the installed display driver.
/// No NVIDIA binaries are redistributed and this service does not change GPU settings.
/// </summary>
internal static class NvidiaManagementService
{
    public static NvidiaManagementSnapshot ReadAdapters()
    {
        NvidiaManagementSession? session = null;
        try
        {
            session = NvidiaManagementSession.TryOpen(out var status);
            if (session == null) return new NvidiaManagementSnapshot(status, Array.Empty<NvidiaAdapterReading>());
            return new NvidiaManagementSnapshot(session.Status, session.ReadAdapters());
        }
        catch (Exception ex)
        {
            return new NvidiaManagementSnapshot($"NVML не удалось прочитать: {ex.Message}", Array.Empty<NvidiaAdapterReading>());
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static int Similarity(string first, string second)
    {
        var a = Normalize(first);
        var b = Normalize(second);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return 100;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)) return 50;
        return b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(token => token.Length >= 3 && a.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    internal sealed record NvidiaManagementSnapshot(string Status, IReadOnlyList<NvidiaAdapterReading> Adapters);
    internal sealed record NvidiaAdapterReading(string Name, NvidiaGpuTelemetry Telemetry);

    internal sealed class NvidiaManagementSession : IDisposable
    {
        private const int NvmlSuccess = 0;
        private const int NvmlTemperatureGpu = 0;
        private const int NvmlClockGraphics = 0;
        private const int NvmlClockMemory = 2;
        private const int NvmlTextCapacity = 96;

        private readonly IntPtr _library;
        private readonly NvmlShutdown? _shutdown;
        private readonly NvmlDeviceGetCount? _getDeviceCount;
        private readonly NvmlDeviceGetHandleByIndex? _getDeviceByIndex;
        private readonly NvmlDeviceGetName? _getDeviceName;
        private readonly NvmlSystemGetDriverVersion? _getDriverVersion;
        private readonly NvmlDeviceGetUtilizationRates? _getUtilization;
        private readonly NvmlDeviceGetMemoryInfo? _getMemoryInfo;
        private readonly NvmlDeviceGetTemperature? _getTemperature;
        private readonly NvmlDeviceGetPowerUsage? _getPowerUsage;
        private readonly NvmlDeviceGetClockInfo? _getClockInfo;
        private readonly NvmlDeviceGetFanSpeed? _getFanSpeed;
        private bool _initialized;

        public string Status { get; }

        private NvidiaManagementSession(IntPtr library, string status)
        {
            _library = library;
            _shutdown = Get<NvmlShutdown>("nvmlShutdown");
            _getDeviceCount = Get<NvmlDeviceGetCount>("nvmlDeviceGetCount_v2") ?? Get<NvmlDeviceGetCount>("nvmlDeviceGetCount");
            _getDeviceByIndex = Get<NvmlDeviceGetHandleByIndex>("nvmlDeviceGetHandleByIndex_v2") ?? Get<NvmlDeviceGetHandleByIndex>("nvmlDeviceGetHandleByIndex");
            _getDeviceName = Get<NvmlDeviceGetName>("nvmlDeviceGetName");
            _getDriverVersion = Get<NvmlSystemGetDriverVersion>("nvmlSystemGetDriverVersion");
            _getUtilization = Get<NvmlDeviceGetUtilizationRates>("nvmlDeviceGetUtilizationRates");
            _getMemoryInfo = Get<NvmlDeviceGetMemoryInfo>("nvmlDeviceGetMemoryInfo");
            _getTemperature = Get<NvmlDeviceGetTemperature>("nvmlDeviceGetTemperature");
            _getPowerUsage = Get<NvmlDeviceGetPowerUsage>("nvmlDeviceGetPowerUsage");
            _getClockInfo = Get<NvmlDeviceGetClockInfo>("nvmlDeviceGetClockInfo");
            _getFanSpeed = Get<NvmlDeviceGetFanSpeed>("nvmlDeviceGetFanSpeed");
            Status = status;
        }

        public static NvidiaManagementSession? TryOpen(out string status)
        {
            var library = LoadNvml();
            if (library == IntPtr.Zero)
            {
                status = "NVML не найдена в папках установленного NVIDIA-драйвера. NVIDIA-разгон не включается без подтверждённого API.";
                return null;
            }

            var initV2 = GetExport<NvmlInit>(library, "nvmlInit_v2") ?? GetExport<NvmlInit>(library, "nvmlInit");
            if (initV2 == null)
            {
                NativeLibrary.Free(library);
                status = "Библиотека NVIDIA найдена, но не экспортирует NVML initialization API.";
                return null;
            }

            var session = new NvidiaManagementSession(library, "NVML подключена из установленного NVIDIA-драйвера. Показаны только поддержанные драйвером счётчики.");
            var result = initV2();
            if (result != NvmlSuccess)
            {
                session.Dispose();
                status = $"NVML найдена, но драйвер отклонил инициализацию (код {result}).";
                return null;
            }

            session._initialized = true;
            status = session.Status;
            return session;
        }

        public IReadOnlyList<NvidiaAdapterReading> ReadAdapters()
        {
            if (_getDeviceCount == null || _getDeviceByIndex == null || _getDeviceName == null)
                return Array.Empty<NvidiaAdapterReading>();
            if (_getDeviceCount(out var count) != NvmlSuccess)
                return Array.Empty<NvidiaAdapterReading>();

            var driverVersion = _getDriverVersion == null
                ? string.Empty
                : ReadText(buffer => _getDriverVersion(buffer, (uint)buffer.Length));
            var adapters = new List<NvidiaAdapterReading>();
            for (uint index = 0; index < Math.Min(count, 32); index++)
            {
                if (_getDeviceByIndex(index, out var device) != NvmlSuccess || device == IntPtr.Zero) continue;
                var name = ReadText(buffer => _getDeviceName(device, buffer, (uint)buffer.Length));
                if (string.IsNullOrWhiteSpace(name)) continue;

                NvmlUtilization utilization = default;
                var hasUtilization = _getUtilization != null && _getUtilization(device, out utilization) == NvmlSuccess;
                NvmlMemory memory = default;
                var hasMemory = _getMemoryInfo != null && _getMemoryInfo(device, out memory) == NvmlSuccess;
                var temperature = ReadUInt((out uint value) => { value = 0; return _getTemperature == null ? -1 : _getTemperature(device, NvmlTemperatureGpu, out value); });
                var powerMilliwatts = ReadUInt((out uint value) => { value = 0; return _getPowerUsage == null ? -1 : _getPowerUsage(device, out value); });
                var graphicsClock = ReadUInt((out uint value) => { value = 0; return _getClockInfo == null ? -1 : _getClockInfo(device, NvmlClockGraphics, out value); });
                var memoryClock = ReadUInt((out uint value) => { value = 0; return _getClockInfo == null ? -1 : _getClockInfo(device, NvmlClockMemory, out value); });
                var fan = ReadUInt((out uint value) => { value = 0; return _getFanSpeed == null ? -1 : _getFanSpeed(device, out value); });

                adapters.Add(new NvidiaAdapterReading(name, new NvidiaGpuTelemetry
                {
                    ApiStatus = Status,
                    DriverVersion = driverVersion,
                    TotalMemoryBytes = hasMemory && memory.Total > 0 ? memory.Total : null,
                    UsedMemoryBytes = hasMemory && memory.Total > 0 ? memory.Used : null,
                    GpuUtilizationPercent = hasUtilization && utilization.Gpu <= 100 ? utilization.Gpu : null,
                    MemoryUtilizationPercent = hasUtilization && utilization.Memory <= 100 ? utilization.Memory : null,
                    TemperatureC = temperature,
                    PowerWatts = powerMilliwatts.HasValue ? powerMilliwatts.Value / 1000d : null,
                    GraphicsClockMhz = graphicsClock,
                    MemoryClockMhz = memoryClock,
                    FanPercent = fan
                }));
            }
            return adapters;
        }

        public NvidiaGpuTelemetry? ReadTelemetry(string adapterName)
        {
            var selected = ReadAdapters()
                .Select(adapter => (Adapter: adapter, Score: Similarity(adapter.Name, adapterName)))
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            return selected.Adapter != null && selected.Score > 0 ? selected.Adapter.Telemetry : null;
        }

        private T? Get<T>(string name) where T : Delegate => GetExport<T>(_library, name);

        private static T? GetExport<T>(IntPtr library, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out var address)) return null;
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private static IntPtr LoadNvml()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "NVSMI", "nvml.dll")
            };
            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var library)) return library;
            }
            return IntPtr.Zero;
        }

        private delegate int NvmlUIntReader(out uint value);

        private static uint? ReadUInt(NvmlUIntReader read)
        {
            return read(out var value) == NvmlSuccess ? value : null;
        }

        private static string ReadText(Func<byte[], int> read)
        {
            var buffer = new byte[NvmlTextCapacity];
            if (read(buffer) != NvmlSuccess) return string.Empty;
            var terminator = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
        }

        public void Dispose()
        {
            if (_initialized) _ = _shutdown?.Invoke();
            _initialized = false;
            if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        }

        [StructLayout(LayoutKind.Sequential)] private struct NvmlUtilization { public uint Gpu; public uint Memory; }
        [StructLayout(LayoutKind.Sequential)] private struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInit();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetCount(out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlSystemGetDriverVersion([Out] byte[] version, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temperature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetClockInfo(IntPtr device, uint clock, out uint mhz);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    }
}
