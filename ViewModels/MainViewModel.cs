using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HeroTweaker.Core.Interfaces;
using HeroTweaker.Core.Models;
using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.Core.Services;
using MessageBox = HeroTweaker.Core.Services.HeroDialog;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.ViewModels;

public class DriveSelectModel
{
    public string RootPath { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TotalSize { get; set; } = string.Empty;
    public string AvailableFreeSpace { get; set; } = string.Empty;
    public double UsedPercentage { get; set; }

    public double UsedRatio => Math.Clamp(UsedPercentage / 100.0, 0.005, 1.0);

    public DriveSelectModel() { }

    public DriveSelectModel(string rootPath, string volumeLabel, string displayName, string totalSize, string availableFreeSpace, double usedPercentage)
    {
        RootPath = rootPath;
        VolumeLabel = volumeLabel;
        DisplayName = displayName;
        TotalSize = totalSize;
        AvailableFreeSpace = availableFreeSpace;
        UsedPercentage = usedPercentage;
    }
}

public record HealthPenaltyItem(string Title, string ImpactText, string Category, Action FixAction);

public class MainViewModel : ObservableObject
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() => dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime, out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    private static ulong FileTimeToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;

    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    private readonly ObservableCollection<TweakViewModel> _allTweaks = new();
    public ICollectionView FilteredTweaks { get; }
    public ObservableCollection<TweakViewModel> DashboardTweaks { get; } = new();

    private CancellationTokenSource? _diskScanCts;
    private DispatcherTimer? _telemetryTimer;

    // === ОБНОВЛЕНИЯ GITHUB ===
    private bool _hasUpdateAvailable;
    public bool HasUpdateAvailable
    {
        get => _hasUpdateAvailable;
        set => SetField(ref _hasUpdateAvailable, value);
    }

    private string _latestVersionTag = string.Empty;
    public string LatestVersionTag
    {
        get => _latestVersionTag;
        set => SetField(ref _latestVersionTag, value);
    }

    private string _releaseUrl = string.Empty;
    public string ReleaseUrl
    {
        get => _releaseUrl;
        set => SetField(ref _releaseUrl, value);
    }

    private string _updateStatus = $"Текущая версия {UpdateCheckerService.CurrentVersion}.";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }
    public string AppVersionDisplay => UpdateCheckerService.CurrentVersion;
    public string AppWindowTitle => $"HeroTweaker v{AppVersionDisplay}";
    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }
    private bool _isUpdateChecking;
    public bool IsUpdateChecking
    {
        get => _isUpdateChecking;
        private set
        {
            if (SetField(ref _isUpdateChecking, value))
            {
                OnPropertyChanged(nameof(UpdateActionText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    private bool _isUpdateInstalling;
    public bool IsUpdateInstalling
    {
        get => _isUpdateInstalling;
        private set
        {
            if (SetField(ref _isUpdateInstalling, value))
            {
                OnPropertyChanged(nameof(UpdateActionText));
                OnPropertyChanged(nameof(CanInstallUpdate));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public string UpdateActionText => IsUpdateChecking ? "Проверяю..." : IsUpdateInstalling ? "Установка..." : "Проверить обновления";
    private UpdateCheckResult? _availableUpdate;
    public bool CanInstallUpdate => _availableUpdate?.CanInstall == true && HasUpdateAvailable && !IsUpdateInstalling;

    public ICommand OpenUpdateUrlCommand { get; }
    public ICommand DismissUpdateCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }

    // === СОСТОЯНИЕ ЗАГРУЗКИ (SPLASH SCREEN) ===
    private bool _isAppLoading = true;
    public bool IsAppLoading { get => _isAppLoading; set => SetField(ref _isAppLoading, value); }

    private string _splashStatus = "Инициализация модулей Hero Engineering...";
    public string SplashStatus { get => _splashStatus; set => SetField(ref _splashStatus, value); }

    private double _splashProgress = 15;
    public double SplashProgress { get => _splashProgress; set => SetField(ref _splashProgress, value); }

    private SystemInfoModel? _sysInfo;
    public SystemInfoModel? SysInfo { get => _sysInfo; set => SetField(ref _sysInfo, value); }

    // === ВИДИМОСТЬ ВКЛАДОК ===
    public bool IsDashboardVisible => SelectedCategory == "Информация";
    public bool IsDiskVisible => SelectedCategory == "Диск";
    public bool IsDriversVisible => SelectedCategory == "Драйверы";
    public bool IsAppsVisible => SelectedCategory == "Приложения";
    public bool IsDeveloperVisible => SelectedCategory == "Developer";
    public bool IsStartupVisible => SelectedCategory == "Автозагрузка";
    public bool IsNetworkVisible => SelectedCategory == "Сеть & Пинг";
    public bool IsAmdGpuVisible => SelectedCategory == "AMD GPU";
    public bool IsNvidiaGpuVisible => SelectedCategory == "NVIDIA GPU";
    public bool IsGpuAuditVisible => IsAmdGpuVisible || IsNvidiaGpuVisible;
    private int _gpuModuleTabIndex;
    public int GpuModuleTabIndex
    {
        get => _gpuModuleTabIndex;
        set
        {
            if (!SetField(ref _gpuModuleTabIndex, value)) return;
            OnPropertyChanged(nameof(IsGpuOverviewVisible));
            OnPropertyChanged(nameof(IsGpuProfilesVisible));
            OnPropertyChanged(nameof(IsGpuDiagnosticsVisible));
            OnPropertyChanged(nameof(IsGpuDriverVisible));
        }
    }
    public bool IsGpuOverviewVisible => GpuModuleTabIndex == 0;
    public bool IsGpuProfilesVisible => GpuModuleTabIndex == 1;
    public bool IsGpuDiagnosticsVisible => GpuModuleTabIndex == 2;
    public bool IsGpuDriverVisible => GpuModuleTabIndex == 3;
    public bool IsBenchmarkVisible => SelectedCategory == "Бенчмарк";
    public bool IsSettingsVisible => SelectedCategory == "Настройки";
    public bool IsTweaksVisible => !IsDashboardVisible && !IsDiskVisible && !IsDriversVisible && !IsAppsVisible && !IsSettingsVisible && !IsDeveloperVisible && !IsStartupVisible && !IsNetworkVisible && !IsGpuAuditVisible && !IsBenchmarkVisible;
    public bool IsSearchVisible => IsTweaksVisible;

    public string CategorySubtitle => SelectedCategory switch
    {
        "Информация" => "Общее состояние операционной системы, телеметрия в реальном времени и быстрые твики",
        "Диск" => "Интерактивный анализ накопителей DiskTree и глубокая очистка системных кэшей",
        "Драйверы" => "Поиск обновлений Windows Update, экспорт INF-пакетов и пакетная установка Visual C++",
        "Приложения" => "Каталог проверенного софта Winget, умный деинсталлятор и восстановление UWP Store",
        "Developer" => "Аудит окружения разработки: компиляторы, разрешение бинарников и чистка дубликатов PATH",
        "Автозагрузка" => "Оптимизация времени холодного старта ПК и управление фоновыми автозапускаемыми службами",
        "Сеть & Пинг" => "Выбор адаптера, сетевой реаниматор, DNS бенчмарк и сверхнизкая задержка Nagle",
        "AMD GPU" => "Аудит AMD-адаптера: конфигурация Windows, память видеокарты и готовность к диагностике производительности",
        "NVIDIA GPU" => "Аудит NVIDIA-адаптера: конфигурация Windows, память видеокарты и готовность к диагностике производительности",
        "Бенчмарк" => "Сравнение замеров до и после по CSV PresentMon — только измеренные данные, без выдуманных баллов",
        "Настройки" => "Выбор цветовой палитры интерфейса, управление снимками реестра и кэшем",
        _ => "Управление и глубокая оптимизация параметров операционной системы"
    };

    // === ТЕЛЕМЕТРИЯ, SPARKLINE И СХЕМА ПИТАНИЯ ===
    private double _realTimeCpuUsage = 14.0;
    public double RealTimeCpuUsage { get => _realTimeCpuUsage; set => SetField(ref _realTimeCpuUsage, value); }

    private double _realTimeRamUsage = 38.0;
    public double RealTimeRamUsage { get => _realTimeRamUsage; set => SetField(ref _realTimeRamUsage, value); }

    private string _ramDetailsFormatted = "0 ГБ / 0 ГБ";
    public string RamDetailsFormatted { get => _ramDetailsFormatted; set => SetField(ref _ramDetailsFormatted, value); }

    private PathGeometry? _cpuChartGeometry;
    public PathGeometry? CpuChartGeometry { get => _cpuChartGeometry; set => SetField(ref _cpuChartGeometry, value); }

    private PathGeometry? _ramChartGeometry;
    public PathGeometry? RamChartGeometry { get => _ramChartGeometry; set => SetField(ref _ramChartGeometry, value); }

    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _ramHistory = new();
    public IReadOnlyList<double> CpuHistory => _cpuHistory.ToArray();
    public IReadOnlyList<double> RamHistory => _ramHistory.ToArray();

    private string _activePowerPlanName = "Сбалансированная";
    public string ActivePowerPlanName { get => _activePowerPlanName; set => SetField(ref _activePowerPlanName, value); }

    public ICommand EnableUltimatePerformanceCommand { get; }

    // === АВАРИЙНЫЕ СИСТЕМНЫЕ ИНСТРУМЕНТЫ ===
    public ICommand RunSfcScanCommand { get; }
    public ICommand RunDismRestoreCommand { get; }

    // === ДЕТАЛИЗАЦИЯ HEALTH SCORE ===
    private int _systemHealthScore = 70;
    public int SystemHealthScore { get => _systemHealthScore; set => SetField(ref _systemHealthScore, value); }

    private string _systemHealthStatusText = "Анализ состояния системы...";
    public string SystemHealthStatusText { get => _systemHealthStatusText; set => SetField(ref _systemHealthStatusText, value); }

    private bool _isHealthDetailsOpen;
    public bool IsHealthDetailsOpen { get => _isHealthDetailsOpen; set => SetField(ref _isHealthDetailsOpen, value); }

    public ObservableCollection<HealthPenaltyItem> HealthPenaltyItems { get; } = new();
    public ICommand ToggleHealthDetailsCommand { get; }

    private int _activeTweaksCount;
    public int ActiveTweaksCount { get => _activeTweaksCount; set => SetField(ref _activeTweaksCount, value); }

    private int _disabledServicesCount;
    public int DisabledServicesCount { get => _disabledServicesCount; set => SetField(ref _disabledServicesCount, value); }

    private string _freedSpaceFormatted = "0 МБ";
    public string FreedSpaceFormatted { get => _freedSpaceFormatted; set => SetField(ref _freedSpaceFormatted, value); }

    public ObservableCollection<RecommendationModel> SystemRecommendations { get; } = new();

    // === СЕТЬ: АДАПТЕРЫ И DNS ===
    public ObservableCollection<NetworkAdapterInfo> AvailableAdapters { get; } = new();

    // === GPU AUDIT: безопасная диагностика без неофициальных драйверных твиков ===
    public ObservableCollection<GpuAdapterInfo> GpuAdapters { get; } = new();
    public ObservableCollection<GpuAuditCheck> GpuAuditChecks { get; } = new();
    public ObservableCollection<GpuAuditCheck> GpuWindowsChecks { get; } = new();
    public ObservableCollection<GpuGameProfile> GpuGameProfiles { get; } = new();

    private bool _hasAmdGpu;
    public bool HasAmdGpu
    {
        get => _hasAmdGpu;
        private set
        {
            if (SetField(ref _hasAmdGpu, value))
                OnPropertyChanged(nameof(HasSupportedGpu));
        }
    }

    private bool _hasNvidiaGpu;
    public bool HasNvidiaGpu
    {
        get => _hasNvidiaGpu;
        private set
        {
            if (SetField(ref _hasNvidiaGpu, value))
                OnPropertyChanged(nameof(HasSupportedGpu));
        }
    }

    public bool HasSupportedGpu => HasAmdGpu || HasNvidiaGpu;

    // === ИЗМЕРЕНИЯ ПРОИЗВОДИТЕЛЬНОСТИ ===
    private FrameTimeBenchmarkResult? _benchmarkBaseline;
    public FrameTimeBenchmarkResult? BenchmarkBaseline { get => _benchmarkBaseline; private set => SetField(ref _benchmarkBaseline, value); }
    private FrameTimeBenchmarkResult? _benchmarkAfter;
    public FrameTimeBenchmarkResult? BenchmarkAfter { get => _benchmarkAfter; private set => SetField(ref _benchmarkAfter, value); }
    public bool HasBenchmarkBaseline => BenchmarkBaseline != null;
    public bool HasBenchmarkAfter => BenchmarkAfter != null;
    public bool HasBenchmarkComparison => BenchmarkBaseline != null && BenchmarkAfter != null;
    public string BenchmarkAverageFpsDelta => HasBenchmarkComparison
        ? $"{BenchmarkAfter!.AverageFps - BenchmarkBaseline!.AverageFps:+0.0;-0.0;0.0} FPS"
        : "Нет второго замера";
    public string BenchmarkTelemetryComparisonText => !HasBenchmarkComparison
        ? "Снимите оба замера, чтобы увидеть изменения системной и GPU-телеметрии."
        : string.Join("   ·   ", new[]
        {
            CompareTelemetry("CPU игры", BenchmarkBaseline!.AverageGameCpuPercent, BenchmarkAfter!.AverageGameCpuPercent, "%", "F1"),
            CompareTelemetry("RAM игры", BenchmarkBaseline.AverageGameWorkingSetMb, BenchmarkAfter.AverageGameWorkingSetMb, "МБ", "F0"),
            CompareTelemetry("GPU игры", BenchmarkBaseline.AverageGameGpuEnginePercent, BenchmarkAfter.AverageGameGpuEnginePercent, "%", "F1"),
            CompareTelemetry("GPU load", BenchmarkBaseline.AverageGpuLoadPercent, BenchmarkAfter.AverageGpuLoadPercent, "%", "F1"),
            CompareTelemetry("GPU temp", BenchmarkBaseline.AverageGpuTemperatureC, BenchmarkAfter.AverageGpuTemperatureC, "°C", "F1"),
            CompareTelemetry("GPU power", BenchmarkBaseline.AverageGpuPowerWatts, BenchmarkAfter.AverageGpuPowerWatts, "Вт", "F0"),
            CompareTelemetry("CPU системы", BenchmarkBaseline.AverageSystemCpuPercent, BenchmarkAfter.AverageSystemCpuPercent, "%", "F1"),
            CompareTelemetry("RAM системы", BenchmarkBaseline.AverageSystemUsedMemoryGb, BenchmarkAfter.AverageSystemUsedMemoryGb, "ГБ", "F1")
        });

    private static string CompareTelemetry(string label, double? before, double? after, string unit, string format)
    {
        if (!before.HasValue || !after.HasValue) return $"{label}: недоступно";
        var delta = after.Value - before.Value;
        var deltaUnit = unit == "%" ? "п.п." : unit;
        return $"{label}: {before.Value.ToString(format)} {unit} → {after.Value.ToString(format)} {unit} (Δ {delta:+0.0;-0.0;0.0} {deltaUnit})";
    }
    private bool _isBenchmarkCapturing;
    public bool IsBenchmarkCapturing { get => _isBenchmarkCapturing; private set => SetField(ref _isBenchmarkCapturing, value); }
    private string _benchmarkStatus = "Выберите EXE игры и длительность захвата.";
    public string BenchmarkStatus { get => _benchmarkStatus; private set => SetField(ref _benchmarkStatus, value); }
    private int _benchmarkDurationSeconds = 60;
    public int BenchmarkDurationSeconds { get => _benchmarkDurationSeconds; set => SetField(ref _benchmarkDurationSeconds, Math.Clamp(value, 15, 600)); }

    private GpuAdapterInfo? _activeGpuAdapter;
    public GpuAdapterInfo? ActiveGpuAdapter { get => _activeGpuAdapter; private set => SetField(ref _activeGpuAdapter, value); }

    private string _gpuAuditStatus = "Ожидание аппаратного аудита";
    public string GpuAuditStatus { get => _gpuAuditStatus; private set => SetField(ref _gpuAuditStatus, value); }

    private bool _isAmdGpuTuningAvailable;
    public bool IsAmdGpuTuningAvailable { get => _isAmdGpuTuningAvailable; private set => SetField(ref _isAmdGpuTuningAvailable, value); }

    private bool _isAmdGpuTuningBusy;
    public bool IsAmdGpuTuningBusy
    {
        get => _isAmdGpuTuningBusy;
        private set
        {
            if (SetField(ref _isAmdGpuTuningBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _amdGpuTuningStatus = "Параметры управления ещё не считаны из AMD-драйвера.";
    public string AmdGpuTuningStatus { get => _amdGpuTuningStatus; private set => SetField(ref _amdGpuTuningStatus, value); }

    private double _amdGpuMinFrequency;
    public double AmdGpuMinFrequency { get => _amdGpuMinFrequency; set => SetField(ref _amdGpuMinFrequency, value); }

    private double _amdGpuMaxFrequency;
    public double AmdGpuMaxFrequency { get => _amdGpuMaxFrequency; set => SetField(ref _amdGpuMaxFrequency, value); }

    private double _amdGpuMinFrequencyLimit;
    public double AmdGpuMinFrequencyLimit { get => _amdGpuMinFrequencyLimit; private set => SetField(ref _amdGpuMinFrequencyLimit, value); }

    private double _amdGpuMaxFrequencyLimit;
    public double AmdGpuMaxFrequencyLimit { get => _amdGpuMaxFrequencyLimit; private set => SetField(ref _amdGpuMaxFrequencyLimit, value); }
    private double _amdGpuMinFrequencyStep = 1;
    public double AmdGpuMinFrequencyStep { get => _amdGpuMinFrequencyStep; private set => SetField(ref _amdGpuMinFrequencyStep, value); }
    private double _amdGpuMaxFrequencyStep = 1;
    public double AmdGpuMaxFrequencyStep { get => _amdGpuMaxFrequencyStep; private set => SetField(ref _amdGpuMaxFrequencyStep, value); }

    private bool _isAmdPowerLimitAvailable;
    public bool IsAmdPowerLimitAvailable { get => _isAmdPowerLimitAvailable; private set => SetField(ref _isAmdPowerLimitAvailable, value); }
    private string _amdPowerLimitStatus = "Лимит мощности ещё не считан из драйвера.";
    public string AmdPowerLimitStatus { get => _amdPowerLimitStatus; private set => SetField(ref _amdPowerLimitStatus, value); }
    private double _amdPowerLimit;
    public double AmdPowerLimit { get => _amdPowerLimit; set => SetField(ref _amdPowerLimit, value); }
    private double _amdPowerLimitMin;
    public double AmdPowerLimitMin { get => _amdPowerLimitMin; private set => SetField(ref _amdPowerLimitMin, value); }
    private double _amdPowerLimitMax;
    public double AmdPowerLimitMax { get => _amdPowerLimitMax; private set => SetField(ref _amdPowerLimitMax, value); }
    private double _amdPowerLimitStep = 1;
    public double AmdPowerLimitStep { get => _amdPowerLimitStep; private set => SetField(ref _amdPowerLimitStep, value); }
    private bool _isAmdVramFrequencyAvailable;
    public bool IsAmdVramFrequencyAvailable { get => _isAmdVramFrequencyAvailable; private set => SetField(ref _isAmdVramFrequencyAvailable, value); }
    private string _amdVramStatus = "Частота видеопамяти ещё не считана из драйвера.";
    public string AmdVramStatus { get => _amdVramStatus; private set => SetField(ref _amdVramStatus, value); }
    private double _amdVramFrequency;
    public double AmdVramFrequency { get => _amdVramFrequency; set => SetField(ref _amdVramFrequency, value); }
    private double _amdVramFrequencyMin;
    public double AmdVramFrequencyMin { get => _amdVramFrequencyMin; private set => SetField(ref _amdVramFrequencyMin, value); }
    private double _amdVramFrequencyMax;
    public double AmdVramFrequencyMax { get => _amdVramFrequencyMax; private set => SetField(ref _amdVramFrequencyMax, value); }
    private double _amdVramFrequencyStep = 1;
    public double AmdVramFrequencyStep { get => _amdVramFrequencyStep; private set => SetField(ref _amdVramFrequencyStep, value); }

    private NetworkAdapterInfo? _selectedAdapter;
    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetField(ref _selectedAdapter, value))
            {
                OnAdapterSelectionChanged();
            }
        }
    }

    public ObservableCollection<DnsServerItem> DnsServers { get; } = new(NetworkOptimizerService.GetPredefinedDnsList());

    private bool _isDnsBenchmarking;
    public bool IsDnsBenchmarking { get => _isDnsBenchmarking; set => SetField(ref _isDnsBenchmarking, value); }

    private bool _isNagleEnabled;
    public bool IsNagleEnabled { get => _isNagleEnabled; set => SetField(ref _isNagleEnabled, value); }

    // === АВТОЗАГРУЗКА ===
    public ObservableCollection<StartupItemModel> StartupItems { get; } = new();
    private bool _isStartupScanning;
    public bool IsStartupScanning { get => _isStartupScanning; set => SetField(ref _isStartupScanning, value); }

    public int StartupCount => StartupItems.Count;
    public int HighImpactStartupCount => StartupItems.Count(i => i.IsEnabled && i.Impact == StartupImpact.High);

    // === ДИСК & ОЧИСТКА ===
    private int _diskSubTabIndex = 0;
    public int DiskSubTabIndex
    {
        get => _diskSubTabIndex;
        set
        {
            if (SetField(ref _diskSubTabIndex, value))
            {
                OnPropertyChanged(nameof(IsDiskTreeSubTabVisible));
                OnPropertyChanged(nameof(IsDiskCleanupSubTabVisible));

                if (value == 1)
                {
                    _ = RefreshCleanupScanAsync();
                }
            }
        }
    }

    public bool IsDiskTreeSubTabVisible => DiskSubTabIndex == 0;
    public bool IsDiskCleanupSubTabVisible => DiskSubTabIndex == 1;

    public ObservableCollection<CleanupCategoryModel> CleanupCategories { get; } = new(DiskCleanupService.GetDefaultCategories());

    private bool _isScanningCleanable;
    public bool IsScanningCleanable { get => _isScanningCleanable; set => SetField(ref _isScanningCleanable, value); }

    public string TotalCleanableFormatted
    {
        get
        {
            long total = CleanupCategories.Where(c => c.IsSelected).Sum(c => c.SizeBytes);
            return DiskItemModel.FormatBytes(total);
        }
    }

    public ObservableCollection<DiskItemModel> AllSunburstSlices { get; } = new();
    public ObservableCollection<DiskItemModel> Level1ListItems { get; } = new();
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();
    public ObservableCollection<DriveSelectModel> AvailableDrives { get; } = new();

    private bool _isLoadingAvailableDrives;
    public bool IsLoadingAvailableDrives
    {
        get => _isLoadingAvailableDrives;
        private set
        {
            if (SetField(ref _isLoadingAvailableDrives, value))
                OnPropertyChanged(nameof(DriveInventoryStatus));
        }
    }

    public string DriveInventoryStatus => IsLoadingAvailableDrives
        ? "Поиск доступных накопителей…"
        : AvailableDrives.Count == 0
            ? "Готовые тома не найдены. Подключите диск или нажмите «Обновить» в разделе DiskTree."
            : string.Empty;

    private DiskItemModel? _hoveredDiskItem;
    public DiskItemModel? HoveredDiskItem { get => _hoveredDiskItem; set => SetField(ref _hoveredDiskItem, value); }

    private string _currentDiskPath = @"C:\";
    public string CurrentDiskPath { get => _currentDiskPath; set => SetField(ref _currentDiskPath, value); }

    private string _currentDiskFolderName = "C:\\";
    public string CurrentDiskFolderName
    {
        get => _currentDiskFolderName;
        set
        {
            if (SetField(ref _currentDiskFolderName, value))
                OnPropertyChanged(nameof(FolderNameText));
        }
    }

    private string _currentDiskTotalFormatted = "0 Б";
    public string CurrentDiskTotalFormatted
    {
        get => _currentDiskTotalFormatted;
        set
        {
            if (SetField(ref _currentDiskTotalFormatted, value))
                OnPropertyChanged(nameof(TotalSizeText));
        }
    }

    public string TotalSizeText => CurrentDiskTotalFormatted;
    public string FolderNameText => CurrentDiskFolderName;

    private bool _isDiskScanning;
    public bool IsDiskScanning { get => _isDiskScanning; set => SetField(ref _isDiskScanning, value); }

    public bool CanGoUpDisk => !string.IsNullOrEmpty(CurrentDiskPath) && Directory.GetParent(CurrentDiskPath) != null;

    private string _diskCacheFormatted = "0 КБ";
    public string DiskCacheFormatted { get => _diskCacheFormatted; set => SetField(ref _diskCacheFormatted, value); }

    // === DEVELOPER DOCTOR ===
    public ObservableCollection<DevEnvironmentComponent> DevComponents { get; } = new();
    public ObservableCollection<PathEntryItem> PathEntries { get; } = new();

    private bool _isDevScanning;
    public bool IsDevScanning
    {
        get => _isDevScanning;
        set
        {
            if (SetField(ref _isDevScanning, value))
                OnPropertyChanged(nameof(DevScanButtonText));
        }
    }

    public string DevScanButtonText => IsDevScanning ? "⏳ Сканирование..." : "🔄 Пересканировать";

    private int _devBrokenPathsCount;
    public int DevBrokenPathsCount
    {
        get => _devBrokenPathsCount;
        set
        {
            if (SetField(ref _devBrokenPathsCount, value))
                OnPropertyChanged(nameof(HasBrokenPaths));
        }
    }

    public bool HasBrokenPaths => DevBrokenPathsCount > 0;

    private string _inspectCommandInput = "python";
    public string InspectCommandInput
    {
        get => _inspectCommandInput;
        set
        {
            if (SetField(ref _inspectCommandInput, value))
            {
                InspectResult = null;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private CommandResolutionResult? _inspectResult;
    public CommandResolutionResult? InspectResult
    {
        get => _inspectResult;
        set
        {
            if (SetField(ref _inspectResult, value))
                OnPropertyChanged(nameof(HasInspectResult));
        }
    }

    public bool HasInspectResult => InspectResult != null;

    // === ПРИЛОЖЕНИЯ ===
    private int _appsSubTabIndex = 0;
    public int AppsSubTabIndex
    {
        get => _appsSubTabIndex;
        set
        {
            if (SetField(ref _appsSubTabIndex, value))
            {
                OnPropertyChanged(nameof(IsCatalogSubTabVisible));
                OnPropertyChanged(nameof(IsInstalledSubTabVisible));
                OnPropertyChanged(nameof(IsRestoreSubTabVisible));

                if (value == 1 && InstalledPrograms.Count == 0)
                    _ = RefreshInstalledProgramsAsync();
            }
        }
    }

    public bool IsCatalogSubTabVisible => AppsSubTabIndex == 0;
    public bool IsInstalledSubTabVisible => AppsSubTabIndex == 1;
    public bool IsRestoreSubTabVisible => AppsSubTabIndex == 2;

    public ObservableCollection<CatalogAppModel> CatalogApps { get; } = new(AppManagerService.GetCatalogApps());
    public ObservableCollection<InstalledProgramModel> InstalledPrograms { get; } = new();
    public ObservableCollection<AppLeftoverModel> AppLeftovers { get; } = new();
    public ICollectionView FilteredInstalledPrograms { get; }

    public int SelectedUninstallCount => InstalledPrograms.Count(program => program.IsSelected);
    public int SelectedLeftoverCount => AppLeftovers.Count(item => item.IsSelected);
    public bool HasAppLeftovers => AppLeftovers.Count > 0;

    private string _appsSearchText = string.Empty;
    public string AppsSearchText
    {
        get => _appsSearchText;
        set
        {
            if (SetField(ref _appsSearchText, value))
                FilteredInstalledPrograms?.Refresh();
        }
    }

    // === НАСТРОЙКИ & ТЕМЫ ===
    public ObservableCollection<ThemeModel> AvailableThemes { get; } = new(ThemeService.GetThemes());

    private string _selectedThemeId = "SlateCyan";
    public string SelectedThemeId
    {
        get => _selectedThemeId;
        set
        {
            if (SetField(ref _selectedThemeId, value))
                ThemeService.ApplyTheme(value);
        }
    }

    // === АУДИТ ===
    public ObservableCollection<TransactionRecord> AuditHistory { get; } = new();

    private bool _isHistoryOpen;
    public bool IsHistoryOpen { get => _isHistoryOpen; set => SetField(ref _isHistoryOpen, value); }

    // === ДРАЙВЕРЫ & КОНСОЛЬ ===
    public ObservableCollection<LogEntry> ConsoleLogs { get; } = new();
    private bool _isConsoleRunning;
    public bool IsConsoleRunning { get => _isConsoleRunning; set => SetField(ref _isConsoleRunning, value); }

    private double _driverProgress;
    public double DriverProgress { get => _driverProgress; set => SetField(ref _driverProgress, value); }

    private readonly List<DriverUpdateItem> _foundDriverUpdates = new();

    private bool _hasDriverUpdates;
    public bool HasDriverUpdates
    {
        get => _hasDriverUpdates;
        set => SetField(ref _hasDriverUpdates, value);
    }

    private string _foundDriversStatusText = string.Empty;
    public string FoundDriversStatusText
    {
        get => _foundDriversStatusText;
        set => SetField(ref _foundDriversStatusText, value);
    }

    public ICommand InstallDriverUpdatesCommand { get; }

    // === СТАТУС & ПОИСК ===
    private bool _isGlobalBusy;
    public bool IsGlobalBusy { get => _isGlobalBusy; set => SetField(ref _isGlobalBusy, value); }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => SetField(ref _progressValue, value); }

    private bool _isProgressIndeterminate;
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; set => SetField(ref _isProgressIndeterminate, value); }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
                FilteredTweaks.Refresh();
        }
    }

    private string _selectedCategory = "Информация";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetField(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(IsDashboardVisible));
                OnPropertyChanged(nameof(IsDiskVisible));
                OnPropertyChanged(nameof(IsDriversVisible));
                OnPropertyChanged(nameof(IsAppsVisible));
                OnPropertyChanged(nameof(IsDeveloperVisible));
                OnPropertyChanged(nameof(IsStartupVisible));
                OnPropertyChanged(nameof(IsNetworkVisible));
                OnPropertyChanged(nameof(IsAmdGpuVisible));
                OnPropertyChanged(nameof(IsNvidiaGpuVisible));
                OnPropertyChanged(nameof(IsGpuAuditVisible));
                OnPropertyChanged(nameof(IsBenchmarkVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsTweaksVisible));
                OnPropertyChanged(nameof(IsSearchVisible));
                OnPropertyChanged(nameof(CategorySubtitle));
                if (IsTweaksVisible)
                    FilteredTweaks.Refresh();

                if (value == "Информация")
                {
                    RefreshAvailableDrives();
                    RefreshPowerPlan();
                    _ = RecalculateHealthScoreAsync();
                }
                else if (value == "Диск")
                {
                    RefreshAvailableDrives();
                    CheckDiskCachePrompt();
                    if (DiskSubTabIndex == 0)
                        _ = NavigateDiskToPathAsync(CurrentDiskPath);
                    else
                        _ = RefreshCleanupScanAsync();
                }
                else if (value == "Developer" && DevComponents.Count == 0)
                {
                    _ = RefreshDeveloperDoctorAsync();
                }
                else if (value == "Автозагрузка" && StartupItems.Count == 0)
                {
                    _ = RefreshStartupItemsAsync();
                }
                else if (value == "Сеть & Пинг")
                {
                    RefreshNetworkAdapters();
                    _ = BenchmarkDnsCommandAction();
                }
                else if (IsGpuAuditVisible)
                {
                    _ = RefreshGpuAuditAsync();
                    if (value == "AMD GPU")
                        _ = RefreshAmdGpuTuningAsync();
                }
                else if (value == "Настройки")
                {
                    UpdateCacheSizeDisplay();
                }
            }
        }
    }

    public int PendingCount => _allTweaks.Count(t => t.HasPendingChange);
    public bool HasPendingChanges => PendingCount > 0;

    // === КОМАНДЫ ===
    public ICommand SelectCategoryCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ApplyAllPendingCommand { get; }
    public ICommand DiscardAllPendingCommand { get; }
    public ICommand CreateRestorePointCommand { get; }
    public ICommand CleanTempCommand { get; }
    public ICommand CleanMemoryCommand { get; }
    public ICommand CleanTempAndRamCommand { get; }
    public ICommand RestartExplorerCommand { get; }
    public ICommand SearchDriverUpdatesCommand { get; }
    public ICommand BackupDriversCommand { get; }
    public ICommand RestoreDriversCommand { get; }
    public ICommand InstallVCRuntimesCommand { get; }
    public ICommand SelectDriveCommand { get; }
    public ICommand DrillDownDiskCommand { get; }
    public ICommand GoUpDiskCommand { get; }
    public ICommand RefreshDiskCommand { get; }
    public ICommand ChooseDiskFolderCommand { get; }
    public ICommand OpenItemInExplorerCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand DeleteDiskItemCommand { get; }
    public ICommand NavigateBreadcrumbCommand { get; }
    public ICommand SelectThemeCommand { get; }
    public ICommand SelectAppsSubTabCommand { get; }
    public ICommand InstallCatalogAppCommand { get; }
    public ICommand UninstallProgramCommand { get; }
    public ICommand UninstallSelectedProgramsCommand { get; }
    public ICommand ScanAppLeftoversCommand { get; }
    public ICommand RemoveSelectedLeftoversCommand { get; }
    public ICommand RefreshInstalledProgramsCommand { get; }
    public ICommand RestoreStoreCommand { get; }
    public ICommand RestoreAllUwpCommand { get; }
    public ICommand ClearDiskCacheCommand { get; }
    public ICommand ToggleHistoryCommand { get; }
    public ICommand RollbackTransactionCommand { get; }
    public ICommand ApplyAllRecommendationsCommand { get; }
    public ICommand ScanDeveloperDoctorCommand { get; }
    public ICommand InspectCommandOwnerCommand { get; }
    public ICommand CleanBrokenPathsCommand { get; }
    public ICommand SelectDiskSubTabCommand { get; }
    public ICommand ScanCleanupCategoriesCommand { get; }
    public ICommand ExecuteCleanDiskCategoriesCommand { get; }
    public ICommand ScanStartupCommand { get; }
    public ICommand ToggleStartupItemCommand { get; }

    // Сеть & DNS
    public ICommand RunDnsBenchmarkCommand { get; }
    public ICommand ApplyDnsServerCommand { get; }
    public ICommand ResetDnsToDhcpCommand { get; }
    public ICommand FlushDnsCacheCommand { get; }
    public ICommand ResetWinsockCommand { get; }
    public ICommand ToggleNagleCommand { get; }
    public ICommand RefreshAdaptersCommand { get; }
    public ICommand RefreshGpuAuditCommand { get; }
    public ICommand ApplyAmdGpuTuningCommand { get; }
    public ICommand RefreshAmdGpuTuningCommand { get; }
    public ICommand ResetAmdGpuTuningCommand { get; }
    public ICommand ApplyAmdPowerLimitCommand { get; }
    public ICommand ApplyAmdVramFrequencyCommand { get; }
    public ICommand AddHighPerformanceGameProfileCommand { get; }
    public ICommand RemoveGpuGameProfileCommand { get; }
    public ICommand SelectGpuModuleTabCommand { get; }
    public ICommand OpenGpuSystemSettingsCommand { get; }
    public ICommand ImportBenchmarkCsvCommand { get; }
    public ICommand CaptureBenchmarkCommand { get; }

    // Сетевой реаниматор
    public ICommand RescanPnpDevicesCommand { get; }
    public ICommand InstallDriverFromFolderCommand { get; }
    public ICommand HardResetNetworkStackCommand { get; }
    public ICommand InstallLoopbackAdapterCommand { get; }
    public ICommand ShowRndisHelpCommand { get; }

    public MainViewModel()
    {
        LoadBenchmarkResults();
        OpenUpdateUrlCommand = new RelayCommand(() => UpdateCheckerService.OpenReleaseUrl(ReleaseUrl));
        DismissUpdateCommand = new RelayCommand(() => HasUpdateAvailable = false);
        CheckForUpdatesCommand = new RelayCommand(async () => await ExecuteCheckForUpdatesAsync(showResult: true), () => !IsUpdateChecking && !IsUpdateInstalling);
        InstallUpdateCommand = new RelayCommand(async () => await ExecuteInstallUpdateAsync(), () => CanInstallUpdate);

        ToggleHealthDetailsCommand = new RelayCommand(() => IsHealthDetailsOpen = !IsHealthDetailsOpen);
        RefreshGpuAuditCommand = new RelayCommand(async () => await RefreshGpuAuditAsync(), () => !IsGlobalBusy);
        RefreshAmdGpuTuningCommand = new RelayCommand(async () => await RefreshAmdGpuTuningAsync(), () => !IsAmdGpuTuningBusy);
        ApplyAmdGpuTuningCommand = new RelayCommand(async () => await ApplyAmdGpuTuningAsync(), () => IsAmdGpuTuningAvailable && !IsAmdGpuTuningBusy);
        ResetAmdGpuTuningCommand = new RelayCommand(async () => await ResetAmdGpuTuningAsync(), () => HasAmdGpu && !IsAmdGpuTuningBusy);
        ApplyAmdPowerLimitCommand = new RelayCommand(async () => await ApplyAmdPowerLimitAsync(), () => IsAmdPowerLimitAvailable && !IsAmdGpuTuningBusy);
        ApplyAmdVramFrequencyCommand = new RelayCommand(async () => await ApplyAmdVramFrequencyAsync(), () => IsAmdVramFrequencyAvailable && !IsAmdGpuTuningBusy);
        AddHighPerformanceGameProfileCommand = new RelayCommand(ExecuteAddHighPerformanceGameProfile);
        RemoveGpuGameProfileCommand = new RelayCommand(ExecuteRemoveGpuGameProfile);
        SelectGpuModuleTabCommand = new RelayCommand(parameter =>
        {
            if (int.TryParse(parameter?.ToString(), out var tab)) GpuModuleTabIndex = tab;
        });
        OpenGpuSystemSettingsCommand = new RelayCommand(ExecuteOpenGpuSystemSettings);
        ImportBenchmarkCsvCommand = new RelayCommand(ExecuteImportBenchmarkCsv);
        CaptureBenchmarkCommand = new RelayCommand(async parameter => await ExecuteCaptureBenchmarkAsync(parameter), _ => !IsBenchmarkCapturing);

        EnableUltimatePerformanceCommand = new RelayCommand(async () =>
        {
            IsGlobalBusy = true;
            StatusMessage = "Активация схемы питания «Максимальная производительность»...";
            try
            {
                await Task.Run(() =>
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(2000);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/setactive e9a42b02-d5df-448d-aa00-03f14749eb61",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(2000);
                });

                RefreshPowerPlan();
                MessageBox.Show("Режим «Максимальная производительность» (Ultimate Performance) успешно активирован!\nПарковка ядер процессора отключена.", "Электропитание", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка настройки питания: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
            }
        });

        RunSfcScanCommand = new RelayCommand(async () =>
        {
            IsGlobalBusy = true;
            IsProgressIndeterminate = true;
            StatusMessage = "Проверка целостности системных файлов (SFC Scan)...";
            try
            {
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sfc.exe",
                        Arguments = "/scannow",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var p = Process.Start(psi);
                    p?.WaitForExit();
                });
                MessageBox.Show("Проверка целостности системы (sfc /scannow) успешно завершена!", "SFC Scan", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { IsGlobalBusy = false; StatusMessage = string.Empty; IsProgressIndeterminate = false; }
        });

        RunDismRestoreCommand = new RelayCommand(async () =>
        {
            IsGlobalBusy = true;
            IsProgressIndeterminate = true;
            StatusMessage = "Восстановление хранилища компонентов DISM...";
            try
            {
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = "/online /cleanup-image /restorehealth",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var p = Process.Start(psi);
                    p?.WaitForExit();
                });
                MessageBox.Show("Восстановление системного образа DISM успешно завершено!", "DISM Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { IsGlobalBusy = false; StatusMessage = string.Empty; IsProgressIndeterminate = false; }
        });

        FilteredTweaks = CollectionViewSource.GetDefaultView(_allTweaks);
        FilteredTweaks.Filter = obj =>
        {
            if (obj is not TweakViewModel t) return false;
            bool hasSearch = !string.IsNullOrWhiteSpace(SearchText);
            if (hasSearch)
            {
                return t.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                       t.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                       t.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                       t.UserWhy.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                       t.TechDetails.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            }
            return SelectedCategory == "Все" || t.Category == SelectedCategory;
        };

        FilteredInstalledPrograms = CollectionViewSource.GetDefaultView(InstalledPrograms);
        FilteredInstalledPrograms.Filter = obj =>
        {
            if (obj is not InstalledProgramModel p) return false;
            return string.IsNullOrWhiteSpace(AppsSearchText) || p.DisplayName.Contains(AppsSearchText, StringComparison.OrdinalIgnoreCase);
        };

        foreach (var c in CleanupCategories)
        {
            c.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CleanupCategoryModel.IsSelected) or nameof(CleanupCategoryModel.SizeBytes))
                    OnPropertyChanged(nameof(TotalCleanableFormatted));
            };
        }

        SelectCategoryCommand = new RelayCommand(p => { if (p is string cat) SelectedCategory = cat; });
        SelectThemeCommand = new RelayCommand(p =>
        {
            if (p is string id)
            {
                SelectedThemeId = id;
                ThemeService.ApplyTheme(id);
            }
        });

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

        SelectAppsSubTabCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out int idx)) AppsSubTabIndex = idx;
        });

        SelectDiskSubTabCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out int idx)) DiskSubTabIndex = idx;
        });

        ScanStartupCommand = new RelayCommand(async () => await RefreshStartupItemsAsync(), () => !IsStartupScanning);

        ToggleStartupItemCommand = new RelayCommand(async p =>
        {
            if (p is StartupItemModel item)
            {
                bool targetState = item.IsEnabled;
                await StartupService.SetStartupItemStateAsync(item, targetState);
                OnPropertyChanged(nameof(HighImpactStartupCount));
            }
        });

        RefreshAdaptersCommand = new RelayCommand(RefreshNetworkAdapters);
        RunDnsBenchmarkCommand = new RelayCommand(async () => await BenchmarkDnsCommandAction(), () => !IsDnsBenchmarking);

        ApplyDnsServerCommand = new RelayCommand(async p =>
        {
            if (p is DnsServerItem server && SelectedAdapter != null)
            {
                try
                {
                    IsGlobalBusy = true;
                    StatusMessage = $"Применение {server.Name} на адаптер '{SelectedAdapter.Name}'...";
                    await NetworkOptimizerService.ApplyDnsAsync(SelectedAdapter.Name, server.PrimaryIp, server.SecondaryIp);
                    RefreshNetworkAdapters();
                    MessageBox.Show($"DNS успешно переключен на {server.Name} ({server.PrimaryIp}) для адаптера '{SelectedAdapter.Name}'", "Сетевой оптимизатор", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка DNS", MessageBoxButton.OK, MessageBoxImage.Error); }
                finally
                {
                    IsGlobalBusy = false;
                    StatusMessage = string.Empty;
                }
            }
        });

        ResetDnsToDhcpCommand = new RelayCommand(async () =>
        {
            if (SelectedAdapter != null)
            {
                try
                {
                    IsGlobalBusy = true;
                    StatusMessage = $"Сброс DNS на DHCP для '{SelectedAdapter.Name}'...";
                    await NetworkOptimizerService.ResetDnsToDhcpAsync(SelectedAdapter.Name);
                    RefreshNetworkAdapters();
                    MessageBox.Show("DNS сброшен в автоматический режим.", "Сетевой оптимизатор", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
                finally
                {
                    IsGlobalBusy = false;
                    StatusMessage = string.Empty;
                }
            }
        });

        FlushDnsCacheCommand = new RelayCommand(async () =>
        {
            try
            {
                IsGlobalBusy = true;
                StatusMessage = "Очистка кэша DNS...";
                await NetworkOptimizerService.FlushDnsCacheAsync();
                MessageBox.Show("Кэш DNS успешно очищен (ipconfig /flushdns).", "Сетевой оптимизатор", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
            }
        });

        ResetWinsockCommand = new RelayCommand(async () =>
        {
            var res = MessageBox.Show("Сбросить сетевой стек Winsock и протоколы TCP/IP?\n\nДля полного применения потребуется перезагрузка ПК.", "Сброс сети", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    IsGlobalBusy = true;
                    StatusMessage = "Сброс каталога Winsock и протоколов TCP/IP...";
                    await NetworkOptimizerService.ResetWinsockAndTcpAsync();
                    MessageBox.Show("Сетевой стек успешно сброшен. Рекомендуется перезагрузить систему.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    IsGlobalBusy = false;
                    StatusMessage = string.Empty;
                }
            }
        });

        ToggleNagleCommand = new RelayCommand(async () =>
        {
            bool newState = !IsNagleEnabled;
            IsGlobalBusy = true;
            StatusMessage = newState ? "Включение режима Ultra Low Latency..." : "Отключение твиков Nagle...";
            try
            {
                string? guid = SelectedAdapter?.Id;
                await NetworkOptimizerService.OptimizeNagleAlgorithmAsync(guid, newState);
                IsNagleEnabled = newState;
                MessageBox.Show(newState ? "Алгоритм Nagle отключен (TCPNoDelay=1, TcpAckFrequency=1).\nПакеты в играх отправляются без микрозадержек!" : "Алгоритм Nagle возвращен к стандартным настройкам Windows.", "Игровая задержка", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
            }
        });

        RescanPnpDevicesCommand = new RelayCommand(async () =>
        {
            IsGlobalBusy = true;
            StatusMessage = "Сканирование оборудования (PnP Rescan)...";
            try
            {
                var (ok, outMsg) = await NetworkRescueService.RescanPnpDevicesAsync();
                RefreshNetworkAdapters();
                MessageBox.Show($"Сканирование шины оборудования завершено.\n\n{outMsg}", "PnP Scan", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally { IsGlobalBusy = false; StatusMessage = string.Empty; }
        });

        InstallDriverFromFolderCommand = new RelayCommand(async () =>
        {
            var dlg = new OpenFolderDialog { Title = "Выберите папку с распакованными INF-драйверами" };
            if (dlg.ShowDialog() == true)
            {
                IsGlobalBusy = true;
                StatusMessage = "Пакетная интеграция драйверов через PnPUtil...";
                try
                {
                    var (ok, outMsg) = await NetworkRescueService.InstallDriversFromFolderAsync(dlg.FolderName);
                    RefreshNetworkAdapters();
                    MessageBox.Show(outMsg, "Инсталляция драйверов", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                finally { IsGlobalBusy = false; StatusMessage = string.Empty; }
            }
        });

        HardResetNetworkStackCommand = new RelayCommand(async () =>
        {
            var res = MessageBox.Show(
                "Выполнить полный сброс и переустановку всех сетевых карт и протоколов (netcfg -d)?\n\n" +
                "После выполнения потребуется обязательная перезагрузка компьютера.",
                "Жесткий сброс адаптеров",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                IsGlobalBusy = true;
                StatusMessage = "Выполнение жесткого сброса сетевого стека...";
                try
                {
                    var (ok, outMsg) = await NetworkRescueService.HardResetNetworkStackAsync();
                    MessageBox.Show($"{outMsg}\n\nПожалуйста, перезагрузите систему.", "Сброс завершен", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally { IsGlobalBusy = false; StatusMessage = string.Empty; }
            }
        });

        InstallLoopbackAdapterCommand = new RelayCommand(async () =>
        {
            IsGlobalBusy = true;
            StatusMessage = "Развертывание Microsoft KM-TEST Loopback Adapter...";
            try
            {
                var (ok, outMsg) = await NetworkRescueService.InstallEmergencyLoopbackAdapterAsync();
                RefreshNetworkAdapters();
                MessageBox.Show(outMsg, "Loopback Adapter", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally { IsGlobalBusy = false; StatusMessage = string.Empty; }
        });

        ShowRndisHelpCommand = new RelayCommand(() =>
        {
            MessageBox.Show(
                "ЭКСТРЕННЫЙ ИНТЕРНЕТ ЧЕРЕЗ СМАРТФОН (RNDIS):\n\n" +
                "1. Подключите телефон к компьютеру по USB-кабелю.\n" +
                "2. В настройках телефона включите «Точка доступа» ➔ «USB-модем» (USB Tethering).\n" +
                "3. Драйвер RNDIS (usb8023.sys) встроен во все версии Windows по умолчанию.\n" +
                "4. Сеть появится за несколько секунд, после чего вы сможете загрузить родные драйверы.",
                "Мастер аварийного интернета",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });

        CleanMemoryCommand = new RelayCommand(async () =>
        {
            IsGlobalBusy = true;
            StatusMessage = "Оптимизация оперативной памяти...";
            try
            {
                await Task.Run(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    foreach (var proc in Process.GetProcesses())
                    {
                        try { EmptyWorkingSet(proc.Handle); } catch { }
                    }
                });

                MessageBox.Show(
                    "Оперативная память успешно оптимизирована!\nРабочий набор процессов выгружен в системный кэш.",
                    "Очистка RAM",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
            }
        }, () => !IsGlobalBusy);

        CleanTempAndRamCommand = new RelayCommand(async () =>
        {
            IsGlobalBusy = true;
            StatusMessage = "Комплексная очистка Temp и памяти...";
            try
            {
                long freedBytes = await SystemUtility.CleanTempFilesAsync();
                await Task.Run(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    foreach (var proc in Process.GetProcesses())
                    {
                        try { EmptyWorkingSet(proc.Handle); } catch { }
                    }
                });

                double freedMb = freedBytes / (1024.0 * 1024.0);
                MessageBox.Show(
                    $"Комплексная очистка завершена!\n\n" +
                    $"• Удалено временных файлов Temp: {freedMb:F1} МБ\n" +
                    $"• Рабочий набор процессов RAM полностью оптимизирован.",
                    "Очистка системы",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                SysInfo = await SystemInfoService.GetSystemInfoAsync();
                await RecalculateHealthScoreAsync();
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
            }
        }, () => !IsGlobalBusy);

        ScanCleanupCategoriesCommand = new RelayCommand(async () => await RefreshCleanupScanAsync(), () => !IsScanningCleanable);

        ExecuteCleanDiskCategoriesCommand = new RelayCommand(async () =>
        {
            var selected = CleanupCategories.Where(c => c.IsSelected && c.SizeBytes > 0).ToList();
            if (selected.Count == 0) return;

            var res = MessageBox.Show($"Очистить выбранные категории ({TotalCleanableFormatted})?", "Очистка диска", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                IsScanningCleanable = true;
                try
                {
                    long freed = await DiskCleanupService.CleanCategoriesAsync(selected);

                    var record = new TransactionRecord
                    {
                        TweakId = "disk_clean",
                        TweakName = "Глубокая очистка диска",
                        Category = "Диск",
                        IsApplied = true,
                        Details = $"Освобождено {DiskItemModel.FormatBytes(freed)} системного мусора и кэшей"
                    };
                    await TransactionManager.CommitTransactionAsync(record);
                    RefreshAuditHistory();

                    await RefreshCleanupScanAsync();
                    await RecalculateHealthScoreAsync();
                    MessageBox.Show($"Успешно освобождено: {DiskItemModel.FormatBytes(freed)}", "Очистка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    IsScanningCleanable = false;
                }
            }
        }, () => CleanupCategories.Any(c => c.IsSelected && c.SizeBytes > 0) && !IsScanningCleanable);

        ToggleHistoryCommand = new RelayCommand(() =>
        {
            RefreshAuditHistory();
            IsHistoryOpen = !IsHistoryOpen;
        });

        RollbackTransactionCommand = new RelayCommand(async p =>
        {
            if (p is Guid txId)
            {
                IsGlobalBusy = true;
                StatusMessage = "Выполнение отката операции...";
                try
                {
                    var (ok, msg) = await TransactionManager.RollbackTransactionAsync(txId);
                    RefreshAuditHistory();
                    foreach (var t in _allTweaks) t.RefreshState();
                    await RecalculateHealthScoreAsync();
                    MessageBox.Show(msg, ok ? "Успех" : "Внимание", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                finally
                {
                    IsGlobalBusy = false;
                    StatusMessage = string.Empty;
                }
            }
        });

        ApplyAllRecommendationsCommand = new RelayCommand(async () =>
        {
            if (IsGlobalBusy) return;

            var targetTweaks = DashboardTweaks.Where(t => !t.CurrentState || t.HasPendingChange).ToList();

            if (targetTweaks.Count == 0)
            {
                targetTweaks = _allTweaks.Where(t => !t.CurrentState && t.Risk == RiskLevel.Safe && t.Category != "Интерфейс").Take(5).ToList();
            }

            if (targetTweaks.Count == 0)
            {
                MessageBox.Show("Все ключевые рекомендуемые твики уже активированы и система максимально оптимизирована!", "Оптимизация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsGlobalBusy = true;
            ProgressValue = 0;
            IsProgressIndeterminate = false;
            StatusMessage = "Применение рекомендуемых твиков...";

            try
            {
                int total = targetTweaks.Count;
                int current = 0;

                foreach (var tw in targetTweaks)
                {
                    tw.TargetState = true;
                    StatusMessage = $"Применение: {tw.Name}...";

                    var record = await tw.CommitChangeWithTransactionAsync();
                    if (record != null)
                    {
                        await TransactionManager.CommitTransactionAsync(record);
                    }

                    current++;
                    ProgressValue = (double)current / total * 100;
                    await Task.Delay(40);
                }

                foreach (var t in _allTweaks) t.RefreshState();
                RefreshAuditHistory();

                await RecalculateHealthScoreAsync();

                StatusMessage = "Рекомендуемые твики успешно активированы!";
                await Task.Delay(300);

                MessageBox.Show($"Успешно применены и оптимизированы {total} параметров системы!", "Оптимизация завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка применения твиков: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
                OnPropertyChanged(nameof(PendingCount));
                OnPropertyChanged(nameof(HasPendingChanges));
                CommandManager.InvalidateRequerySuggested();
            }
        });

        ScanDeveloperDoctorCommand = new RelayCommand(async () =>
        {
            if (!IsDevScanning)
            {
                await RefreshDeveloperDoctorAsync();
            }
        });

        CleanBrokenPathsCommand = new RelayCommand(async () =>
        {
            if (DevBrokenPathsCount == 0) return;

            var confirm = MessageBox.Show(
                $"Обнаружено {DevBrokenPathsCount} проблемных записей в PATH.\n\n" +
                "Удалить их и сохранить снимок для отката?",
                "Очистка PATH",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                IsDevScanning = true;
                try
                {
                    var userSnap = TransactionManager.CreateRegistrySnapshot(RegistryHive.CurrentUser, @"Environment", "Path");
                    var sysSnap = TransactionManager.CreateRegistrySnapshot(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path");

                    int removed = await DeveloperDoctorService.CleanBrokenPathEntriesAsync();

                    var record = new TransactionRecord
                    {
                        TweakId = "path_cleanup",
                        TweakName = "Очистка битых путей PATH",
                        Category = "Developer Doctor",
                        IsApplied = true,
                        Details = $"Удалено {removed} устаревших путей из PATH"
                    };

                    if (userSnap != null) record.Snapshots.Add(userSnap);
                    if (sysSnap != null) record.Snapshots.Add(sysSnap);

                    await TransactionManager.CommitTransactionAsync(record);
                    RefreshAuditHistory();

                    await RefreshDeveloperDoctorAsync();
                    MessageBox.Show($"Успешно очищено записей: {removed}", "PATH Doctor", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    IsDevScanning = false;
                }
            }
        }, () => HasBrokenPaths && !IsDevScanning);

        InspectCommandOwnerCommand = new RelayCommand(async () =>
        {
            if (!string.IsNullOrWhiteSpace(InspectCommandInput))
            {
                try
                {
                    InspectResult = await DeveloperDoctorService.ResolveCommandOwnerAsync(InspectCommandInput.Trim());
                }
                catch (Exception ex)
                {
                    InspectResult = new CommandResolutionResult
                    {
                        Command = InspectCommandInput,
                        Found = false,
                        ResolvedExecutablePath = $"Ошибка: {ex.Message}"
                    };
                }
            }
        }, () => !string.IsNullOrWhiteSpace(InspectCommandInput));

        InstallCatalogAppCommand = new RelayCommand(async p =>
        {
            if (p is CatalogAppModel app && !app.IsInstalling)
            {
                try
                {
                    app.IsInstalling = true;
                    IsGlobalBusy = true;
                    IsProgressIndeterminate = true;
                    StatusMessage = $"Установка {app.Name} через Winget...";
                    await AppManagerService.InstallAppViaWingetAsync(app.WingetId, AddLog);
                    _ = RefreshInstalledProgramsAsync();
                }
                catch (Exception ex)
                {
                    AddLog($"Ошибка установки: {ex.Message}", LogLevel.Error);
                    MessageBox.Show($"Не удалось установить {app.Name}.\n{ex.Message}", "Ошибка Winget", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    app.IsInstalling = false;
                    IsGlobalBusy = false;
                    StatusMessage = string.Empty;
                    IsProgressIndeterminate = false;
                }
            }
        });

        UninstallProgramCommand = new RelayCommand(p =>
        {
            try
            {
                if (p is InstalledProgramModel prog)
                {
                    var res = MessageBox.Show($"Запустить деинсталлятор для:\n\n{prog.DisplayName}?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes)
                    {
                        AppManagerService.RunUninstall(prog.UninstallString);
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        });

        UninstallSelectedProgramsCommand = new RelayCommand(async () => await ExecuteBatchUninstallAsync(),
            () => SelectedUninstallCount > 0 && !IsGlobalBusy);
        ScanAppLeftoversCommand = new RelayCommand(ExecuteScanAppLeftovers);
        RemoveSelectedLeftoversCommand = new RelayCommand(async () => await ExecuteRemoveSelectedLeftoversAsync(), () => SelectedLeftoverCount > 0 && !IsGlobalBusy);

        RefreshInstalledProgramsCommand = new RelayCommand(async () => await RefreshInstalledProgramsAsync());

        RestoreStoreCommand = new RelayCommand(async () =>
        {
            try
            {
                IsGlobalBusy = true;
                IsProgressIndeterminate = true;
                StatusMessage = "Восстановление Microsoft Store...";
                await AppManagerService.RestoreMicrosoftStoreAsync(AddLog);
            }
            catch (Exception ex) { AddLog(ex.Message, LogLevel.Error); }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
                IsProgressIndeterminate = false;
            }
        });

        RestoreAllUwpCommand = new RelayCommand(async () =>
        {
            try
            {
                IsGlobalBusy = true;
                IsProgressIndeterminate = true;
                StatusMessage = "Перерегистрация всех UWP приложений...";
                await AppManagerService.RestoreAllDefaultUwpAppsAsync(AddLog);
            }
            catch (Exception ex) { AddLog(ex.Message, LogLevel.Error); }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
                IsProgressIndeterminate = false;
            }
        });

        ClearDiskCacheCommand = new RelayCommand(() =>
        {
            try
            {
                DiskCacheService.ClearAllCache();
                UpdateCacheSizeDisplay();
                MessageBox.Show("Кэш DiskTree успешно очищен!", "Кэш", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        });

        ApplyAllPendingCommand = new RelayCommand(async () => await ExecuteApplyPendingAsync(), () => HasPendingChanges && !IsGlobalBusy);
        DiscardAllPendingCommand = new RelayCommand(ExecuteDiscardPending, () => HasPendingChanges && !IsGlobalBusy);

        CreateRestorePointCommand = new RelayCommand(async () => await ExecuteCreateRestorePointAsync(), () => !IsGlobalBusy);
        CleanTempCommand = new RelayCommand(async () => await ExecuteCleanTempAsync(), () => !IsGlobalBusy);
        RestartExplorerCommand = new RelayCommand(async () => await SystemUtility.RestartExplorerAsync(), () => !IsGlobalBusy);

        SearchDriverUpdatesCommand = new RelayCommand(async () => await ExecuteSearchDriverUpdatesAsync(), () => !IsConsoleRunning);
        InstallDriverUpdatesCommand = new RelayCommand(async () => await ExecuteInstallDriverUpdatesAsync(), () => HasDriverUpdates && !IsConsoleRunning);
        BackupDriversCommand = new RelayCommand(async () => await ExecuteBackupDriversAsync(), () => !IsConsoleRunning);
        RestoreDriversCommand = new RelayCommand(async () => await ExecuteRestoreDriversAsync(), () => !IsConsoleRunning);
        InstallVCRuntimesCommand = new RelayCommand(async () => await ExecuteInstallVCRuntimesAsync(), () => !IsConsoleRunning);

        SelectDriveCommand = new RelayCommand(async p =>
        {
            try
            {
                if (p is string path && Directory.Exists(path))
                {
                    SelectedCategory = "Диск";
                    DiskSubTabIndex = 0;
                    await NavigateDiskToPathAsync(path);
                }
            }
            catch (Exception ex) { AddLog($"Диск недоступен: {ex.Message}", LogLevel.Warning); }
        });

        DrillDownDiskCommand = new RelayCommand(async p =>
        {
            try
            {
                if (p is DiskItemModel item && item.IsFolder && Directory.Exists(item.FullPath))
                    await NavigateDiskToPathAsync(item.FullPath);
            }
            catch { }
        });

        GoUpDiskCommand = new RelayCommand(async () =>
        {
            try
            {
                var parent = Directory.GetParent(CurrentDiskPath);
                if (parent != null) await NavigateDiskToPathAsync(parent.FullName);
            }
            catch { }
        }, () => CanGoUpDisk && !IsDiskScanning);

        RefreshDiskCommand = new RelayCommand(async () => await NavigateDiskToPathAsync(CurrentDiskPath, forceRefresh: true), () => !IsDiskScanning);
        ChooseDiskFolderCommand = new RelayCommand(async () => await ExecuteChooseDiskFolderAsync(), () => !IsDiskScanning);

        NavigateBreadcrumbCommand = new RelayCommand(async p =>
        {
            try
            {
                if (p is BreadcrumbItem b && Directory.Exists(b.FullPath))
                    await NavigateDiskToPathAsync(b.FullPath);
            }
            catch { }
        });

        OpenItemInExplorerCommand = new RelayCommand(p =>
        {
            try
            {
                if (p is DiskItemModel item)
                {
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{item.FullPath}\"", UseShellExecute = true });
                }
            }
            catch { }
        });

        CopyPathCommand = new RelayCommand(p => { if (p is DiskItemModel item) Clipboard.SetText(item.FullPath); });

        DeleteDiskItemCommand = new RelayCommand(async p =>
        {
            try
            {
                string? targetPath = null;
                string? itemName = null;

                if (p is DiskItemModel item)
                {
                    targetPath = item.FullPath;
                    itemName = item.Name;
                }
                else if (p is string pathStr)
                {
                    targetPath = pathStr;
                    itemName = Path.GetFileName(pathStr);
                }

                if (!IsSafeDiskDeletionTarget(p as DiskItemModel, targetPath)) return;

                var res = MessageBox.Show(
                    $"Вы действительно хотите переместить в Корзину?\n\n{targetPath}",
                    "Перемещение в Корзину",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Yes)
                {
                    IsGlobalBusy = true;
                    StatusMessage = $"Перемещение в корзину: {itemName}...";

                    bool success = await Task.Run(() =>
                    {
                        try
                        {
                            if (Directory.Exists(targetPath))
                            {
                                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                                    targetPath,
                                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                                return true;
                            }
                            if (File.Exists(targetPath))
                            {
                                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                    targetPath,
                                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                                return true;
                            }
                        }
                        catch { }
                        return false;
                    });

                    if (success)
                    {
                        DiskCacheService.ClearAllCache();
                        await NavigateDiskToPathAsync(CurrentDiskPath, forceRefresh: true);
                        RefreshAvailableDrives();
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Не удалось переместить в Корзину:\n{targetPath}\n\nВозможно, файл заблокирован другим процессом или требуются права Администратора.",
                            "Ошибка удаления",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
            }
        });

        RegisterTweaks();
        RefreshAuditHistory();
        RebuildBreadcrumbs(CurrentDiskPath);
        StartRealTimeTelemetry();

    }

    private bool _initializationStarted;

    // Navigation must know about the installed vendor before the asynchronous audit has
    // finished, otherwise the only entry point to the GPU module would be hidden.
    private void PrimeGpuNavigation()
    {
        try
        {
            var (hasAmd, hasNvidia) = GpuAuditService.DetectSupportedVendors();
            HasAmdGpu = hasAmd;
            HasNvidiaGpu = hasNvidia;
        }
        catch
        {
            // The full audit will report an unavailable adapter without blocking startup.
        }
    }

    public Task StartInitializationAsync()
    {
        if (_initializationStarted) return Task.CompletedTask;
        _initializationStarted = true;
        return InitializeAppAsync();
    }

    public async Task ApplySingleTweakImmediateAsync(TweakViewModel tw)
    {
        if (IsGlobalBusy) return;

        IsGlobalBusy = true;
        StatusMessage = $"Применение: {tw.Name}...";

        try
        {
            tw.TargetState = true;
            var record = await tw.CommitChangeWithTransactionAsync();
            if (record != null)
            {
                await TransactionManager.CommitTransactionAsync(record);
            }

            tw.RefreshState();
            RefreshAuditHistory();
            await RecalculateHealthScoreAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка применения твика: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsGlobalBusy = false;
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(HasPendingChanges));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void RefreshPowerPlan()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = "/getactivescheme",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string line = p.StandardOutput.ReadToEnd();
                p.WaitForExit(1000);
                int openIdx = line.IndexOf('(');
                int closeIdx = line.IndexOf(')');
                if (openIdx >= 0 && closeIdx > openIdx)
                {
                    ActivePowerPlanName = line.Substring(openIdx + 1, closeIdx - openIdx - 1);
                    return;
                }
            }
        }
        catch { }
        ActivePowerPlanName = "Сбалансированная";
    }

    private void RefreshNetworkAdapters()
    {
        try
        {
            var adapters = NetworkOptimizerService.GetAllPhysicalAdapters();
            AvailableAdapters.Clear();
            foreach (var a in adapters) AvailableAdapters.Add(a);

            if (SelectedAdapter == null || !AvailableAdapters.Any(a => a.Name == SelectedAdapter.Name))
            {
                SelectedAdapter = AvailableAdapters.FirstOrDefault(a => a.IsDefaultRoute)
                    ?? AvailableAdapters.FirstOrDefault(a => a.IsUp)
                    ?? AvailableAdapters.FirstOrDefault();
            }
            else
            {
                OnAdapterSelectionChanged();
            }
        }
        catch { }
    }

    private Task? _gpuAuditRefreshTask;

    private Task RefreshGpuAuditAsync()
    {
        if (_gpuAuditRefreshTask is { IsCompleted: false })
            return _gpuAuditRefreshTask;

        _gpuAuditRefreshTask = RefreshGpuAuditCoreAsync();
        return _gpuAuditRefreshTask;
    }

    private async Task RefreshGpuAuditCoreAsync()
    {
        try
        {
            GpuAuditStatus = "Считывание конфигурации GPU и драйвера...";
            var snapshot = await GpuAuditService.CollectAsync();

            GpuAdapters.Clear();
            foreach (var adapter in snapshot.Adapters) GpuAdapters.Add(adapter);

            GpuAuditChecks.Clear();
            foreach (var check in snapshot.Checks) GpuAuditChecks.Add(check);

            GpuWindowsChecks.Clear();
            foreach (var check in snapshot.WindowsChecks) GpuWindowsChecks.Add(check);

            RefreshGpuGameProfiles();

            HasAmdGpu = snapshot.HasAmd;
            HasNvidiaGpu = snapshot.HasNvidia;
            ActiveGpuAdapter = SelectedCategory == "NVIDIA GPU"
                ? snapshot.Adapters.FirstOrDefault(adapter => adapter.Vendor == GpuVendor.Nvidia)
                : snapshot.Adapters.FirstOrDefault(adapter => adapter.Vendor == GpuVendor.Amd)
                    ?? snapshot.Adapters.FirstOrDefault(adapter => adapter.IsDiscrete)
                    ?? snapshot.Adapters.FirstOrDefault();
            GpuAuditStatus = ActiveGpuAdapter == null
                ? "Видеоадаптер не обнаружен"
                : $"Аудит обновлён: {ActiveGpuAdapter.Name}";
        }
        catch
        {
            GpuAuditStatus = "Не удалось прочитать конфигурацию GPU";
        }
    }

    public Task RefreshAmdGpuTuningAsync() => RefreshAmdGpuTuningAsync(allowWhileBusy: false);

    private async Task RefreshAmdGpuTuningAsync(bool allowWhileBusy)
    {
        if (IsAmdGpuTuningBusy && !allowWhileBusy)
            return;

        IsAmdGpuTuningBusy = true;
        AmdGpuTuningStatus = "Проверяю AMD-адаптер и читаю параметры драйвера…";

        try
        {
            // When the AMD page is opened, the hardware audit and tuning read can
            // start together. Wait for that audit before deciding there is no AMD
            // adapter, and share any audit already in progress.
            if (SelectedCategory == "AMD GPU" && ActiveGpuAdapter?.Vendor != GpuVendor.Amd)
                await RefreshGpuAuditAsync();

            if (ActiveGpuAdapter?.Vendor != GpuVendor.Amd)
            {
                IsAmdGpuTuningAvailable = false;
                IsAmdPowerLimitAvailable = false;
                IsAmdVramFrequencyAvailable = false;
                AmdGpuTuningStatus = ActiveGpuAdapter?.Vendor == GpuVendor.Nvidia
                    ? "Для NVIDIA регуляторы появятся после подключения NVAPI."
                    : "Ручная настройка доступна только для обнаруженного AMD-адаптера.";
                AmdPowerLimitStatus = AmdGpuTuningStatus;
                AmdVramStatus = AmdGpuTuningStatus;
                return;
            }

            AmdGpuTuningStatus = "Чтение допустимых частот из драйвера AMD…";
            var adapterName = ActiveGpuAdapter.Name;
            var snapshot = await Task.Run(() => AmdAdlxTuningService.Read(adapterName));
            IsAmdGpuTuningAvailable = snapshot.Available;
            AmdGpuTuningStatus = snapshot.Status;
            IsAmdPowerLimitAvailable = snapshot.PowerLimitAvailable;
            AmdPowerLimitStatus = snapshot.PowerLimitStatus;
            IsAmdVramFrequencyAvailable = snapshot.VramFrequencyAvailable;
            AmdVramStatus = snapshot.VramStatus;
            if (!snapshot.Available) return;

            AmdGpuMinFrequencyLimit = snapshot.MinFrequencyLimit;
            AmdGpuMaxFrequencyLimit = snapshot.MaxFrequencyLimit;
            AmdGpuMinFrequencyStep = Math.Max(1, snapshot.MinFrequencyStep);
            AmdGpuMaxFrequencyStep = Math.Max(1, snapshot.MaxFrequencyStep);
            AmdGpuMinFrequency = snapshot.MinFrequency;
            AmdGpuMaxFrequency = snapshot.MaxFrequency;
            if (snapshot.PowerLimitAvailable)
            {
                AmdPowerLimitMin = snapshot.PowerLimitMin;
                AmdPowerLimitMax = snapshot.PowerLimitMax;
                AmdPowerLimitStep = Math.Max(1, snapshot.PowerLimitStep);
                AmdPowerLimit = snapshot.PowerLimit;
            }
            if (snapshot.VramFrequencyAvailable)
            {
                AmdVramFrequencyMin = snapshot.VramFrequencyMin;
                AmdVramFrequencyMax = snapshot.VramFrequencyMax;
                AmdVramFrequencyStep = Math.Max(1, snapshot.VramFrequencyStep);
                AmdVramFrequency = snapshot.VramFrequency;
            }
        }
        finally
        {
            IsAmdGpuTuningBusy = false;
        }
    }

    private async Task ApplyAmdGpuTuningAsync()
    {
        if (!IsAmdGpuTuningAvailable || ActiveGpuAdapter?.Vendor != GpuVendor.Amd) return;
        if (AmdGpuMinFrequency > AmdGpuMaxFrequency)
        {
            AmdGpuTuningStatus = "Минимальная частота не может быть выше максимальной.";
            return;
        }

        IsAmdGpuTuningBusy = true;
        AmdGpuTuningStatus = "Передаю частоты в драйвер AMD…";
        try
        {
            var result = await Task.Run(() => AmdAdlxTuningService.Apply(
                ActiveGpuAdapter.Name,
                (int)Math.Round(AmdGpuMinFrequency),
                (int)Math.Round(AmdGpuMaxFrequency)));
            AmdGpuTuningStatus = result.Message;
            if (result.Success)
                await RefreshAmdGpuTuningAsync(allowWhileBusy: true);
        }
        finally
        {
            IsAmdGpuTuningBusy = false;
        }
    }

    private async Task ResetAmdGpuTuningAsync()
    {
        if (ActiveGpuAdapter?.Vendor != GpuVendor.Amd) return;
        IsAmdGpuTuningBusy = true;
        AmdGpuTuningStatus = "Сбрасываю настройки через драйвер AMD…";
        try
        {
            var result = await Task.Run(() => AmdAdlxTuningService.ResetToFactory(ActiveGpuAdapter.Name));
            AmdGpuTuningStatus = result.Message;
            if (result.Success)
                await RefreshAmdGpuTuningAsync(allowWhileBusy: true);
        }
        finally
        {
            IsAmdGpuTuningBusy = false;
        }
    }

    private async Task ApplyAmdPowerLimitAsync()
    {
        if (!IsAmdPowerLimitAvailable || ActiveGpuAdapter?.Vendor != GpuVendor.Amd) return;
        IsAmdGpuTuningBusy = true;
        AmdGpuTuningStatus = "Передаю лимит мощности в драйвер AMD…";
        try
        {
            var result = await Task.Run(() => AmdAdlxTuningService.ApplyPowerLimit(
                ActiveGpuAdapter.Name, (int)Math.Round(AmdPowerLimit)));
            AmdPowerLimitStatus = result.Message;
            AmdGpuTuningStatus = result.Message;
            if (result.Success)
                await RefreshAmdGpuTuningAsync(allowWhileBusy: true);
        }
        finally
        {
            IsAmdGpuTuningBusy = false;
        }
    }

    private async Task ApplyAmdVramFrequencyAsync()
    {
        if (!IsAmdVramFrequencyAvailable || ActiveGpuAdapter?.Vendor != GpuVendor.Amd) return;
        IsAmdGpuTuningBusy = true;
        AmdGpuTuningStatus = "Передаю частоту VRAM в драйвер AMD…";
        try
        {
            var result = await Task.Run(() => AmdAdlxTuningService.ApplyVramFrequency(
                ActiveGpuAdapter.Name, (int)Math.Round(AmdVramFrequency)));
            AmdVramStatus = result.Message;
            AmdGpuTuningStatus = result.Message;
            if (result.Success)
                await RefreshAmdGpuTuningAsync(allowWhileBusy: true);
        }
        finally
        {
            IsAmdGpuTuningBusy = false;
        }
    }

    private void RefreshGpuGameProfiles()
    {
        GpuGameProfiles.Clear();
        foreach (var profile in GpuAuditService.GetHighPerformanceProfiles()) GpuGameProfiles.Add(profile);
    }

    private void ExecuteAddHighPerformanceGameProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите EXE игры для профиля высокой производительности",
            Filter = "Исполняемые файлы (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            GpuAuditService.SetHighPerformanceProfile(dialog.FileName);
            RefreshGpuGameProfiles();
            MessageBox.Show("Для этой игры выбран высокопроизводительный GPU Windows. Изменение вступит в силу при следующем запуске игры.", "GPU-профиль", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось сохранить GPU-профиль: {ex.Message}", "GPU-профиль", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteOpenGpuSystemSettings(object? parameter)
    {
        var target = parameter?.ToString() switch
        {
            "graphics" => "ms-settings:display-advancedgraphics",
            "display" => "ms-settings:display-advanced",
            "device-manager" => "devmgmt.msc",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть системную страницу GPU.\n\n{ex.Message}", "GPU", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExecuteImportBenchmarkCsv(object? parameter)
    {
        var isBaseline = string.Equals(parameter?.ToString(), "baseline", StringComparison.OrdinalIgnoreCase);
        var dialog = new OpenFileDialog
        {
            Title = isBaseline ? "Выберите замер ДО (PresentMon CSV)" : "Выберите замер ПОСЛЕ (PresentMon CSV)",
            Filter = "PresentMon CSV (*.csv)|*.csv|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var result = FrameTimeBenchmarkService.ImportPresentMonCsv(dialog.FileName);
            if (isBaseline) BenchmarkBaseline = result;
            else BenchmarkAfter = result;
            OnPropertyChanged(nameof(HasBenchmarkBaseline));
            OnPropertyChanged(nameof(HasBenchmarkAfter));
            OnPropertyChanged(nameof(HasBenchmarkComparison));
            OnPropertyChanged(nameof(BenchmarkAverageFpsDelta));
            OnPropertyChanged(nameof(BenchmarkTelemetryComparisonText));
            SaveBenchmarkResults();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось разобрать замер.\n\n{ex.Message}", "Бенчмарк", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExecuteCaptureBenchmarkAsync(object? parameter)
    {
        var isBaseline = string.Equals(parameter?.ToString(), "baseline", StringComparison.OrdinalIgnoreCase);
        var gameDialog = new OpenFileDialog
        {
            Title = "Выберите исполняемый файл игры для PresentMon",
            Filter = "Исполняемые файлы (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (gameDialog.ShowDialog() != true) return;

        var outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker", "Benchmarks");
        Directory.CreateDirectory(outputFolder);
        var suffix = isBaseline ? "before" : "after";
        var outputPath = Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(gameDialog.FileName)}-{suffix}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        IsBenchmarkCapturing = true;
        BenchmarkStatus = $"Захват {Path.GetFileName(gameDialog.FileName)} · {BenchmarkDurationSeconds} сек. Запустите игру и повторите одну и ту же сцену.";
        try
        {
            var result = await FrameTimeBenchmarkService.CaptureAsync(gameDialog.FileName, outputPath, BenchmarkDurationSeconds, ActiveGpuAdapter?.Name);
            if (isBaseline) BenchmarkBaseline = result;
            else BenchmarkAfter = result;
            OnPropertyChanged(nameof(HasBenchmarkBaseline));
            OnPropertyChanged(nameof(HasBenchmarkAfter));
            OnPropertyChanged(nameof(HasBenchmarkComparison));
            OnPropertyChanged(nameof(BenchmarkAverageFpsDelta));
            OnPropertyChanged(nameof(BenchmarkTelemetryComparisonText));
            SaveBenchmarkResults();
            BenchmarkStatus = $"Готово: {result.SampleCount:N0} кадров. CSV: {outputPath}";
        }
        catch (Exception ex)
        {
            BenchmarkStatus = $"Захват не выполнен: {ex.Message}";
        }
        finally
        {
            IsBenchmarkCapturing = false;
        }
    }

    private static string BenchmarkResultsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker", "Benchmarks", "comparison.json");

    private void LoadBenchmarkResults()
    {
        try
        {
            var path = BenchmarkResultsPath;
            if (!File.Exists(path)) return;
            var stored = JsonSerializer.Deserialize<StoredBenchmarkResults>(File.ReadAllText(path));
            if (stored == null) return;
            BenchmarkBaseline = stored.Baseline;
            BenchmarkAfter = stored.After;
            OnPropertyChanged(nameof(HasBenchmarkBaseline));
            OnPropertyChanged(nameof(HasBenchmarkAfter));
            OnPropertyChanged(nameof(HasBenchmarkComparison));
            OnPropertyChanged(nameof(BenchmarkAverageFpsDelta));
            OnPropertyChanged(nameof(BenchmarkTelemetryComparisonText));
        }
        catch { /* Повреждённый файл истории не должен мешать запуску приложения. */ }
    }

    private void SaveBenchmarkResults()
    {
        try
        {
            var path = BenchmarkResultsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new StoredBenchmarkResults(BenchmarkBaseline, BenchmarkAfter), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Сам захват важнее необязательного файла сравнения. */ }
    }

    private sealed record StoredBenchmarkResults(FrameTimeBenchmarkResult? Baseline, FrameTimeBenchmarkResult? After);

    private void ExecuteRemoveGpuGameProfile(object? parameter)
    {
        if (parameter is not GpuGameProfile profile) return;
        var confirmation = MessageBox.Show(
            $"Удалить профиль высокой производительности для:\n{profile.ExecutablePath}?\n\nWindows снова будет выбирать GPU автоматически.",
            "Удаление GPU-профиля", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            GpuAuditService.RemoveProfile(profile.ExecutablePath);
            RefreshGpuGameProfiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось удалить GPU-профиль: {ex.Message}", "GPU-профиль", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnAdapterSelectionChanged()
    {
        if (SelectedAdapter == null) return;

        IsNagleEnabled = NetworkOptimizerService.IsNagleOptimized(SelectedAdapter.Id);

        foreach (var s in DnsServers)
        {
            s.IsCurrent = SelectedAdapter.CurrentDns.Contains(s.PrimaryIp);
        }
    }

    private async Task BenchmarkDnsCommandAction()
    {
        IsDnsBenchmarking = true;
        try
        {
            await NetworkOptimizerService.MeasureDnsLatencyAsync(DnsServers);
        }
        finally
        {
            IsDnsBenchmarking = false;
        }
    }

    public async Task RefreshStartupItemsAsync()
    {
        IsStartupScanning = true;
        try
        {
            var items = await StartupService.GetStartupItemsAsync();
            StartupItems.Clear();
            foreach (var i in items) StartupItems.Add(i);
            OnPropertyChanged(nameof(StartupCount));
            OnPropertyChanged(nameof(HighImpactStartupCount));
        }
        finally
        {
            IsStartupScanning = false;
        }
    }

    public async Task RefreshCleanupScanAsync()
    {
        IsScanningCleanable = true;
        try
        {
            var tasks = CleanupCategories.Select(DiskCleanupService.ScanCategoryAsync);
            await Task.WhenAll(tasks);
            OnPropertyChanged(nameof(TotalCleanableFormatted));
        }
        finally
        {
            IsScanningCleanable = false;
        }
    }

    public async Task RefreshDeveloperDoctorAsync()
    {
        IsDevScanning = true;
        try
        {
            await Task.Delay(100);

            var list = await DeveloperDoctorService.ScanEnvironmentsAsync();
            DevComponents.Clear();
            foreach (var item in list) DevComponents.Add(item);

            var paths = DeveloperDoctorService.ScanPathEntries();
            PathEntries.Clear();
            foreach (var p in paths) PathEntries.Add(p);

            DevBrokenPathsCount = paths.Count(p => !p.ExistsOnDisk || p.IsDuplicate);

            if (!string.IsNullOrWhiteSpace(InspectCommandInput))
            {
                InspectResult = await DeveloperDoctorService.ResolveCommandOwnerAsync(InspectCommandInput.Trim());
            }
        }
        finally
        {
            IsDevScanning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public async Task RecalculateHealthScoreAsync()
    {
        try
        {
            var relevantTweaks = _allTweaks.Where(t => t.Category != "Интерфейс").ToList();
            var report = await SystemHealthService.AnalyzeSystemHealthAsync(relevantTweaks);

            SystemHealthScore = report.Score;
            SystemHealthStatusText = report.StatusText;
            ActiveTweaksCount = _allTweaks.Count(t => t.CurrentState);
            DisabledServicesCount = report.DisabledServicesCount;
            FreedSpaceFormatted = report.CleanableSpaceFormatted;

            SystemRecommendations.Clear();
            foreach (var r in report.Recommendations)
                SystemRecommendations.Add(r);

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                HealthPenaltyItems.Clear();
                var penalties = relevantTweaks.Where(t => !t.CurrentState).ToList();

                if (penalties.Count == 0)
                {
                    HealthPenaltyItems.Add(new HealthPenaltyItem(
                        "Все ключевые параметры оптимизированы",
                        "0% штрафа",
                        "Система",
                        () => { }
                    ));
                }
                else
                {
                    foreach (var tw in penalties)
                    {
                        HealthPenaltyItems.Add(new HealthPenaltyItem(
                            tw.Name,
                            tw.Risk == RiskLevel.Safe ? "-5% к оптимизации" : "-2% (Продвинутый)",
                            tw.Category,
                            () => { _ = ApplySingleTweakImmediateAsync(tw); }));
                    }
                }
            });
        }
        catch { }
    }

    private void RefreshAuditHistory()
    {
        try
        {
            AuditHistory.Clear();
            foreach (var rec in TransactionManager.GetHistory())
            {
                AuditHistory.Add(rec);
            }
        }
        catch { }
    }

    private void CheckDiskCachePrompt()
    {
        try
        {
            if (!DiskCacheService.IsCachePermissionAsked)
            {
                var res = MessageBox.Show(
                    "Включить интеллектуальное кэширование DiskTree?\n\n" +
                    "Кэш сохраняет снимок структуры папок в AppData, позволяя повторно открывать их мгновенно.\n\n" +
                    "Очистить кэш можно в любой момент во вкладке «Настройки».",
                    "Интеллектуальный кэш HeroTweaker",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                DiskCacheService.SavePermission(res == MessageBoxResult.Yes);
                UpdateCacheSizeDisplay();
            }
        }
        catch { }
    }

    private void UpdateCacheSizeDisplay()
    {
        try
        {
            long bytes = DiskCacheService.GetTotalCacheSize();
            DiskCacheFormatted = DiskItemModel.FormatBytes(bytes);
        }
        catch { DiskCacheFormatted = "0 КБ"; }
    }

    public async Task NavigateDiskToPathAsync(string path, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        CancellationTokenSource? scanCts = null;
        try
        {
            _diskScanCts?.Cancel();
            scanCts = new CancellationTokenSource();
            _diskScanCts = scanCts;
            var ct = scanCts.Token;

            IsDiskScanning = true;
            CurrentDiskPath = path;
            CurrentDiskFolderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(CurrentDiskFolderName)) CurrentDiskFolderName = path;

            RebuildBreadcrumbs(path);
            OnPropertyChanged(nameof(CanGoUpDisk));

            if (!forceRefresh)
            {
                var cached = await DiskCacheService.LoadSnapshotAsync(path);
                if (cached.HasValue)
                {
                    await Task.Delay(200, ct);

                    AllSunburstSlices.Clear();
                    foreach (var s in cached.Value.Slices) AllSunburstSlices.Add(s);

                    Level1ListItems.Clear();
                    foreach (var it in cached.Value.Level1) Level1ListItems.Add(it);

                    CurrentDiskTotalFormatted = DiskItemModel.FormatBytes(cached.Value.TotalSize);
                    IsDiskScanning = false;
                    return;
                }
            }

            var (allSlices, level1Items, totalSize) = await DiskAnalyzerService.AnalyzeSunburstAsync(path, ct);

            if (!ct.IsCancellationRequested)
            {
                AllSunburstSlices.Clear();
                foreach (var s in allSlices) AllSunburstSlices.Add(s);

                Level1ListItems.Clear();
                foreach (var it in level1Items) Level1ListItems.Add(it);

                CurrentDiskTotalFormatted = DiskItemModel.FormatBytes(totalSize);

                await DiskCacheService.SaveSnapshotAsync(path, allSlices, level1Items, totalSize);
                UpdateCacheSizeDisplay();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AddLog($"Ошибка диска: {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            if (scanCts != null && ReferenceEquals(_diskScanCts, scanCts))
            {
                IsDiskScanning = false;
                _diskScanCts = null;
            }
            scanCts?.Dispose();
            OnPropertyChanged(nameof(CanGoUpDisk));
        }
    }

    private static bool IsSafeDiskDeletionTarget(DiskItemModel? item, string? path)
    {
        if (item?.IsSynthetic == true || string.IsNullOrWhiteSpace(path)) return false;

        string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = (Path.GetPathRoot(path) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public void RefreshAvailableDrives()
    {
        UpdateAvailableDrives(CollectAvailableDrives());
    }

    private async Task LoadAvailableDrivesAsync()
    {
        IsLoadingAvailableDrives = true;
        try
        {
            var drives = await Task.Run(CollectAvailableDrives);
            UpdateAvailableDrives(drives);
        }
        catch (Exception ex)
        {
            AddLog($"Не удалось загрузить список накопителей: {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            IsLoadingAvailableDrives = false;
        }
    }

    private static List<DriveSelectModel> CollectAvailableDrives()
    {
        var result = new List<DriveSelectModel>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0) continue;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Локальный диск" : drive.VolumeLabel;
                string root = drive.RootDirectory.FullName;
                string total = DiskItemModel.FormatBytes(drive.TotalSize);
                string free = DiskItemModel.FormatBytes(drive.AvailableFreeSpace);

                double usedBytes = (double)(drive.TotalSize - drive.AvailableFreeSpace);
                double usedPercentage = Math.Round((usedBytes / drive.TotalSize) * 100.0, 1);

                string display = $"{drive.Name.TrimEnd('\\')} ({label})";
                result.Add(new DriveSelectModel(root, label, display, total, free, usedPercentage));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return result;
    }

    private void UpdateAvailableDrives(IEnumerable<DriveSelectModel> drives)
    {
        AvailableDrives.Clear();
        foreach (var drive in drives)
            AvailableDrives.Add(drive);
        OnPropertyChanged(nameof(DriveInventoryStatus));
    }

    private void RebuildBreadcrumbs(string path)
    {
        try
        {
            Breadcrumbs.Clear();
            var di = new DirectoryInfo(path);
            var chain = new List<DirectoryInfo>();
            var curr = di;
            while (curr != null) { chain.Insert(0, curr); curr = curr.Parent; }

            for (int i = 0; i < chain.Count; i++)
            {
                string title = chain[i].Name.TrimEnd(Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(title)) title = chain[i].FullName;
                Breadcrumbs.Add(new BreadcrumbItem(title, chain[i].FullName, i == chain.Count - 1));
            }
        }
        catch { }
    }

    private async Task ExecuteChooseDiskFolderAsync()
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "Выберите раздел или папку" };
            if (dialog.ShowDialog() == true)
                await NavigateDiskToPathAsync(dialog.FolderName);
        }
        catch { }
    }

    public async Task RefreshInstalledProgramsAsync()
    {
        try
        {
            var list = await AppManagerService.GetInstalledProgramsAsync();
            InstalledPrograms.Clear();
            foreach (var p in list)
            {
                p.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(InstalledProgramModel.IsSelected))
                        OnPropertyChanged(nameof(SelectedUninstallCount));
                };
                InstalledPrograms.Add(p);
            }
            FilteredInstalledPrograms.Refresh();
            OnPropertyChanged(nameof(SelectedUninstallCount));
        }
        catch { }
    }

    private async Task ExecuteBatchUninstallAsync()
    {
        var selected = InstalledPrograms.Where(program => program.IsSelected).ToList();
        if (selected.Count == 0) return;

        var confirmation = MessageBox.Show(
            $"Удалить выбранные программы: {selected.Count}?\n\n" +
            "Для записей с QuietUninstallString будет использован тихий деинсталлятор. " +
            "Для остальных Windows может показать штатное окно удаления.",
            "Очередь деинсталляции", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        IsGlobalBusy = true;
        try
        {
            foreach (var program in selected)
            {
                var command = program.SupportsQuietUninstall ? program.QuietUninstallString : program.UninstallString;
                program.RemovalStatus = program.SupportsQuietUninstall ? "Удаление без интерфейса…" : "Ожидание штатного деинсталлятора…";
                StatusMessage = $"Удаление: {program.DisplayName}";
                try
                {
                    var exitCode = await AppManagerService.RunUninstallAndWaitAsync(command);
                    program.RemovalStatus = exitCode == 0 ? "Завершено" : $"Код завершения: {exitCode}";
                }
                catch (Exception ex)
                {
                    program.RemovalStatus = $"Ошибка: {ex.Message}";
                }
            }

            await RefreshInstalledProgramsAsync();
            UpdateAppLeftovers(selected);
        }
        finally
        {
            IsGlobalBusy = false;
            StatusMessage = string.Empty;
        }
    }

    private void ExecuteScanAppLeftovers()
    {
        var selected = InstalledPrograms.Where(program => program.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Выберите программы, для которых нужно проверить оставшиеся каталоги.", "Поиск остатков", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        UpdateAppLeftovers(selected);
    }

    private void UpdateAppLeftovers(IEnumerable<InstalledProgramModel> programs)
    {
        AppLeftovers.Clear();
        foreach (var item in AppManagerService.FindPotentialLeftovers(programs))
        {
            item.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(AppLeftoverModel.IsSelected))
                    OnPropertyChanged(nameof(SelectedLeftoverCount));
            };
            AppLeftovers.Add(item);
        }
        OnPropertyChanged(nameof(HasAppLeftovers));
        OnPropertyChanged(nameof(SelectedLeftoverCount));
    }

    private async Task ExecuteRemoveSelectedLeftoversAsync()
    {
        var selected = AppLeftovers.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0) return;
        var confirmation = MessageBox.Show(
            $"Переместить в Корзину выбранные остатки: {selected.Count}?\n\nБудут затронуты только отмеченные каталоги из списка ниже.",
            "Очистка остатков", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        IsGlobalBusy = true;
        try
        {
            await Task.Run(() =>
            {
                foreach (var item in selected)
                {
                    try
                    {
                        if (Directory.Exists(item.Path))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                                item.Path,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                    }
                    catch { }
                }
            });
            foreach (var item in selected) AppLeftovers.Remove(item);
            OnPropertyChanged(nameof(HasAppLeftovers));
            OnPropertyChanged(nameof(SelectedLeftoverCount));
        }
        finally
        {
            IsGlobalBusy = false;
        }
    }

    private void StartRealTimeTelemetry()
    {
        GetSystemTimes(out var iTime, out var kTime, out var uTime);
        _prevIdleTime = FileTimeToUInt64(iTime);
        _prevKernelTime = FileTimeToUInt64(kTime);
        _prevUserTime = FileTimeToUInt64(uTime);

        for (int i = 0; i < 35; i++)
        {
            _cpuHistory.Add(15);
            _ramHistory.Add(40);
        }

        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _telemetryTimer.Tick += (_, _) =>
        {
            try
            {
                var mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem))
                {
                    double usedBytes = (double)(mem.ullTotalPhys - mem.ullAvailPhys);
                    double totalBytes = (double)mem.ullTotalPhys;
                    double usedGb = usedBytes / (1024.0 * 1024.0 * 1024.0);
                    double totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);

                    RealTimeRamUsage = Math.Round((usedBytes / totalBytes) * 100.0, 0);
                    RamDetailsFormatted = $"{usedGb:F1} ГБ / {totalGb:F1} ГБ";
                }

                if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                {
                    ulong idle = FileTimeToUInt64(idleFt);
                    ulong kernel = FileTimeToUInt64(kernelFt);
                    ulong user = FileTimeToUInt64(userFt);

                    ulong usrDiff = user - _prevUserTime;
                    ulong kerDiff = kernel - _prevKernelTime;
                    ulong idlDiff = idle - _prevIdleTime;

                    ulong sysTotal = usrDiff + kerDiff;
                    if (sysTotal > 0)
                    {
                        double cpu = (double)(sysTotal - idlDiff) / sysTotal * 100.0;
                        RealTimeCpuUsage = Math.Clamp(Math.Round(cpu, 0), 1.0, 100.0);
                    }

                    _prevIdleTime = idle;
                    _prevKernelTime = kernel;
                    _prevUserTime = user;
                }

                _cpuHistory.Add(RealTimeCpuUsage);
                if (_cpuHistory.Count > 35) _cpuHistory.RemoveAt(0);

                _ramHistory.Add(RealTimeRamUsage);
                if (_ramHistory.Count > 35) _ramHistory.RemoveAt(0);

                CpuChartGeometry = BuildAreaGeometry(_cpuHistory, 300, 36);
                RamChartGeometry = BuildAreaGeometry(_ramHistory, 300, 36);
            }
            catch { }
        };
        _telemetryTimer.Start();
    }

    private static PathGeometry BuildAreaGeometry(List<double> values, double width, double height)
    {
        var geom = new PathGeometry();
        if (values.Count < 2) return geom;

        var figure = new PathFigure
        {
            StartPoint = new Point(0, height),
            IsClosed = true,
            IsFilled = true
        };

        double stepX = width / (values.Count - 1);

        for (int i = 0; i < values.Count; i++)
        {
            double val = Math.Clamp(values[i], 0, 100);
            double x = i * stepX;
            double y = height - (val / 100.0 * (height - 4));
            if (i == 0) figure.Segments.Add(new LineSegment(new Point(0, y), true));
            else figure.Segments.Add(new LineSegment(new Point(x, y), true));
        }

        figure.Segments.Add(new LineSegment(new Point(width, height), true));
        figure.Segments.Add(new LineSegment(new Point(0, height), true));

        geom.Figures.Add(figure);
        geom.Freeze();
        return geom;
    }

    public void AddLog(string message, LogLevel level = LogLevel.Info)
    {
        try
        {
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background, () =>
            {
                ConsoleLogs.Add(new LogEntry(DateTime.Now, message, level));
            });
        }
        catch { }
    }

    private async Task ExecuteSearchDriverUpdatesAsync()
    {
        try
        {
            IsConsoleRunning = true;
            HasDriverUpdates = false;
            _foundDriverUpdates.Clear();
            DriverProgress = 20;

            AddLog("--- ПОИСК ОБНОВЛЕНИЙ ДРАЙВЕРОВ (WINDOWS UPDATE) ---", LogLevel.Command);
            var results = await DriverService.SearchDriverUpdatesAsync(AddLog);
            DriverProgress = 100;

            if (results.Count > 0)
            {
                _foundDriverUpdates.AddRange(results);
                HasDriverUpdates = true;
                FoundDriversStatusText = $"Обнаружено доступных обновлений драйверов: {results.Count}";
            }
            else
            {
                HasDriverUpdates = false;
                FoundDriversStatusText = "Все установленные драйверы актуальны";
            }
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка: {ex.Message}", LogLevel.Error);
            HasDriverUpdates = false;
        }
        finally
        {
            IsConsoleRunning = false;
        }
    }

    private async Task ExecuteInstallDriverUpdatesAsync()
    {
        if (_foundDriverUpdates.Count == 0) return;

        var confirm = MessageBox.Show(
            $"Запустить установку {_foundDriverUpdates.Count} найденных драйверов через Windows Update?\n\nВо время обновления драйвера дисплея экран может кратковременно мигнуть.",
            "Обновление драйверов",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            IsConsoleRunning = true;
            DriverProgress = 15;
            AddLog("--- НАЧАЛО УСТАНОВКИ ОБНОВЛЕНИЙ ДРАЙВЕРОВ ---", LogLevel.Command);

            bool success = await DriverService.InstallDriverUpdatesAsync(
                _foundDriverUpdates,
                AddLog,
                p => DriverProgress = p);

            if (success)
            {
                HasDriverUpdates = false;
                _foundDriverUpdates.Clear();
                MessageBox.Show("Все драйверы успешно обновлены!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка установки: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            IsConsoleRunning = false;
        }
    }

    private async Task ExecuteBackupDriversAsync()
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "Папка для бэкапа драйверов" };
            if (dialog.ShowDialog() == true)
            {
                IsConsoleRunning = true;
                await DriverService.BackupDriversAsync(dialog.FolderName, AddLog);
            }
        }
        catch (Exception ex) { AddLog($"Ошибка: {ex.Message}", LogLevel.Error); }
        finally { IsConsoleRunning = false; }
    }

    private async Task ExecuteRestoreDriversAsync()
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "Выберите папку с бэкапом" };
            if (dialog.ShowDialog() == true)
            {
                IsConsoleRunning = true;
                await DriverService.RestoreDriversAsync(dialog.FolderName, AddLog);
            }
        }
        catch (Exception ex) { AddLog($"Ошибка: {ex.Message}", LogLevel.Error); }
        finally { IsConsoleRunning = false; }
    }

    private async Task ExecuteInstallVCRuntimesAsync() =>
        await DriverService.InstallVisualCppRuntimesAsync(AddLog, p => DriverProgress = p);

    private async Task InitializeAppAsync()
    {
        try
        {
            IsAppLoading = true;
            SplashProgress = 10;
            SplashStatus = "Открытие интерфейса…";
            await Task.Yield();
            // Load volumes independently: DriveInfo may wait on disconnected volumes,
            // and must not delay the first dashboard render.
            _ = LoadAvailableDrivesAsync();
            var tweakCount = Math.Max(1, _allTweaks.Count);
            IProgress<int> tweakProgress = new Progress<int>(completed =>
                SplashProgress = 10 + (int)Math.Round(45d * completed / tweakCount));
            await Task.Run(() =>
            {
                for (var index = 0; index < _allTweaks.Count; index++)
                {
                    _allTweaks[index].RefreshState();
                    if (index % 4 == 3 || index == _allTweaks.Count - 1)
                        tweakProgress.Report(index + 1);
                }
            });

            // Network, registry and power-plan queries may block on driver/WMI state.
            // Keep them off the WPF dispatcher so the splash and window animation stay responsive.
            var networkTask = Task.Run(NetworkOptimizerService.GetAllPhysicalAdapters);
            var vendorTask = Task.Run(GpuAuditService.DetectSupportedVendors);
            var powerPlanTask = Task.Run(GetActivePowerPlanName);
            await Task.WhenAll(networkTask, vendorTask, powerPlanTask);
            var adapters = networkTask.Result;
            AvailableAdapters.Clear();
            foreach (var adapter in adapters) AvailableAdapters.Add(adapter);
            if (SelectedAdapter == null || !AvailableAdapters.Any(adapter => adapter.Name == SelectedAdapter.Name))
                SelectedAdapter = AvailableAdapters.FirstOrDefault(adapter => adapter.IsDefaultRoute)
                    ?? AvailableAdapters.FirstOrDefault(adapter => adapter.IsUp)
                    ?? AvailableAdapters.FirstOrDefault();
            else OnAdapterSelectionChanged();
            HasAmdGpu = vendorTask.Result.HasAmd;
            HasNvidiaGpu = vendorTask.Result.HasNvidia;
            ActivePowerPlanName = powerPlanTask.Result;

            SplashProgress = 75;
            SplashStatus = "Загрузка сведений о видеокарте…";
            await RefreshGpuAuditAsync();

            SplashProgress = 90;
            SplashStatus = "Чтение сведений о системе…";
            try { SysInfo = await SystemInfoService.GetSystemInfoAsync(); } catch { }

            SplashProgress = 100;
            SplashStatus = "Готово";
        }
        catch (Exception ex)
        {
            SplashStatus = "Часть сведений недоступна; интерфейс продолжит работу.";
            AddLog($"Ошибка начальной загрузки: {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            IsAppLoading = false;

            _ = Task.Run(async () =>
            {
                await RecalculateHealthScoreAsync();
                await CheckUpdatesInBackgroundAsync();
            });
        }
    }

    private static string GetActivePowerPlanName()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = "/getactivescheme",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                var line = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1000);
                var openIndex = line.IndexOf('(');
                var closeIndex = line.IndexOf(')');
                if (openIndex >= 0 && closeIndex > openIndex)
                    return line.Substring(openIndex + 1, closeIndex - openIndex - 1);
            }
        }
        catch { }
        return "Сбалансированная";
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        try
        {
            var result = await UpdateCheckerService.CheckForUpdatesAsync();
            Application.Current?.Dispatcher?.Invoke(() => ApplyUpdateCheckResult(result));
        }
        catch { }
    }

    private async Task ExecuteCheckForUpdatesAsync(bool showResult)
    {
        if (IsUpdateChecking || IsUpdateInstalling) return;
        IsUpdateChecking = true;
        UpdateStatus = "Проверяю стабильный релиз GitHub...";
        try
        {
            var result = await UpdateCheckerService.CheckForUpdatesAsync();
            ApplyUpdateCheckResult(result);
            if (showResult)
                MessageBox.Show(UpdateStatus, "HeroTweaker — обновления", MessageBoxButton.OK,
                    result.HasUpdate && result.CanInstall ? MessageBoxImage.Information : MessageBoxImage.None);
        }
        finally
        {
            IsUpdateChecking = false;
            OnPropertyChanged(nameof(CanInstallUpdate));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyUpdateCheckResult(UpdateCheckResult result)
    {
        _availableUpdate = result;
        HasUpdateAvailable = result.HasUpdate;
        LatestVersionTag = result.LatestTag;
        ReleaseUrl = result.ReleaseUrl;
        UpdateStatus = result.Status;
        OnPropertyChanged(nameof(CanInstallUpdate));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ExecuteInstallUpdateAsync()
    {
        var update = _availableUpdate;
        if (update == null || !update.CanInstall || IsUpdateInstalling) return;
        IsUpdateInstalling = true;
        UpdateProgress = 0;
        UpdateStatus = $"Загрузка {update.LatestTag}...";
        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgress = value;
                UpdateStatus = value < 70
                    ? $"Скачивание обновления {value:F0}%..."
                    : value < 100 ? "Проверка архива и подготовка файлов..." : "Обновление подготовлено. Перезапускаю HeroTweaker...";
            });
            await SelfUpdateService.DownloadVerifyAndLaunchAsync(update, progress);
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Обновление не установлено: {ex.Message}";
            MessageBox.Show(UpdateStatus, "HeroTweaker — обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsUpdateInstalling = false;
            OnPropertyChanged(nameof(CanInstallUpdate));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ExecuteApplyPendingAsync()
    {
        var pendingList = _allTweaks.Where(t => t.HasPendingChange).ToList();
        if (pendingList.Count == 0) return;

        IsGlobalBusy = true;
        ProgressValue = 0;
        int total = pendingList.Count;
        int done = 0;

        try
        {
            foreach (var tweak in pendingList)
            {
                StatusMessage = $"Применение: {tweak.Name}...";
                var record = await tweak.CommitChangeWithTransactionAsync();
                if (record != null)
                {
                    await TransactionManager.CommitTransactionAsync(record);
                }

                done++;
                ProgressValue = (double)done / total * 100;
                await Task.Delay(35);
            }

            foreach (var t in _allTweaks) t.RefreshState();
            RefreshAuditHistory();
            await RecalculateHealthScoreAsync();

            StatusMessage = "Все транзакции зафиксированы в журнале аудита!";
            await Task.Delay(250);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка транзакции", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            IsGlobalBusy = false;
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(HasPendingChanges));
        }
    }

    private void ExecuteDiscardPending()
    {
        foreach (var t in _allTweaks) t.ResetPending();
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private async Task ExecuteCreateRestorePointAsync()
    {
        IsGlobalBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Создание точки восстановления...";
        try
        {
            var (ok, msg) = await SystemUtility.CreateRestorePointAsync(m => StatusMessage = m);
            MessageBox.Show(msg, ok ? "Успех" : "Внимание", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsGlobalBusy = false;
            StatusMessage = string.Empty;
            IsProgressIndeterminate = false;
        }
    }

    private async Task ExecuteCleanTempAsync()
    {
        IsGlobalBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Очистка Temp...";
        try
        {
            long freed = await SystemUtility.CleanTempFilesAsync();
            MessageBox.Show($"Освобождено: {freed / (1024.0 * 1024.0):F2} МБ", "Temp", MessageBoxButton.OK, MessageBoxImage.Information);
            SysInfo = await SystemInfoService.GetSystemInfoAsync();
            await RecalculateHealthScoreAsync();
        }
        finally
        {
            IsGlobalBusy = false;
            StatusMessage = string.Empty;
            IsProgressIndeterminate = false;
        }
    }

    // =====================================================================================
    // ДВИЖОК РЕГИСТРАЦИИ ТВIКОВ
    // =====================================================================================

    private void RegTweak(
        string id,
        string name,
        string desc,
        string cat,
        RegistryHive hive,
        string path,
        string valName,
        object targetVal,
        object defVal,
        bool delKey = false,
        bool isFeatured = false,
        RiskLevel risk = RiskLevel.Safe,
        string userWhy = "",
        string techWhy = "",
        string rebootReq = "Мгновенно")
    {
        if (_allTweaks.Any(t => t.Id == id)) return;

        var tweak = new RegistryTweak(id, name, desc, cat, hive, path, valName, targetVal, defVal, delKey);

        Func<Task> revertAction = () => Task.Run(() =>
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                if (delKey)
                {
                    if (string.IsNullOrEmpty(valName)) baseKey.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    else { using var subKey = baseKey.OpenSubKey(path, writable: true); subKey?.DeleteValue(valName, throwOnMissingValue: false); }
                }
                else
                {
                    using var subKey = baseKey.CreateSubKey(path, writable: true);
                    if (subKey != null && !string.IsNullOrEmpty(valName))
                    {
                        if (defVal is int i) subKey.SetValue(valName, i, RegistryValueKind.DWord);
                        else if (defVal is string s) subKey.SetValue(valName, s, RegistryValueKind.String);
                        else subKey.SetValue(valName, defVal);
                    }
                }
            }
            catch { }

            try
            {
                string h = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
                if (delKey)
                {
                    if (string.IsNullOrEmpty(valName)) Process.Start(new ProcessStartInfo("reg.exe", $"delete \"{h}\\{path}\" /f") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                    else Process.Start(new ProcessStartInfo("reg.exe", $"delete \"{h}\\{path}\" /v \"{valName}\" /f") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                }
                else
                {
                    string t = defVal is int ? "REG_DWORD" : "REG_SZ";
                    Process.Start(new ProcessStartInfo("reg.exe", $"add \"{h}\\{path}\" /v \"{valName}\" /t {t} /d \"{defVal}\" /f") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                }
            }
            catch { }
        });

        Func<TransactionRecord?> snapshotFunc = () =>
        {
            var r = new TransactionRecord { TweakId = id, TweakName = name, Category = cat };
            try
            {
                var s = TransactionManager.CreateRegistrySnapshot(hive, path, valName);
                if (s != null) r.Snapshots.Add(s);
            }
            catch { }
            return r;
        };

        string hiveStr = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
        string targetValStr = delKey ? "(Удаление ключа/значения)" : $"{valName} = {targetVal} (DWORD/SZ)";
        string techFull = $"{hiveStr}\\{path}\nПараметр: {targetValStr}\n{techWhy}".Trim();

        var vm = new TweakViewModel(tweak, revertAction, snapshotFunc, isFeatured, risk)
        {
            UserWhy = string.IsNullOrWhiteSpace(userWhy) ? desc : userWhy,
            TechDetails = techFull,
            RebootText = rebootReq,
            OnPendingStateChanged = () =>
            {
                OnPropertyChanged(nameof(PendingCount));
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        };

        _allTweaks.Add(vm);
        if (isFeatured) DashboardTweaks.Add(vm);
    }

    private void SrvTweak(
        string id,
        string name,
        string desc,
        string srvName,
        int defMode,
        bool isFeatured = false,
        RiskLevel risk = RiskLevel.Safe,
        string userWhy = "",
        string techWhy = "",
        string rebootReq = "После перезапуска службы или перезагрузки")
    {
        if (_allTweaks.Any(t => t.Id == id)) return;

        string path = $@"SYSTEM\CurrentControlSet\Services\{srvName}";
        var tweak = new RegistryTweak(id, name, desc, "Службы", RegistryHive.LocalMachine, path, "Start", 4, defMode);

        Func<Task> revertAction = () => Task.Run(() =>
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var subKey = baseKey.CreateSubKey(path, writable: true);
                subKey?.SetValue("Start", defMode, RegistryValueKind.DWord);
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo("reg.exe", $"add \"HKLM\\{path}\" /v \"Start\" /t REG_DWORD /d \"{defMode}\" /f") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();

                string mStr = defMode switch { 2 => "auto", 3 => "demand", 4 => "disabled", _ => "auto" };
                Process.Start(new ProcessStartInfo { FileName = "sc.exe", Arguments = $"config \"{srvName}\" start= {mStr}", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
                if (defMode != 4) Process.Start(new ProcessStartInfo { FileName = "sc.exe", Arguments = $"start \"{srvName}\"", CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        });

        Func<TransactionRecord?> snapshotFunc = () =>
        {
            var r = new TransactionRecord { TweakId = id, TweakName = name, Category = "Службы" };
            try
            {
                var s = TransactionManager.CreateServiceSnapshot(srvName);
                if (s != null) r.Snapshots.Add(s);
            }
            catch { }
            return r;
        };

        string defModeStr = defMode switch { 2 => "Auto (2)", 3 => "Manual/Demand (3)", 4 => "Disabled (4)", _ => defMode.ToString() };
        string techFull = $"Служба Win32: {srvName}\nРежим запуска: Disabled (Start=4, по умолч: {defModeStr})\nПуть: HKLM\\SYSTEM\\CurrentControlSet\\Services\\{srvName}\n{techWhy}".Trim();

        var vm = new TweakViewModel(tweak, revertAction, snapshotFunc, isFeatured, risk)
        {
            UserWhy = string.IsNullOrWhiteSpace(userWhy) ? desc : userWhy,
            TechDetails = techFull,
            RebootText = rebootReq,
            OnPendingStateChanged = () =>
            {
                OnPropertyChanged(nameof(PendingCount));
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        };

        _allTweaks.Add(vm);
        if (isFeatured) DashboardTweaks.Add(vm);
    }

    private void RegisterTweaks()
    {
        _allTweaks.Clear();
        DashboardTweaks.Clear();

        // 1. ПРИВАТНОСТЬ
        RegTweak("telemetry_disable", "Отключить телеметрию Windows", "Ограничивает сбор данных диагностических служб Microsoft.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1,
            isFeatured: true,
            userWhy: "Windows перестает непрерывно собирать и отправлять отчеты об использовании ПК, кликах и ошибках. Разгружает диск и фоновый трафик.",
            techWhy: "Выставляет AllowTelemetry = 0 (уровень Security). Блокирует провайдер событий ETW 'Microsoft-Windows-Diagnostics-Logging' и модуль Asimov.");

        RegTweak("advertising_id_disable", "Запретить рекламный ID", "Блокирует показ персонализированной рекламы в приложениях.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1,
            userWhy: "Приложения из Microsoft Store и игры больше не смогут отслеживать ваши интересы для показа целевой рекламы.",
            techWhy: "Отключает генерацию уникального GUID пользователя AdvertisingID в подсистеме Windows.System.UserProfile.AdvertisingManager.");

        RegTweak("cortana_disable", "Отключить Cortana", "Блокирует фоновый голосовой ассистент.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, 1, delKey: true,
            userWhy: "Полностью выгружает голосовой ассистент Cortana. Освобождает оперативную память и устраняет микрофонную активность в фоне.",
            techWhy: "Политика Windows Search: AllowCortana = 0. Запрещает запуск процессов SearchUI.exe / CortanaCore в фоновых сессиях DCOM.");

        RegTweak("activity_history", "Отключить журнал активности", "Запрещает Windows сохранять историю запущенных задач.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, 1,
            userWhy: "Очищает ленту 'Timeline' и не сохраняет историю открытых вами файлов, вкладок браузера и программ.",
            techWhy: "PublishUserActivities = 0 и UploadUserActivities = 0. Прекращает синхронизацию Connected Devices Platform Service (CDPSvc) с облаком MSGraph.");

        RegTweak("typing_telemetry", "Отключить сбор данных клавиатуры", "Блокирует отправку шаблонов рукописного и экранного ввода.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0,
            userWhy: "Защищает вводимые пароли и переписку от отправки словарей и паттернов набора текста на анализ в Microsoft.",
            techWhy: "RestrictImplicitInkCollection = 1, HarvestContacts = 0. Блокирует сбор биометрических характеристик нажатий в компоненте TextInputHost.");

        RegTweak("app_diagnostics", "Запретить приложениям доступ к диагностике", "Ограничивает фоновый доступ сторонних программ к журналу диагностики.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsGetDiagnosticInfo", 2, 0,
            userWhy: "Сторонние утилиты не смогут сканировать журнал сбоев и системные ошибки других открытых программ.",
            techWhy: "AppPrivacy: LetAppsGetDiagnosticInfo = 2 (Deny). Ограничивает права capability 'appDiagnostics' в манифестах AppX/UWP.");

        RegTweak("location_tracking", "Отключить службы геолокации", "Блокирует встроенные системные датчики местоположения.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, 0,
            userWhy: "Сайты и фоновые программы не смогут непрерывно определять ваш физический город и координаты по Wi-Fi сетям.",
            techWhy: "DisableLocation = 1. Останавливает системный провайдер Sensor and Location Platform и геолокационные вызовы Windows.Devices.Geolocation.");

        RegTweak("bing_start_search", "Отключить поиск Bing в Пуске", "Убирает веб-страницы и рекламу из поисковой строки меню Пуск.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, 0, delKey: true,
            isFeatured: true,
            userWhy: "Поиск в Пуске находит нужные программы мгновенно, не зависая на подгрузку сайтов и новостей из интернета.",
            techWhy: "DisableSearchBoxSuggestions = 1. Блокирует HTTP-запросы Cortana/SearchApp к api.bing.com при вводе символов в Editbox Проводника.");

        RegTweak("web_search_disable", "Отключить веб-поиск Windows Search", "Поиск Windows ищет файлы только на локальном накопителе.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, 1,
            userWhy: "Исключает отправку ваших поисковых запросов в интернет-поисковик Bing.",
            techWhy: "BingSearchEnabled = 0, CortanaConsent = 0 в Search ветке реестра текущего пользователя.");

        RegTweak("lockscreen_spotlight", "Отключить рекламу на экране блокировки", "Убирает встроенные рекламные советы и ссылки на экране входа.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenEnabled", 0, 1,
            userWhy: "Экран блокировки перестает загружать из интернета случайные рекламные фотографии и промо-ссылки Microsoft.",
            techWhy: "ContentDeliveryManager: RotatingLockScreenEnabled = 0, SubscribedContent-338387Enabled = 0.");

        RegTweak("windows_consumer_features", "Запретить установку промо-приложений", "Блокирует скрытую установку игр (Candy Crush, TikTok) после апдейтов.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, 0, delKey: true,
            userWhy: "Windows больше никогда не установит рекламные мобильные игры и мусорный софт без вашего спроса.",
            techWhy: "CloudContent: DisableWindowsConsumerFeatures = 1. Отключает триггер SilentInstalledApps в OOBE и Component Store.");

        RegTweak("tailored_experiences", "Отключить персонализацию диагностики", "Запрещает Microsoft предлагать рекламу на основе системных логов.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1, 0, delKey: true,
            userWhy: "Microsoft не сможет анализировать сбои ваших программ для подбора целевых подсказок в ОС.",
            techWhy: "DisableTailoredExperiencesWithDiagnosticData = 1. Отключает фоновый сопоставитель профилей в CloudContentManager.");

        RegTweak("feedback_notifications", "Отключить всплывающие опросы отчетов", "Windows больше не будет запрашивать обратную связь и оценки.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, 1, delKey: true,
            userWhy: "Навсегда убирает всплывающие окна вида 'Насколько вероятно, что вы порекомендуете Windows 11 другу?'.",
            techWhy: "NumberOfSIUFInPeriod = 0. Полностью отключает периодический опрос подсистемы SIUF (System In-line User Feedback).");

        RegTweak("edge_prelaunch", "Запретить фоновый предзапуск Edge", "Предотвращает предварительную загрузку Edge в память при старте ОС.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\MicrosoftEdge\Main", "AllowPrelaunch", 0, 1, delKey: true,
            userWhy: "Браузер Microsoft Edge не будет висеть в памяти и тратить 200–300 МБ ОЗУ, пока вы его сами не откроете.",
            techWhy: "AllowPrelaunch = 0, AllowTabPreloading = 0. Запрещает системному планировщику запускать скрытые инстансы msedge.exe.");

        RegTweak("ceip_telemetry", "Отключить программу улучшения ПО (CEIP)", "Блокирует службу сбора отзывов о работе программ Windows.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0, 1, delKey: true,
            userWhy: "Устраняет фоновый опрос установленного ПО и экономит процессорное время.",
            techWhy: "SQMClient: CEIPEnable = 0, StudyId = 0. Блокирует генерацию SQM-пакетов библиотекой sqmapi.dll.");

        RegTweak("clipboard_cloud_sync", "Запретить отправку буфера обмена в облако", "Отключает передачу скопированных паролей и текста на сервера.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Clipboard", "AllowCrossDeviceClipboard", 0, 1,
            userWhy: "Гарантирует, что скопированные пароли, номера карт и текст никогда не покинут ваш компьютер через облачную синхронизацию.",
            techWhy: "AllowCrossDeviceClipboard = 0. Отключает отправку сериализованных пейлоадов буфера в службу OneSettings/SkyDrive.");

        RegTweak("uac_disable", "Отключить UAC (Контроль учетных записей)", "Полностью отключает затемнение экрана и предупреждения при запуске программ. Снижает базовую безопасность ОС.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 0, 1,
            risk: RiskLevel.Advanced,
            userWhy: "Убирает затемнение экрана и вопрос 'Разрешить этому приложению вносить изменения?'. Ускоряет запуск игр и софта, но требует внимательности.",
            techWhy: "EnableLUA = 0. Отключает фильтрованные токены администратора (Filtered Admin Token). Софт сразу получает полные права сессии.",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("smartscreen_disable", "Отключить фильтр SmartScreen", "Отключает проверку запускаемых файлов в интернете. Ускоряет запуск софта, но убирает предупреждения.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0, 1,
            risk: RiskLevel.Advanced,
            userWhy: "Убирает синее предупреждающее окно при первом запуске скачанных exe-файлов и устраняет задержку запуска программ без цифровой подписи.",
            techWhy: "EnableSmartScreen = 0. Отключает отправку хешей исполняемых файлов (SHA-256) в сервис SmartScreen Reputation Service.");

        // 2. ПРОИЗВОДИТЕЛЬНОСТЬ И ИГРЫ
        RegTweak("anim_disable", "Отключить анимации окон", "Мгновенный отклик интерфейса без задержек при сворачивании окон.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1",
            isFeatured: true,
            userWhy: "Окна распахиваются и сворачиваются со скоростью клика, без медленного эффекта скольжения. Визуально система ощущается гораздо быстрее.",
            techWhy: "Control Panel\\Desktop\\WindowMetrics: MinAnimate = '0'. Отключает интерполяцию кадров анимации в Desktop Window Manager (DWM).");

        RegTweak("system_responsiveness", "Максимальная отзывчивость системы", "Снимает скрытый 20% резерв тактов CPU для фоновых задач.", "Производительность",
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 0, 20,
            isFeatured: true,
            userWhy: "Windows отдает 100% мощности процессора активной игре или программе вместо искусственного резервирования 20% под фоновые службы.",
            techWhy: "SystemResponsiveness = 0 (DWORD). Отключает резервирование квантов CPU планировщиком Multimedia Class Scheduler Service (MMCSS).");

        RegTweak("network_throttling", "Отключить сетевое дросселирование", "Устраняет задержку сетевого стека, снижая пинг в играх.", "Производительность",
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10,
            isFeatured: true,
            userWhy: "Устраняет скрытый лимит Windows, сдерживающий передачу сетевых пакетов во время прослушивания музыки, стриминга в Discord или видео.",
            techWhy: "NetworkThrottlingIndex = 0xFFFFFFFF (Disabled). Отключает драйверное ограничение стека NDIS на 10 пакетов/мс для мультимедийных сессий.");

        RegTweak("game_scheduler_priority", "Повысить приоритет GPU в играх", "Назначает играм максимальный приоритет планировщика графики.", "Производительность",
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, 8,
            userWhy: "Видеокарта в первую очередь обрабатывает кадры запущенной игры, уменьшая статтеры и микрофризы от фоновых окон.",
            techWhy: "Tasks\\Games: GPU Priority = 8, Priority = 6, Scheduling Category = High. Переводит D3D/Vulkan контекст в приоритетный планировщик WDDM.");

        RegTweak("gamedvr_disable", "Отключить Xbox Game DVR", "Выключает фоновый захват экрана в играх, устраняя статтеры.", "Производительность",
            RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1,
            userWhy: "Устраняет периодические микрофризы в сетевых играх, вызванные скрытой фоновой записью геймплея.",
            techWhy: "GameDVR_Enabled = 0, GameDVR_FSEBehaviorMode = 2. Отключает фоновые буферы кодировщика NVENC/AMF в GameBarFT.dll.");

        RegTweak("game_bar_fts", "Отключить оверлей Game Bar", "Снимает оверлей Xbox и освобождает видеопамять.", "Производительность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1,
            userWhy: "Отключает тяжелый всплывающий интерфейс Xbox Game Bar (Win+G) и освобождает ресурсы GPU.",
            techWhy: "AppCaptureEnabled = 0. Запрещает внедрение оверлейных хуков bcastdvr.exe в полноэкранные пайплайны DirectX.");

        RegTweak("hags_gpu", "Аппаратное ускорение планирования GPU (HAGS)", "Снижает задержки графического конвейера видеокарты.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1,
            userWhy: "Позволяет современной видеокарте напрямую управлять своей видеопамятью, разгружая процессор и повышая 1% Low FPS.",
            techWhy: "HwSchMode = 2. Включает Hardware-Accelerated GPU Scheduling в модели драйверов WDDM 2.7+.",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("menu_show_delay", "Ускорить раскрытие меню (0 мс)", "Убирает задержку 400 мс при нажатии на контекстные меню и списки.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", "400",
            userWhy: "Контекстные меню открываются мгновенно, без стандартной задержки в полсекунды.",
            techWhy: "Control Panel\\Desktop: MenuShowDelay = '0' (вместо '400' мс). Устраняет системный таймер ожидания Win32 TrackPopupMenu.");

        RegTweak("mouse_hover_time", "Мгновенный отклик при наведении мыши", "Снижает тайм-аут регистрации курсора над элементами с 400 до 10 мс.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseHoverTime", "10", "400",
            userWhy: "Подсказки и эффекты кнопок подсвечиваются моментально при наведении курсора.",
            techWhy: "MouseHoverTime = '10' (мс). Регулирует системный параметр WM_MOUSEHOVER в оконном менеджере user32.dll.");

        RegTweak("auto_end_tasks", "Автоматически закрывать зависшие задачи", "При выключении ПК не ждет ручного подтверждения закрытия программ.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Desktop", "AutoEndTasks", "1", "0",
            userWhy: "При выключении система больше не будет показывать экран 'Это приложение мешает выключению' и ждать клика.",
            techWhy: "AutoEndTasks = '1'. Предписывает подсистеме CSRSS завершать зависшие процессы без вывода диалогового окна EndTask.");

        RegTweak("wait_to_kill", "Ускорить выключение ПК (WaitToKill = 2с)", "Сокращает время ожидания ответа от служб перед завершением работы.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "2000", "5000",
            userWhy: "Компьютер выключается и перезагружается ощутимо быстрее — система ждет зависшие службы всего 2 секунды.",
            techWhy: "WaitToKillServiceTimeout = '2000' (мс). Лимит ожидания диспетчера служб (SCM) перед отправкой сигнала принудительного завершения.");

        RegTweak("hung_app_timeout", "Быстрое завершение сбойных программ", "Сокращает тайм-аут распознавания зависших окон до 1 сек.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Desktop", "HungAppTimeout", "1000", "5000",
            userWhy: "Если программа намертво зависла, Windows сразу предложит ее закрыть, не дожидаясь нескольких минут.",
            techWhy: "HungAppTimeout = '1000' (мс). Тайм-аут отправки оконного сообщения WM_NULL перед пометкой процесса как 'Не отвечает'.");

        RegTweak("transparency_disable", "Отключить эффекты прозрачности", "Убирает эффекты размытия и акрила, разгружая видеопамять.", "Производительность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1,
            userWhy: "Разгружает встроенные видеокарты и старые GPU, экономя видеопамять за счет отключения эффектов акрила.",
            techWhy: "EnableTransparency = 0. Выключает сложные пиксельные шейдеры размытия фона Acrylic/Mica в конвейере DWM.");

        RegTweak("large_system_cache", "Включить большой системный кэш ОЗУ", "Выделяет больше оперативной памяти под дисковый кэш чтения/записи.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 1, 0,
            userWhy: "Подходит для ПК с 16+ ГБ оперативной памяти: ускоряет повторное открытие тяжелых программ и чтение файлов.",
            techWhy: "LargeSystemCache = 1. Переводит дисковый кэш файловой системы в серверный режим, увеличивая рабочий набор System Working Set.",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("llmnr_disable", "Отключить протокол LLMNR", "Ускоряет разрешение локальных DNS-запросов и убирает лишний трафик.", "Производительность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0, 1, delKey: true,
            userWhy: "Снижает задержки сетевого резолвера и защищает от перехвата учетных данных в локальных сетях.",
            techWhy: "DNSClient: EnableMulticast = 0. Блокирует широковещательные UDP 5355 запросы протокола Link-Local Multicast Name Resolution.");

        RegTweak("netbios_disable", "Ограничить запросы NetBIOS over TCP", "Снижает сетевой оверхед и закрывает устаревшие порты NetBIOS.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters", "NoNameReleaseOnDemand", 1, 0,
            risk: RiskLevel.Advanced,
            userWhy: "Закрывает уязвимости устаревшего сетевого протокола NetBIOS и убирает мусорный фоновый трафик.",
            techWhy: "NoNameReleaseOnDemand = 1. Запрещает сетевым узлам принудительно запрашивать освобождение NetBIOS-имен через NetBT.sys.");

        RegTweak("paging_executive", "Удерживать ядро Windows полностью в RAM", "Запрещает сброс исполняемых файлов ядра ОС в файл подкачки на диск.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, 0,
            risk: RiskLevel.Advanced,
            userWhy: "Ядро Windows и системные драйверы всегда находятся в оперативной памяти, исключая задержки чтения с накопителя.",
            techWhy: "DisablePagingExecutive = 1. Запрещает подсистеме Memory Manager сбрасывать код ntoskrnl.exe и системных драйверов в pagefile.sys.",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("power_throttling", "Отключить энергосберегающий троттлинг CPU", "Запрещает системе искусственно занижать тактовую частоту для фоновых задач.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, 0,
            risk: RiskLevel.Advanced,
            userWhy: "Процессор не сбрасывает частоту фоновых игровых потоков, античитов и Discord. Идеально для стационарных ПК.",
            techWhy: "PowerThrottlingOff = 1. Отключает механизм EcoQoS планировщика потоков Windows для фоновых потоков исполнения.");

        RegTweak("fse_fullscreen_optimizations", "Принудительный Fullscreen Mode в играх", "Устраняет задержку буферизации DWM в полноэкранных приложениях.", "Производительность",
            RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0,
            risk: RiskLevel.Advanced,
            userWhy: "Дает минимально возможный инпут-лаг мыши в шутерах за счет прямого вывода кадров на монитор без буфера DWM.",
            techWhy: "GameDVR_FSEBehaviorMode = 2. Обходит промежуточную очередь композитинга оконного менеджера DWM Desktop Composition.");

        RegTweak("vbs_disable", "Отключить VBS и Core Isolation", "Отключает изоляцию ядра. Дает чистый прирост FPS до 10% в играх, но ослабляет защиту ядра ОС.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, 1,
            risk: RiskLevel.Advanced,
            userWhy: "Дает мгновенный прирост 5–10% FPS в процессорозависимых играх, убирая оверхед постоянной виртуализации ядра Windows.",
            techWhy: "EnableVirtualizationBasedSecurity = 0. Выключает виртуализационную изоляцию HyperGuard и целостность кода гипервизора (HVCI).",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("ipv6_disable", "Отключить протокол IPv6", "Отключает IPv6 на уровне системы. Снижает задержки в ряде игр, но может затронуть сервисы Microsoft.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0xFF, 0x00,
            risk: RiskLevel.Advanced,
            userWhy: "Устраняет задержки двойного опроса DNS (IPv4/IPv6) у провайдеров, которые не поддерживают нативно IPv6.",
            techWhy: "DisabledComponents = 0xFF. Полностью отключает биндинги сетевого стека Tcpip6.sys для всех физических адаптеров.");

        RegTweak("ntfs_disable_last_access", "Отключить запись времени доступа NTFS", "Устраняет постоянную перезапись метаданных файлов при каждом чтении с диска.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 1, 0,
            isFeatured: true,
            userWhy: "Каждый раз, когда Windows или игра открывает файл, система больше не тратит ресурсы на запись времени 'Когда файл был прочитан'. Увеличивает ресурс SSD и отзывчивость диска.",
            techWhy: "FileSystem: NtfsDisableLastAccessUpdate = 1 (DWORD). Отключает обновление временных меток $STANDARD_INFORMATION в файловой записи MFT при операциях IRP_MJ_READ.");

        RegTweak("ntfs_disable_8dot3", "Отключить генерацию имен 8.3 (MS-DOS)", "Ускоряет создание и поиск файлов в папках с десятками тысяч файлов.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisable8dot3NameCreation", 1, 0,
            userWhy: "Windows перестает создавать для каждого файла устаревшие короткие дубликаты имен времен DOS. Значительно ускоряет работу с тяжелыми каталогами и кэшами программ.",
            techWhy: "FileSystem: NtfsDisable8dot3NameCreation = 1. Исключает коллизии хэшей коротких имен в директориях и снижает нагрузку на драйвер Ntfs.sys.");

        RegTweak("disable_dynamic_ticks", "Отключить Dynamic Ticking таймера CPU", "Стабилизирует интервалы системных прерываний, снижая разброс кадровой задержки (frametime).", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "DisableDynamicTick", 1, 0,
            risk: RiskLevel.Advanced,
            userWhy: "Устраняет плавающие микростаттеры в играх, запрещая процессору произвольно объединять такты системного таймера для энергосбережения.",
            techWhy: "DisableDynamicTick = 1 (эквивалент bcdedit /set disabledynamictick yes). Обеспечивает строгую периодичность системного тика APIC таймера.",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("disable_hibernation", "Отключить гибернацию (Файл hiberfil.sys)", "Удаляет скрытый системный файл hiberfil.sys, мгновенно освобождая от 8 до 64 ГБ на диске C:.", "Диск",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 0, 1,
            isFeatured: true,
            userWhy: "Если вы не пользуетесь режимом 'Гибернация', отключение мгновенно освобождает на системном диске гигабайты памяти, равные объему вашей оперативной памяти.",
            techWhy: "HibernateEnabled = 0 (эквивалент powercfg -h off). Удаляет файл сброса состояния ядра C:\\hiberfil.sys и выключает гибридный спящий режим Fast Startup.");

        RegTweak("disable_mmcss_audio_throttling", "Снять ограничение тактов аудиопотока", "Устраняет хрипы звука и рассинхрон при пиковых нагрузках на CPU.", "Производительность",
            RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Audio", "Background Only", "False", "True",
            userWhy: "Звук в Discord, играх и наушниках не будет заикаться или трещать, когда процессор нагружен на 100% тяжелой игрой или компиляцией.",
            techWhy: "Tasks\\Audio: Background Only = 'False', Scheduling Category = 'High'. Гарантирует повышенный приоритет потокам аудио-движка WASAPI в планировщике.");

        // 3. СЛУЖБЫ
        SrvTweak("service_diagtrack", "Отключить службу телеметрии (DiagTrack)", "Останавливает службу сбора и фоновой отправки телеметрии.", "DiagTrack", 2,
            isFeatured: false,
            userWhy: "Служба сбора данных телеметрии перестает нагружать процессор и накопитель в фоновом режиме.",
            techWhy: "Служба 'Connected User Experiences and Telemetry'. Занимается сбором трассировок ETW и агрегацией пользовательских событий.");

        SrvTweak("service_sysmain", "Отключить службу SysMain (Superfetch)", "Отключает лишнее кэширование, снижая нагрузку на SSD и RAM.", "SysMain", 2,
            isFeatured: true,
            userWhy: "Устраняет внезапные 100% всплески нагрузки на SSD-накопитель, продлевая ресурс его ячеек памяти.",
            techWhy: "Служба предвыборки Superfetch/SysMain. Создает непрерывные фоновые операции чтения/записи в C:\\Windows\\Prefetch.");

        SrvTweak("service_wsearch", "Отключить поиск Windows Search", "Останавливает непрерывную фоновую индексацию накопителей.", "WSearch", 2,
            userWhy: "Полностью прекращает скрытый нагрев диска и непрерывную перезапись поисковых баз данных Windows.",
            techWhy: "Служба индексатора SearchIndexer.exe. Освобождает файл базы данных Windows.edb, достигающий десятков гигабайт.");

        SrvTweak("service_wersvc", "Отключить регистрацию ошибок (WerSvc)", "Блокирует сбор дампов и отправку отчетов о сбоях в Microsoft.", "WerSvc", 3,
            userWhy: "При вылете программы система больше не висит по несколько минут, собирая тяжелые файлы отчетов на диск.",
            techWhy: "Служба Windows Error Reporting (WerSvc). Предотвращает сохранение минидампов и обращение к серверам Watson.");

        SrvTweak("service_remotereg", "Отключить службу удаленного реестра", "Блокирует удаленный сетевой доступ к системному реестру.", "RemoteRegistry", 4,
            userWhy: "Повышает безопасность ПК: никто в локальной сети не сможет дистанционно просматривать или изменять ваши настройки.",
            techWhy: "RemoteRegistry (regsvc.dll). Отключает RPC-эндпоинт удаленного вызова WinReg для внешних подключений.");

        SrvTweak("service_spooler", "Отключить диспетчер печати (Spooler)", "Если у вас нет принтера, служба не нужна и только занимает память.", "Spooler", 2,
            userWhy: "Если к компьютеру не подключен принтер, служба печати абсолютно бесполезна и только потребляет оперативную память.",
            techWhy: "Диспетчер очереди печати spoolsv.exe. Закрывает локальные RPC-пайпы печати и устраняет уязвимости PrintNightmare.");

        SrvTweak("service_fax", "Отключить службу факса (Fax)", "Служба факсимильной связи полностью бесполезна на современных ПК.", "Fax", 3,
            userWhy: "Факсимильная связь на современных игровых и рабочих ПК не используется и является мертвым грузом в памяти.",
            techWhy: "Служба fxssvc.exe. Полностью деактивирует системную очередь TAPI/факсимильных заданий.");

        SrvTweak("service_dmwappush", "Отключить WAP-маршрутизацию телеметрии", "Блокирует фоновую службу доставки push-сообщений телеметрии.", "dmwappushservice", 3,
            userWhy: "Убирает скрытый канал фонового получения системных push-команд от диагностических сервисов.",
            techWhy: "dmwappushservice (Device Management Wireless Application Protocol). Используется Microsoft для удаленной конфигурации телеметрии.");

        SrvTweak("service_touch_keyboard", "Отключить службу сенсорной клавиатуры", "Служба ввода TabletInputService не нужна для обычных ПК и мышей.", "TabletInputService", 3,
            userWhy: "Если у вас стандартный монитор без сенсорного экрана, сенсорная экранная клавиатура и рукописный ввод не нужны.",
            techWhy: "TabletInputService (Touch Keyboard and Handwriting Panel Service). Освобождает фоновый поток TabTip.exe.");

        SrvTweak("service_sensor", "Отключить службу системных датчиков", "Датчики освещения и поворота экрана (SensrSvc) не требуются десктопам.", "SensrSvc", 3,
            userWhy: "Стационарным компьютерам не требуются датчики автоповорота экрана и датчики освещенности.",
            techWhy: "SensrSvc (Sensor Service). Управляет подсистемой датчиков ориентации и люксметров в Windows Sensor API.");

        SrvTweak("service_retaildemo", "Отключить службу демонстрации RetailDemo", "Фоновый компонент демонстрации Windows в торговых сетях.", "RetailDemo", 3,
            userWhy: "Магазинный демонстрационный режим не нужен для домашнего или офисного ПК.",
            techWhy: "RetailDemo (Retail Demo Service). Фоновый агент витринных стендов розничных магазинов.");

        SrvTweak("service_alljoyn", "Отключить маршрутизатор AllJoyn Router", "Служба маршрутизации умных IoT устройств AllJoyn.", "AJRouter", 3,
            userWhy: "Если вы не управляете умным домом с этого компьютера через протокол AllJoyn, служба висит вхолостую.",
            techWhy: "AJRouter (AllJoyn Router Service). Занимается D-Bus маршрутизацией открытого протокола умного дома AllSeen Alliance.");

        SrvTweak("service_mapsbroker", "Отключить диспетчер карт (MapsBroker)", "Отключает фоновую синхронизацию системных карт MapsBroker.", "MapsBroker", 2,
            userWhy: "Встроенные офлайн-карты Windows перестанут выходить в интернет и обновлять кэш дорог в фоне.",
            techWhy: "MapsBroker (Downloaded Maps Manager). Управляет загруженными тайлами векторных карт картографического движка Bing Maps.");

        SrvTweak("service_xblauth", "Отключить диспетчер авторизации Xbox Live", "Останавливает сервис авторизации игр Xbox (если играете только в Steam/Epic).", "XblAuthManager", 3,
            risk: RiskLevel.Advanced,
            userWhy: "Полезно, если вы играете только в Steam, Epic Games или Riot. Внимание: отключит вход в игры из Microsoft Store и Xbox Game Pass.",
            techWhy: "XblAuthManager (Xbox Live Auth Manager). Обеспечивает тихую аутентификацию токенов XSTS в сетевом стеке Xbox Live.");

        SrvTweak("service_biometrics", "Отключить биометрию (Windows Hello)", "Служба сканирования отпечатков пальцев и распознавания лиц.", "WbioSrvc", 3,
            risk: RiskLevel.Advanced,
            userWhy: "Если вы входите в систему по обычному паролю или PIN-коду без сканера отпечатка пальца и камеры распознавания лица.",
            techWhy: "WbioSrvc (Windows Biometric Service). Управляет биометрическими адаптерами WBF (Windows Biometric Framework).");

        RegTweak("defender_disable", "Отключить Windows Defender", "Блокирует встроенный защитник. ВНИМАНИЕ: Требует ручного отключения 'Защиты от подделки' в настройках безопасности!", "Службы",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1, 0,
            risk: RiskLevel.Experimental,
            userWhy: "Полностью отключает фоновые проверки Windows Defender. Освобождает ОЗУ и процессор, но требует осторожности в интернете.",
            techWhy: "DisableAntiSpyware = 1, DisableRealtimeMonitoring = 1. Блокирует загрузку драйвера фильтрации файлов WdFilter.sys.");

        SrvTweak("service_wuauserv", "Остановить Центр Обновлений Windows", "Блокирует службу апдейтов (wuauserv). Вы перестанете получать системные патчи безопасности.", "wuauserv", 3,
            risk: RiskLevel.Experimental,
            userWhy: "Windows гарантированно перестанет скачивать обновления и перезагружать ПК во время вашей работы или каток.",
            techWhy: "wuauserv (Windows Update). Останавливает обработку очередей агента обновления WUAU (Windows Update Automatic Updates).");

        SrvTweak("service_waasmedic", "Остановить службу WaaSMedicSvc", "Блокирует встроенного агента автоматического восстановления Windows Update.", "WaaSMedicSvc", 3,
            risk: RiskLevel.Experimental,
            userWhy: "Не дает операционной системе самостоятельно снова включить службу обновлений Windows в обход ваших настроек.",
            techWhy: "WaaSMedicSvc (Windows Remediation Service). Встроенный компонент самовосстановления поврежденных файлов и служб апдейтов.");

        RegTweak("disable_delivery_optimization_service", "Полностью отключить службу Delivery Optimization", "Запрещает использование интернет-канала для скрытой раздачи обновлений.", "Службы",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\DoSvc", "Start", 4, 2,
            userWhy: "Служба DoSvc больше не сможет нагружать процессор и забивать интернет-канал незаметной фоновой P2P-раздачей системных файлов другим компьютерам.",
            techWhy: "Служба Win32: DoSvc (Delivery Optimization). Полный перевод в Disabled (Start=4). Исключает захват порта 7680 TCP.");

        // 4. ИНТЕРФЕЙС И ПРОВОДНИК (НЕ ВЛИЯЮТ НА ЗДОРОВЬЕ СИСТЕМЫ)
        RegTweak("classic_context_menu", "Классическое меню (Windows 10)", "Возвращает классическое меню без кнопки «Показать дополнительные параметры».", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", "",
            delKey: true,
            isFeatured: true,
            userWhy: "Возвращает быстрое и удобное меню Windows 10 по правому клику. Больше не нужно каждый раз нажимать Shift+F10 или 'Показать дополнительные параметры'.",
            techWhy: "Переопределяет COM-объект CLSID {86ca1aa0-34aa-4e8b-a509-50c905bae2a2} пустым InprocServer32, отключая XAML Context Menu в Проводнике.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("show_file_extensions", "Показывать расширения файлов", "Отображает реальные расширения файлов (.exe, .zip, .txt).", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0, 1,
            isFeatured: true,
            userWhy: "Защищает от вирусов: вы всегда будете видеть настоящее расширение файла (например, document.pdf.exe) и никогда не ошибетесь.",
            techWhy: "Explorer\\Advanced: HideFileExt = 0. Заставляет оболочку Shell32 отображать расширения имен зарегистрированных типов файлов.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("this_pc_default", "Открывать «Этот компьютер» в Проводнике", "Вместо стартового экрана «Главная» или «Быстрый доступ».", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1, 2,
            userWhy: "При открытии Проводника (Win+E) сразу открывается список ваших дисков (C:, D:), а не список последних открытых файлов.",
            techWhy: "LaunchTo = 1 (вместо 2 - Quick Access / Home). Задает начальный PIDL для корневого окна explorer.exe.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("taskbar_seconds", "Отображать секунды в часах Windows 11", "Выводит точное системное время с секундами в трее.", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSecondsInSystemClock", 1, 0,
            userWhy: "В часах в правом нижнем углу экрана отображаются точные секунды — удобно для фиксации времени и синхронизации.",
            techWhy: "ShowSecondsInSystemClock = 1. Включает секундный таймер обновления в XAML-контроле часов панели задач Windows 11.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("taskbar_widgets", "Скрыть виджеты с панели задач", "Убирает кнопку погоды и новостей «Виджеты» в левом углу панели задач.", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0, 1,
            userWhy: "Убирает назойливую панель новостей и погоды, освобождая место на панели задач и выгружая фоновый процесс Widgets.exe.",
            techWhy: "TaskbarDa = 0. Отключает отображение и фоновую инициализацию Windows Widget Board.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("taskbar_chat", "Скрыть значок «Чат» (Microsoft Teams)", "Убирает ненужную системную кнопку чата с панели задач.", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 0, 1,
            userWhy: "Убирает встроенную иконку чата Teams из панели задач Windows 11.",
            techWhy: "TaskbarMn = 0. Отключает интеграцию персональной версии Microsoft Teams в панели задач.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("explorer_compact_mode", "Компактный режим папок в Проводнике", "Уменьшает отступы между строками файлов для отображения большего списка.", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "UseCompactMode", 1, 0,
            userWhy: "Делает списки файлов компактными, как в Windows 10: на одном экране помещается в полтора раза больше строк без пустых отступов.",
            techWhy: "UseCompactMode = 1. Уменьшает отступы в элементах управления WinUI ItemsView в современном Проводнике.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("show_hidden_files", "Показывать скрытые файлы и папки", "Делает видимыми скрытые системные папки (AppData, ProgramData).", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 1, 2,
            userWhy: "Позволяет быстро находить скрытые системные папки, сохранения игр в AppData и конфигурационные файлы программ.",
            techWhy: "Advanced: Hidden = 1. Предписывает оболочке Shell отображать объекты с атрибутом FILE_ATTRIBUTE_HIDDEN.",
            rebootReq: "Требуется перезапуск Проводника");

        RegTweak("sticky_keys_disable", "Отключить залипание клавиш (Shift 5 раз)", "Предотвращает сворачивание игр при частом нажатии клавиши Shift.", "Интерфейс",
            RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510",
            userWhy: "В сетевых играх и шутерах частое нажатие на клавишу Shift больше не свернет игру с назойливым пищащим окном.",
            techWhy: "StickyKeys: Flags = '506'. Снимает бит SKF_HOTKEYACTIVE (0x04) в подсистеме специальных возможностей user32.dll.");

        RegTweak("filter_keys_disable", "Отключить фильтрацию ввода клавиш", "Устраняет задержки отклика клавиатуры при длительном удержании.", "Интерфейс",
            RegistryHive.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126",
            userWhy: "Исключает случайную задержку реакции клавиатуры при многократных и быстрых нажатиях в играх.",
            techWhy: "Keyboard Response: Flags = '122'. Деактивирует флаг FKF_HOTKEYACTIVE для предотвращения активации фильтрации ввода.");

        RegTweak("snap_assist_flyout", "Отключить всплывающие подсказки Snap Assist", "Убирает всплывающие подсказки компоновки окон при наведении на крестик.", "Интерфейс",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "SnapAssist", 0, 1,
            userWhy: "При наведении мыши на кнопку развертывания окна больше не выскакивает назойливая панель макетов экранов.",
            techWhy: "SnapAssist = 0. Отключает появление оверлея Win32 Flyout подсказок компоновщика Snap Layouts в Проводнике.");

        // 5. ОБНОВЛЕНИЯ WINDOWS
        RegTweak("disable_driver_updates", "Запретить замену драйверов через WU", "Предотвращает автоматическую замену ваших драйверов GPU более старыми версиями от Microsoft.", "Обновления",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, 0, delKey: true,
            isFeatured: true,
            userWhy: "Windows Update больше никогда не заменит ваш свежий драйвер видеокарты (NVIDIA/AMD) старой урезанной версией из каталога Microsoft.",
            techWhy: "WindowsUpdate: ExcludeWUDriversInQualityUpdate = 1. Исключает класс драйверов из поиска обновлений качества Windows Update Client.");

        RegTweak("delivery_opt_p2p", "Запретить P2P раздачу обновлений (Delivery)", "Windows больше не будет отдавать скачанные обновления другим ПК через интернет.", "Обновления",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization", "SystemSettingsDownloadMode", 0, 1,
            userWhy: "Экономит ваш интернет-трафик и пинг: ваш компьютер не будет использоваться как торрент-раздатчик системных обновлений для чужих ПК.",
            techWhy: "DeliveryOptimization: DODownloadMode = 0. Переводит службу доставки обновлений DoSvc в режим 'Только HTTP без P2P-пиринга'.");

        RegTweak("no_auto_reboot_users", "Запретить перезагрузку ПК при работе пользователя", "Блокирует принудительный рестарт после апдейтов, пока в системе есть активный пользователь.", "Обновления",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, 0, delKey: true,
            userWhy: "Компьютер никогда внезапно не перезагрузится посреди вашей важной работы или игры из-за скачанного обновления.",
            techWhy: "WindowsUpdate\\AU: NoAutoRebootWithLoggedOnUsers = 1. Блокирует таймер принудительной перезагрузки сессии Winlogon.");

        RegTweak("disable_speech_model_updates", "Отключить автообновление голосовых моделей", "Запрещает загрузку языковых пакетов распознавания речи в фоновом режиме.", "Обновления",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Speech", "AllowSpeechModelUpdate", 0, 1, delKey: true,
            userWhy: "Предотвращает внезапное скачивание многогигабайтных языковых баз распознавания речи в фоновом режиме.",
            techWhy: "Policies\\Microsoft\\Speech: AllowSpeechModelUpdate = 0. Запрещает системному планировщику фоновую синхронизацию SpeechModelUpdateTask.");

        RegTweak("disable_auto_update_download", "Только уведомлять о наличии обновлений", "Запрещает автоматическую загрузку тяжелых апдейтов без вашего согласия.", "Обновления",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", 2, 0, delKey: true,
            userWhy: "Windows Update найдет обновление, но не начнет скачивать его втихаря, забивая ваш интернет-канал. Вы сами решаете, когда нажать 'Скачать'.",
            techWhy: "AUOptions = 2 (Notify for download and notify for install). Переводит режим работы агента автоматических обновлений в ручной режим оповещения.");
    }
}
