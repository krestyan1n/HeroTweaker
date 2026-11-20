using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HeroTweaker.Core.Services;

public sealed record AmdGpuTuningSnapshot(
    bool Available,
    string Status,
    int MinFrequency,
    int MaxFrequency,
    int MinFrequencyLimit,
    int MaxFrequencyLimit,
    int MinFrequencyStep,
    int MaxFrequencyStep,
    bool PowerLimitAvailable,
    string PowerLimitStatus,
    int PowerLimit,
    int PowerLimitMin,
    int PowerLimitMax,
    int PowerLimitStep,
    bool VramFrequencyAvailable,
    string VramStatus,
    int VramFrequency,
    int VramFrequencyMin,
    int VramFrequencyMax,
    int VramFrequencyStep);

/// <summary>
/// Thin runtime binding to AMD's driver-installed ADLX library. Only documented
/// ADLX interfaces are used; no driver DLL is redistributed with HeroTweaker.
/// </summary>
public static class AmdAdlxTuningService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IntRange
    {
        public int Min;
        public int Max;
        public int Step;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryFullVersion(out ulong version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Initialize2(ulong version, out IntPtr system, out IntPtr mapping);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Initialize(ulong version, out IntPtr system);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Terminate();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGpus(IntPtr self, out IntPtr gpus);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetListSize(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGpuAt(IntPtr self, uint index, out IntPtr gpu);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGpuName(IntPtr self, out IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTuningServices(IntPtr self, out IntPtr services);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsSupported(IntPtr self, IntPtr gpu, out byte supported);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetManualGfxTuning(IntPtr self, IntPtr gpu, out IntPtr tuning);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterface(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string interfaceId, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRange(IntPtr self, out IntRange range);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInt(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetInt(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Release(IntPtr self);

    public static AmdGpuTuningSnapshot Read(string? adapterName)
    {
        try
        {
            using var session = new AdlxSession();
            IntPtr gpu = session.FindGpu(adapterName);
            if (gpu == IntPtr.Zero)
                return Unavailable("ADLX не обнаружил AMD-адаптер.");

            IntPtr services = IntPtr.Zero;
            IntPtr tuningBase = IntPtr.Zero;
            IntPtr tuning = IntPtr.Zero;
            IntPtr powerTuning = IntPtr.Zero;
            IntPtr vramTuningBase = IntPtr.Zero;
            IntPtr vramTuning = IntPtr.Zero;
            try
            {
                Check(session.Call(session.System, 8, out services), "Не удалось открыть GPU Tuning в ADLX.");
                byte supported = 0;
                Check(session.Call(services, 8, gpu, out supported), "Драйвер не ответил на проверку ручной настройки GPU.");
                if (supported == 0)
                    return Unavailable("Драйвер этой видеокарты не предоставляет ручную настройку частот через ADLX.");

                Check(session.Call(services, 14, gpu, out tuningBase), "Драйвер не предоставил интерфейс настройки частот.");
                int queryResult = session.Call(tuningBase, 2, "IADLXManualGraphicsTuning2_1", out tuning);
                if (queryResult != 0)
                    queryResult = session.Call(tuningBase, 2, "IADLXManualGraphicsTuning2", out tuning);
                Check(queryResult, "Драйвер не поддерживает интерфейс частот RDNA.");

                Check(session.Call(tuning, 3, out IntRange minRange), "Не удалось прочитать диапазон минимальной частоты.");
                Check(session.Call(tuning, 6, out IntRange maxRange), "Не удалось прочитать диапазон максимальной частоты.");
                Check(session.Call(tuning, 4, out int minValue), "Не удалось прочитать текущую минимальную частоту.");
                Check(session.Call(tuning, 7, out int maxValue), "Не удалось прочитать текущую максимальную частоту.");

                if (minRange.Min < 0 || minRange.Max <= minRange.Min || maxRange.Max <= maxRange.Min)
                    return Unavailable("ADLX вернул некорректный диапазон настройки частот.");

                bool powerAvailable = false;
                string powerStatus = "Регулятор лимита мощности не предоставлен драйвером.";
                int powerValue = 0;
                int powerMin = 0;
                int powerMax = 0;
                int powerStep = 1;
                if (session.Call(services, 11, gpu, out supported) == 0 && supported != 0 &&
                    session.Call(services, 17, gpu, out powerTuning) == 0 && powerTuning != IntPtr.Zero &&
                    session.Call(powerTuning, 3, out IntRange powerRange) == 0 &&
                    session.Call(powerTuning, 4, out powerValue) == 0 &&
                    powerRange.Max > powerRange.Min)
                {
                    powerAvailable = true;
                    powerMin = powerRange.Min;
                    powerMax = powerRange.Max;
                    powerStep = Math.Max(1, powerRange.Step);
                    powerStatus = "Текущий лимит и допустимый диапазон прочитаны из AMD ADLX.";
                }

                bool vramAvailable = false;
                string vramStatus = "Регулятор частоты видеопамяти не предоставлен драйвером.";
                int vramValue = 0;
                int vramMin = 0;
                int vramMax = 0;
                int vramStep = 1;
                if (session.Call(services, 9, gpu, out supported) == 0 && supported != 0 &&
                    session.Call(services, 15, gpu, out vramTuningBase) == 0 && vramTuningBase != IntPtr.Zero &&
                    session.Call(vramTuningBase, 2, "IADLXManualVRAMTuning2", out vramTuning) == 0 && vramTuning != IntPtr.Zero &&
                    session.Call(vramTuning, 7, out IntRange vramRange) == 0 &&
                    session.Call(vramTuning, 8, out vramValue) == 0 &&
                    vramRange.Max > vramRange.Min && vramRange.Step > 0)
                {
                    vramAvailable = true;
                    vramMin = vramRange.Min;
                    vramMax = vramRange.Max;
                    vramStep = vramRange.Step;
                    vramStatus = "Частота видеопамяти и допустимый шаг прочитаны из AMD ADLX.";
                }

                return new AmdGpuTuningSnapshot(true,
                    "Значения и допустимый диапазон частот получены от драйвера AMD через ADLX.",
                    minValue, maxValue, minRange.Min, maxRange.Max, minRange.Step, maxRange.Step,
                    powerAvailable, powerStatus, powerValue, powerMin, powerMax, powerStep,
                    vramAvailable, vramStatus, vramValue, vramMin, vramMax, vramStep);
            }
            finally
            {
                session.Release(vramTuning);
                session.Release(vramTuningBase);
                session.Release(powerTuning);
                session.Release(tuning);
                session.Release(tuningBase);
                session.Release(services);
                session.Release(gpu);
            }
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    public static (bool Success, string Message) Apply(string? adapterName, int minFrequency, int maxFrequency)
    {
        if (minFrequency > maxFrequency)
            return (false, "Минимальная частота не может быть выше максимальной.");

        try
        {
            using var session = new AdlxSession();
            IntPtr gpu = session.FindGpu(adapterName);
            if (gpu == IntPtr.Zero)
                return (false, "AMD-адаптер не найден.");

            IntPtr services = IntPtr.Zero;
            IntPtr tuningBase = IntPtr.Zero;
            IntPtr tuning = IntPtr.Zero;
            try
            {
                Check(session.Call(session.System, 8, out services), "Не удалось открыть GPU Tuning в ADLX.");
                byte supported = 0;
                Check(session.Call(services, 8, gpu, out supported), "Не удалось проверить поддержку настройки.");
                if (supported == 0)
                    return (false, "Ручная настройка частот не поддерживается этим драйвером.");

                Check(session.Call(services, 14, gpu, out tuningBase), "Драйвер не предоставил настройку частот.");
                int queryResult = session.Call(tuningBase, 2, "IADLXManualGraphicsTuning2_1", out tuning);
                if (queryResult != 0)
                    queryResult = session.Call(tuningBase, 2, "IADLXManualGraphicsTuning2", out tuning);
                Check(queryResult, "Интерфейс ручной настройки частот недоступен.");

                Check(session.Call(tuning, 3, out IntRange minRange), "Не удалось получить допустимый диапазон.");
                Check(session.Call(tuning, 6, out IntRange maxRange), "Не удалось получить допустимый диапазон.");
                if (minFrequency < minRange.Min || minFrequency > minRange.Max ||
                    maxFrequency < maxRange.Min || maxFrequency > maxRange.Max)
                    return (false, "Значения вышли за диапазон, разрешённый драйвером. Обновите параметры.");
                if ((minFrequency - minRange.Min) % Math.Max(1, minRange.Step) != 0 ||
                    (maxFrequency - maxRange.Min) % Math.Max(1, maxRange.Step) != 0)
                    return (false, "Частоты не соответствуют шагу, заданному драйвером. Перечитайте диапазон.");

                Check(session.Call(tuning, 4, out int oldMin), "Не удалось прочитать текущую минимальную частоту.");
                Check(session.Call(tuning, 7, out int oldMax), "Не удалось прочитать текущую максимальную частоту.");

                // Change the bound that opens the valid interval first. If a later
                // driver call fails, make a best-effort rollback to the old values.
                int first = minFrequency > oldMax || maxFrequency >= oldMax ? 8 : 5;
                int second = first == 8 ? 5 : 8;
                int firstValue = first == 8 ? maxFrequency : minFrequency;
                int secondValue = second == 8 ? maxFrequency : minFrequency;
                int firstResult = session.Call(tuning, first, firstValue);
                if (firstResult != 0)
                    return (false, $"Драйвер отклонил изменение частоты (код {firstResult}).");

                int secondResult = session.Call(tuning, second, secondValue);
                if (secondResult != 0)
                {
                    session.Call(tuning, 8, oldMax);
                    session.Call(tuning, 5, oldMin);
                    return (false, $"Драйвер отклонил второй параметр частоты (код {secondResult}); предыдущие значения восстановлены.");
                }

                return (true, "Частоты приняты драйвером AMD. Проверяйте стабильность под нагрузкой.");
            }
            finally
            {
                session.Release(tuning);
                session.Release(tuningBase);
                session.Release(services);
                session.Release(gpu);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Success, string Message) ResetToFactory(string? adapterName)
    {
        try
        {
            using var session = new AdlxSession();
            IntPtr gpu = session.FindGpu(adapterName);
            if (gpu == IntPtr.Zero)
                return (false, "AMD-адаптер не найден.");
            IntPtr services = IntPtr.Zero;
            try
            {
                Check(session.Call(session.System, 8, out services), "Не удалось открыть GPU Tuning в ADLX.");
                Check(session.Call(services, 5, gpu), "Драйвер не смог вернуть заводские параметры.");
                return (true, "Драйвер восстановил заводские параметры GPU.");
            }
            finally
            {
                session.Release(services);
                session.Release(gpu);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Success, string Message) ApplyPowerLimit(string? adapterName, int powerLimit)
    {
        try
        {
            using var session = new AdlxSession();
            IntPtr gpu = session.FindGpu(adapterName);
            if (gpu == IntPtr.Zero) return (false, "AMD-адаптер не найден.");
            IntPtr services = IntPtr.Zero;
            IntPtr tuning = IntPtr.Zero;
            try
            {
                Check(session.Call(session.System, 8, out services), "Не удалось открыть GPU Tuning в ADLX.");
                byte supported = 0;
                Check(session.Call(services, 11, gpu, out supported), "Не удалось проверить поддержку лимита мощности.");
                if (supported == 0) return (false, "Этот AMD-драйвер не поддерживает ручной лимит мощности.");
                Check(session.Call(services, 17, gpu, out tuning), "Драйвер не предоставил лимит мощности.");
                Check(session.Call(tuning, 3, out IntRange range), "Не удалось прочитать диапазон лимита мощности.");
                if (powerLimit < range.Min || powerLimit > range.Max)
                    return (false, "Значение вне диапазона, разрешённого драйвером. Обновите параметры.");
                if ((powerLimit - range.Min) % Math.Max(1, range.Step) != 0)
                    return (false, "Значение не соответствует шагу, заданному драйвером. Перечитайте диапазон.");
                Check(session.Call(tuning, 4, out int oldValue), "Не удалось прочитать текущий лимит мощности.");
                int result = session.Call(tuning, 5, powerLimit);
                if (result != 0)
                    return (false, $"Драйвер отклонил лимит мощности (код ADLX {result}); текущее значение {oldValue}% сохранено.");
                return (true, "Лимит мощности принят драйвером AMD. Контролируйте температуру и стабильность под нагрузкой.");
            }
            finally
            {
                session.Release(tuning);
                session.Release(services);
                session.Release(gpu);
            }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool Success, string Message) ApplyVramFrequency(string? adapterName, int frequency)
    {
        try
        {
            using var session = new AdlxSession();
            IntPtr gpu = session.FindGpu(adapterName);
            if (gpu == IntPtr.Zero) return (false, "AMD-адаптер не найден.");
            IntPtr services = IntPtr.Zero;
            IntPtr tuningBase = IntPtr.Zero;
            IntPtr tuning = IntPtr.Zero;
            try
            {
                Check(session.Call(session.System, 8, out services), "Не удалось открыть GPU Tuning в ADLX.");
                byte supported = 0;
                Check(session.Call(services, 9, gpu, out supported), "Не удалось проверить поддержку настройки видеопамяти.");
                if (supported == 0) return (false, "Этот AMD-драйвер не поддерживает ручную настройку видеопамяти.");
                Check(session.Call(services, 15, gpu, out tuningBase), "Драйвер не предоставил настройку видеопамяти.");
                Check(session.Call(tuningBase, 2, "IADLXManualVRAMTuning2", out tuning), "Интерфейс частоты видеопамяти не поддерживается.");
                Check(session.Call(tuning, 7, out IntRange range), "Не удалось прочитать диапазон видеопамяти.");
                if (frequency < range.Min || frequency > range.Max)
                    return (false, "Значение вне диапазона, разрешённого драйвером. Перечитайте параметры.");
                if ((frequency - range.Min) % Math.Max(1, range.Step) != 0)
                    return (false, "Частота не соответствует шагу, заданному драйвером. Перечитайте диапазон.");
                int result = session.Call(tuning, 9, frequency);
                return result == 0
                    ? (true, "Частота видеопамяти принята драйвером AMD. Проверьте стабильность в играх и тесте VRAM.")
                    : (false, $"Драйвер отклонил частоту видеопамяти (код ADLX {result}).");
            }
            finally
            {
                session.Release(tuning);
                session.Release(tuningBase);
                session.Release(services);
                session.Release(gpu);
            }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static AmdGpuTuningSnapshot Unavailable(string status) => new(false, status, 0, 0, 0, 0, 1, 1, false, "", 0, 0, 0, 1, false, "", 0, 0, 0, 1);

    private static void Check(int result, string message)
    {
        if (result != 0) throw new InvalidOperationException($"{message} (ADLX {result}).");
    }

    private sealed class AdlxSession : IDisposable
    {
        private readonly IntPtr _library;
        private readonly Terminate? _terminate;
        private IntPtr _mapping;
        public IntPtr System { get; }

        public AdlxSession()
        {
            string path = Path.Combine(Environment.SystemDirectory, "amdadlx64.dll");
            if (!File.Exists(path))
                throw new FileNotFoundException("Не найдена библиотека ADLX из драйвера AMD.", path);

            _library = NativeLibrary.Load(path);
            try
            {
                var queryVersion = GetExport<QueryFullVersion>(_library, "ADLXQueryFullVersion");
                _terminate = GetExport<Terminate>(_library, "ADLXTerminate");
                Check(queryVersion(out ulong version), "Не удалось получить версию ADLX.");

                if (NativeLibrary.TryGetExport(_library, "ADLXInitialize2", out IntPtr init2Address))
                {
                    var initialize2 = Marshal.GetDelegateForFunctionPointer<Initialize2>(init2Address);
                    Check(initialize2(version, out IntPtr system, out _mapping), "Не удалось инициализировать ADLX.");
                    System = system;
                }
                else
                {
                    var initialize = GetExport<Initialize>(_library, "ADLXInitialize");
                    Check(initialize(version, out IntPtr system), "Не удалось инициализировать ADLX.");
                    System = system;
                }
            }
            catch
            {
                NativeLibrary.Free(_library);
                throw;
            }
        }

        public IntPtr FindGpu(string? name)
        {
            Call(System, 1, out IntPtr list);
            if (list == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                uint count = Call(list, 3);
                for (uint i = 0; i < count; i++)
                {
                    if (Call(list, 11, i, out IntPtr gpu) != 0 || gpu == IntPtr.Zero) continue;
                    if (CallName(gpu, out IntPtr nativeName) == 0)
                    {
                        string currentName = Marshal.PtrToStringUTF8(nativeName) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name) ||
                            currentName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                            currentName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                            name.Contains(currentName, StringComparison.OrdinalIgnoreCase))
                            return gpu;
                    }
                    Release(gpu);
                }
                return IntPtr.Zero;
            }
            finally { Release(list); }
        }

        public int Call(IntPtr target, int index, out IntPtr value) => Invoke<GetPointer>(target, index)(target, out value);
        public int Call(IntPtr target, int index, IntPtr gpu, out IntPtr value) => Invoke<GetPointerWithGpu>(target, index)(target, gpu, out value);
        public int Call(IntPtr target, int index, IntPtr gpu) => Invoke<ResetGpu>(target, index)(target, gpu);
        public int Call(IntPtr target, int index, string id, out IntPtr value) => Invoke<QueryInterface>(target, index)(target, id, out value);
        public int Call(IntPtr target, int index, out IntRange value) => Invoke<GetRange>(target, index)(target, out value);
        public int Call(IntPtr target, int index, out int value) => Invoke<GetInt>(target, index)(target, out value);
        public int Call(IntPtr target, int index, int value) => Invoke<SetInt>(target, index)(target, value);
        public int Call(IntPtr target, int index, IntPtr gpu, out byte value) => Invoke<IsSupported>(target, index)(target, gpu, out value);
        public uint Call(IntPtr target, int index) => Invoke<GetListSize>(target, index)(target);
        public int Call(IntPtr target, int index, uint position, out IntPtr value) => Invoke<GetGpuAt>(target, index)(target, position, out value);
        private int CallName(IntPtr target, out IntPtr name) => Invoke<GetGpuName>(target, 7)(target, out name);

        private static T Invoke<T>(IntPtr target, int index) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(target);
            IntPtr method = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(method);
        }

        public void Release(IntPtr target)
        {
            if (target == IntPtr.Zero) return;
            Invoke<Release>(target, 1)(target);
        }

        private static T GetExport<T>(IntPtr library, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out IntPtr address))
                throw new EntryPointNotFoundException($"В ADLX отсутствует {name}.");
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        public void Dispose()
        {
            try
            {
                // IADLXSystem and IADLMapping are singleton interfaces with
                // their own vtables; neither inherits IADLXInterface::Release.
                // Release only the regular interfaces returned by methods above.
                _mapping = IntPtr.Zero;
                _terminate?.Invoke();
            }
            finally { NativeLibrary.Free(_library); }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointer(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointerWithGpu(IntPtr self, IntPtr gpu, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ResetGpu(IntPtr self, IntPtr gpu);
    }
}
