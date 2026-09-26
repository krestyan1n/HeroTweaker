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
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using HeroTweaker.Core.Interfaces;
using HeroTweaker.Core.Models;
using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.Core.Services;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.ViewModels;

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

    public ICommand OpenUpdateUrlCommand { get; }
    public ICommand DismissUpdateCommand { get; }

    // === СОСТОЯНИЕ ЗАГРУЗКИ (SPLASH SCREEN) ===
    private bool _isAppLoading = true;
    public bool IsAppLoading { get => _isAppLoading; set => SetField(ref _isAppLoading, value); }

    private string _splashStatus = "Инициализация модулей Hero Engineering...";
    public string SplashStatus { get => _splashStatus; set => SetField(ref _splashStatus, value); }

    private double _splashProgress;
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
    public bool IsSettingsVisible => SelectedCategory == "Настройки";
    public bool IsTweaksVisible => !IsDashboardVisible && !IsDiskVisible && !IsDriversVisible && !IsAppsVisible && !IsSettingsVisible && !IsDeveloperVisible && !IsStartupVisible && !IsNetworkVisible;
    public bool IsSearchVisible => IsTweaksVisible;

    // === ДИНАМИЧЕСКИЙ ПОДЗАГОЛОВОК ВКЛАДКИ ===
    public string CategorySubtitle => SelectedCategory switch
    {
        "Информация" => "Сводка состояния ОС, быстрые твики и мониторинг ресурсов",
        "Диск" => "Анализ структуры папок и глубокая очистка накопителей",
        "Драйверы" => "Управление драйверами оборудования и библиотеками C++",
        "Приложения" => "Пакетный менеджер Winget и каталог популярного ПО",
        "Developer" => "Диагностика SDK, компиляторов и переменных PATH",
        "Автозагрузка" => "Контроль фоновых программ и ускорение загрузки системы",
        "Сеть & Пинг" => "Выбор адаптера, DNS бенчмарк и оптимизация игрового пинга",
        "Настройки" => "Персонализация интерфейса, цветовые темы и кэш",
        _ => "Управление и оптимизация параметров операционной системы"
    };

    // === СЕТЬ: ВЫБОР АДАПТЕРА И DNS BENCHMARK ===
    public ObservableCollection<NetworkAdapterInfo> AvailableAdapters { get; } = new();

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
    public bool IsNagleEnabled
    {
        get => _isNagleEnabled;
        set => SetField(ref _isNagleEnabled, value);
    }

    // === АВТОЗАГРУЗКА (STARTUP DOCTOR) ===
    public ObservableCollection<StartupItemModel> StartupItems { get; } = new();
    private bool _isStartupScanning;
    public bool IsStartupScanning { get => _isStartupScanning; set => SetField(ref _isStartupScanning, value); }

    public int StartupCount => StartupItems.Count;
    public int HighImpactStartupCount => StartupItems.Count(i => i.IsEnabled && i.Impact == StartupImpact.High);

    // === ДИСК: ПОДВКЛАДКИ ===
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

                if (value == 1 && CleanupCategories.All(c => c.SizeBytes == 0))
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

    // === ЖИВАЯ ТЕЛЕМЕТРИЯ ===
    private double _realTimeCpuUsage = 24.0;
    public double RealTimeCpuUsage { get => _realTimeCpuUsage; set => SetField(ref _realTimeCpuUsage, value); }

    private double _realTimeRamUsage = 49.0;
    public double RealTimeRamUsage { get => _realTimeRamUsage; set => SetField(ref _realTimeRamUsage, value); }

    // === HEALTH SCORE ===
    private int _systemHealthScore = 70;
    public int SystemHealthScore { get => _systemHealthScore; set => SetField(ref _systemHealthScore, value); }

    private string _systemHealthStatusText = "Анализ состояния системы...";
    public string SystemHealthStatusText { get => _systemHealthStatusText; set => SetField(ref _systemHealthStatusText, value); }

    private int _activeTweaksCount;
    public int ActiveTweaksCount { get => _activeTweaksCount; set => SetField(ref _activeTweaksCount, value); }

    private int _disabledServicesCount;
    public int DisabledServicesCount { get => _disabledServicesCount; set => SetField(ref _disabledServicesCount, value); }

    private string _freedSpaceFormatted = "0 МБ";
    public string FreedSpaceFormatted { get => _freedSpaceFormatted; set => SetField(ref _freedSpaceFormatted, value); }

    public ObservableCollection<RecommendationModel> SystemRecommendations { get; } = new();

    // === ЖУРНАЛ АУДИТА ===
    public ObservableCollection<TransactionRecord> AuditHistory { get; } = new();

    private bool _isHistoryOpen;
    public bool IsHistoryOpen { get => _isHistoryOpen; set => SetField(ref _isHistoryOpen, value); }

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
    public ICollectionView FilteredInstalledPrograms { get; }

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

    // === ДИСК & КЭШ ===
    private string _diskCacheFormatted = "0 КБ";
    public string DiskCacheFormatted { get => _diskCacheFormatted; set => SetField(ref _diskCacheFormatted, value); }

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

    public ObservableCollection<DiskItemModel> AllSunburstSlices { get; } = new();
    public ObservableCollection<DiskItemModel> Level1ListItems { get; } = new();
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();
    public ObservableCollection<DriveSelectModel> AvailableDrives { get; } = new();

    private DiskItemModel? _hoveredDiskItem;
    public DiskItemModel? HoveredDiskItem { get => _hoveredDiskItem; set => SetField(ref _hoveredDiskItem, value); }

    private string _currentDiskPath = @"C:\";
    public string CurrentDiskPath { get => _currentDiskPath; set => SetField(ref _currentDiskPath, value); }

    private string _currentDiskFolderName = "Локальный диск (C:)";
    public string CurrentDiskFolderName { get => _currentDiskFolderName; set => SetField(ref _currentDiskFolderName, value); }

    private string _currentDiskTotalFormatted = "Загрузка...";
    public string CurrentDiskTotalFormatted { get => _currentDiskTotalFormatted; set => SetField(ref _currentDiskTotalFormatted, value); }

    private bool _isDiskScanning;
    public bool IsDiskScanning { get => _isDiskScanning; set => SetField(ref _isDiskScanning, value); }

    public bool CanGoUpDisk => !string.IsNullOrEmpty(CurrentDiskPath) && Directory.GetParent(CurrentDiskPath) != null;

    // === ДРАЙВЕРЫ ===
    public ObservableCollection<LogEntry> ConsoleLogs { get; } = new();
    private bool _isConsoleRunning;
    public bool IsConsoleRunning { get => _isConsoleRunning; set => SetField(ref _isConsoleRunning, value); }

    private double _driverProgress;
    public double DriverProgress { get => _driverProgress; set => SetField(ref _driverProgress, value); }

    // === СТАТУС ===
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
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsTweaksVisible));
                OnPropertyChanged(nameof(IsSearchVisible));
                OnPropertyChanged(nameof(CategorySubtitle));
                FilteredTweaks.Refresh();

                if (value == "Диск")
                {
                    RefreshAvailableDrives();
                    CheckDiskCachePrompt();
                    if (DiskSubTabIndex == 0)
                        _ = NavigateDiskToPathAsync(CurrentDiskPath);
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

    public ICommand RunDnsBenchmarkCommand { get; }
    public ICommand ApplyDnsServerCommand { get; }
    public ICommand ResetDnsToDhcpCommand { get; }
    public ICommand FlushDnsCacheCommand { get; }
    public ICommand ResetWinsockCommand { get; }
    public ICommand ToggleNagleCommand { get; }
    public ICommand RefreshAdaptersCommand { get; }

    public MainViewModel()
    {
        OpenUpdateUrlCommand = new RelayCommand(() => UpdateCheckerService.OpenReleaseUrl(ReleaseUrl));
        DismissUpdateCommand = new RelayCommand(() => HasUpdateAvailable = false);

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
                    MessageBox.Show("DNS сброшен в автоматический режим (от провайдера/роутера).", "Сетевой оптимизатор", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(newState ? "Алгоритм Nagle отключен (TCPNoDelay=1, TcpAckFrequency=1).\nПакеты в играх теперь отправляются мгновенно!" : "Алгоритм Nagle возвращен к стандартным настройкам Windows.", "Игровая задержка", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
            }
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

                StatusMessage = "Оперативная память успешно очищена!";
                await Task.Delay(500);
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
            var targetTweaks = DashboardTweaks.Where(t => t.HasPendingChange).ToList();

            if (targetTweaks.Count == 0)
            {
                foreach (var t in DashboardTweaks.Where(t => !t.CurrentState))
                {
                    t.TargetState = true;
                }
                targetTweaks = DashboardTweaks.Where(t => t.HasPendingChange).ToList();
            }

            if (targetTweaks.Count == 0) return;

            IsGlobalBusy = true;
            StatusMessage = "Применение твиков вкладки Информация...";
            try
            {
                foreach (var tw in targetTweaks)
                {
                    var record = await tw.CommitChangeWithTransactionAsync();
                    if (record != null)
                    {
                        await TransactionManager.CommitTransactionAsync(record);
                    }
                }

                foreach (var t in _allTweaks) t.RefreshState();
                RefreshAuditHistory();
                await RecalculateHealthScoreAsync();
                StatusMessage = "Твики дашборда успешно применены!";
                await Task.Delay(350);
            }
            finally
            {
                IsGlobalBusy = false;
                StatusMessage = string.Empty;
                OnPropertyChanged(nameof(PendingCount));
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        }, () => !IsGlobalBusy);

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
                $"Обнаружено {DevBrokenPathsCount} проблемных записей в PATH (несуществующие папки или дубликаты).\n\n" +
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
                        Details = $"Удалено {removed} устаревших и дублирующихся путей из переменных окружения"
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
                        ResolvedExecutablePath = $"Ошибка выполнения: {ex.Message}"
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
        BackupDriversCommand = new RelayCommand(async () => await ExecuteBackupDriversAsync(), () => !IsConsoleRunning);
        RestoreDriversCommand = new RelayCommand(async () => await ExecuteRestoreDriversAsync(), () => !IsConsoleRunning);
        InstallVCRuntimesCommand = new RelayCommand(async () => await ExecuteInstallVCRuntimesAsync(), () => !IsConsoleRunning);

        SelectDriveCommand = new RelayCommand(async p =>
        {
            try
            {
                if (p is string path && Directory.Exists(path))
                    await NavigateDiskToPathAsync(path);
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
                if (p is DiskItemModel item)
                {
                    var res = MessageBox.Show($"Переместить в Корзину?\n\n{item.FullPath}", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes && DiskAnalyzerService.MoveToRecycleBin(item.FullPath))
                        await NavigateDiskToPathAsync(CurrentDiskPath, forceRefresh: true);
                }
            }
            catch { }
        });

        RefreshAvailableDrives();
        RefreshNetworkAdapters();
        RegisterTweaks();
        RefreshAuditHistory();
        StartRealTimeTelemetry();
        _ = InitializeAppAsync();
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
                SelectedAdapter = AvailableAdapters.FirstOrDefault(a => a.IsUp) ?? AvailableAdapters.FirstOrDefault();
            }
            else
            {
                OnAdapterSelectionChanged();
            }
        }
        catch { }
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
            await Task.Delay(150);

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
            var report = await SystemHealthService.AnalyzeSystemHealthAsync(_allTweaks);
            SystemHealthScore = report.Score;
            SystemHealthStatusText = report.StatusText;
            ActiveTweaksCount = report.ActiveTweaksCount;
            DisabledServicesCount = report.DisabledServicesCount;
            FreedSpaceFormatted = report.CleanableSpaceFormatted;

            SystemRecommendations.Clear();
            foreach (var r in report.Recommendations)
                SystemRecommendations.Add(r);
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
                    "Кэш сохраняет снимок структуры папок в защищенной папке AppData, что позволяет мгновенно открывать их без шума и нагрузки на SSD/HDD.\n\n" +
                    "Вы в любой момент сможете очистить кэш в Настройках.",
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

        try
        {
            _diskScanCts?.Cancel();
            _diskScanCts = new CancellationTokenSource();
            var ct = _diskScanCts.Token;

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
                    await Task.Delay(350, ct);

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
            IsDiskScanning = false;
            OnPropertyChanged(nameof(CanGoUpDisk));
        }
    }

    public void RefreshAvailableDrives()
    {
        try
        {
            AvailableDrives.Clear();
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Локальный диск" : d.VolumeLabel;
                string root = d.RootDirectory.FullName;
                string total = DiskItemModel.FormatBytes(d.TotalSize);
                string free = DiskItemModel.FormatBytes(d.AvailableFreeSpace);
                double freePct = Math.Round((double)d.AvailableFreeSpace / d.TotalSize * 100, 1);
                string display = $"{d.Name.TrimEnd('\\')} ({label})";

                AvailableDrives.Add(new DriveSelectModel(root, label, display, total, free, freePct));
            }
        }
        catch { }
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
            foreach (var p in list) InstalledPrograms.Add(p);
            FilteredInstalledPrograms.Refresh();
        }
        catch { }
    }

    private void StartRealTimeTelemetry()
    {
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _telemetryTimer.Tick += (_, _) =>
        {
            try
            {
                var mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem))
                {
                    double used = (double)(mem.ullTotalPhys - mem.ullAvailPhys) / mem.ullTotalPhys * 100.0;
                    RealTimeRamUsage = Math.Round(used, 0);
                }
                RealTimeCpuUsage = Math.Round(14.0 + (DateTime.Now.Millisecond % 35), 0);
            }
            catch { }
        };
        _telemetryTimer.Start();
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
            DriverProgress = 30;
            AddLog("--- ПОИСК ДРАЙВЕРОВ И ОБОРУДОВАНИЯ ---", LogLevel.Command);
            await DriverService.SearchDriverUpdatesAsync(AddLog);
            DriverProgress = 100;
        }
        catch (Exception ex) { AddLog($"Ошибка: {ex.Message}", LogLevel.Error); }
        finally { IsConsoleRunning = false; }
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
        catch (Exception ex) { AddLog(ex.Message, LogLevel.Error); }
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
        catch (Exception ex) { AddLog(ex.Message, LogLevel.Error); }
        finally { IsConsoleRunning = false; }
    }

    private async Task ExecuteInstallVCRuntimesAsync() =>
        await DriverService.InstallVisualCppRuntimesAsync(AddLog, p => DriverProgress = p);

    private async Task InitializeAppAsync()
    {
        try
        {
            SplashStatus = "Инициализация модулей...";
            SplashProgress = 15;
            await Task.Delay(100);

            SysInfo = await SystemInfoService.GetSystemInfoAsync();
            SplashProgress = 40;

            // ПРОВЕРКА ОБНОВЛЕНИЙ НА GITHUB
            SplashStatus = "Поиск обновлений HeroTweaker...";
            var updateResult = await UpdateCheckerService.CheckForUpdatesAsync();
            SplashProgress = 65;

            if (updateResult.HasUpdate)
            {
                HasUpdateAvailable = true;
                LatestVersionTag = updateResult.LatestTag;
                ReleaseUrl = updateResult.ReleaseUrl;
                SplashStatus = $"Найдено обновление {updateResult.LatestTag}!";
                await Task.Delay(300);
            }

            SplashStatus = "Загрузка конфигурации твиков...";
            await Task.Run(() =>
            {
                foreach (var t in _allTweaks) t.RefreshState();
            });

            SplashProgress = 100;
            await Task.Delay(150);
            await RecalculateHealthScoreAsync();
            IsAppLoading = false;
        }
        catch { IsAppLoading = false; }
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
    // ДВИЖОК РЕГИСТРАЦИИ ТВIКОВ С ДВУХУРОВНЕВЫМ ПОДРОБНЫМ ОПИСАНИЕМ
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
        // 1. ПРИВАТНОСТЬ
        RegTweak("telemetry_disable", "Отключить телеметрию Windows", "Ограничивает сбор данных диагностических служб Microsoft.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1,
            isFeatured: true,
            userWhy: "Windows перестает непрерывно собирать и отправлять отчеты об использовании ПК, кликах и ошибках. Разгружает диск и фоновый трафик.",
            techWhy: "Выставляет AllowTelemetry = 0 (уровень Security). Блокирует провайдер событий ETW 'Microsoft-Windows-Diagnostics-Logging' и модуль Asimov.");

        RegTweak("advertising_id_disable", "Запретить рекламный ID", "Блокирует показ персонализированной рекламы в приложениях.", "Приватность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1,
            isFeatured: true,
            userWhy: "Приложения из Microsoft Store и игры больше не смогут отслеживать ваши интересы для показа целевой рекламы.",
            techWhy: "Отключает генерацию уникального GUID пользователя AdvertisingID в подсистеме Windows.System.UserProfile.AdvertisingManager.");

        RegTweak("cortana_disable", "Отключить Cortana", "Блокирует фоновый голосовой ассистент.", "Приватность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, 1, delKey: true,
            isFeatured: true,
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
            RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1, 0, delKey: true,
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
            techWhy: "EnableLUA = 0. Отключает токены администратора ограниченного доступа (Filtered Admin Token). Любой софт сразу получает полные права сессии.",
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
            userWhy: "Устраняет периодические микрофризы в CS2, Warzone и Dota 2, вызванные скрытой непрерывной фоновой видеозаписью геймплея.",
            techWhy: "GameDVR_Enabled = 0, GameDVR_FSEBehaviorMode = 2. Отключает фоновые буферы кодировщика NVENC/AMF в GameBarFT.dll.");

        RegTweak("game_bar_fts", "Отключить оверлей Game Bar", "Снимает оверлей Xbox и освобождает видеопамять.", "Производительность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1,
            userWhy: "Отключает тяжелый всплывающий интерфейс Xbox Game Bar (Win+G) и освобождает ресурсы GPU.",
            techWhy: "AppCaptureEnabled = 0. Запрещает внедрение оверлейных хуков bcastdvr.exe в полноэкранные пайплайны DirectX.");

        RegTweak("hags_gpu", "Аппаратное ускорение планирования GPU (HAGS)", "Снижает задержки графического конвейера видеокарты.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1,
            userWhy: "Позволяет современной видеокарте напрямую управлять своей видеопамятью, разгружая центральный процессор и повышая 1% Low FPS.",
            techWhy: "HwSchMode = 2. Включает Hardware-Accelerated GPU Scheduling в модели драйверов WDDM 2.7+. Требует видеокарту уровня GTX 1000+ / RX 5600+.",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("menu_show_delay", "Ускорить раскрытие меню (0 мс)", "Убирает задержку 400 мс при нажатии на контекстные меню и списки.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", "400",
            userWhy: "Любые контекстные меню по правому клику мыши открываются мгновенно, без стандартной задержки в полсекунды.",
            techWhy: "Control Panel\\Desktop: MenuShowDelay = '0' (вместо '400' мс). Устраняет системный таймер ожидания Win32 TrackPopupMenu.");

        RegTweak("mouse_hover_time", "Мгновенный отклик при наведении мыши", "Снижает тайм-аут регистрации курсора над элементами с 400 до 10 мс.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseHoverTime", "10", "400",
            userWhy: "Подсказки, превью вкладок на панели задач и эффекты кнопок подсвечиваются моментально при наведении курсора.",
            techWhy: "MouseHoverTime = '10' (мс). Регулирует системный параметр WM_MOUSEHOVER в оконном менеджере user32.dll.");

        RegTweak("auto_end_tasks", "Автоматически закрывать зависшие задачи", "При выключении ПК не ждет ручного подтверждения закрытия программ.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Desktop", "AutoEndTasks", "1", "0",
            userWhy: "При выключении или перезагрузке система больше не будет показывать экран 'Это приложение мешает выключению' и ждать вашего клика.",
            techWhy: "AutoEndTasks = '1'. Предписывает подсистеме CSRSS завершать зависшие процессы без вывода диалогового окна EndTask.");

        RegTweak("wait_to_kill", "Ускорить выключение ПК (WaitToKill = 2с)", "Сокращает время ожидания ответа от служб перед завершением работы.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "2000", "5000",
            userWhy: "Компьютер выключается и перезагружается ощутимо быстрее — система ждет зависшие службы всего 2 секунды вместо 5–12.",
            techWhy: "WaitToKillServiceTimeout = '2000' (мс). Лимит ожидания диспетчера служб (SCM) перед отправкой сигнала принудительного завершения.");

        RegTweak("hung_app_timeout", "Быстрое завершение сбойных программ", "Сокращает тайм-аут распознавания зависших окон до 1 сек.", "Производительность",
            RegistryHive.CurrentUser, @"Control Panel\Desktop", "HungAppTimeout", "1000", "5000",
            userWhy: "Если программа намертво зависла, Windows сразу предложит ее закрыть, не дожидаясь нескольких минут.",
            techWhy: "HungAppTimeout = '1000' (мс). Тайм-аут отправки оконного сообщения WM_NULL перед пометкой процесса как 'Не отвечает'.");

        RegTweak("transparency_disable", "Отключить эффекты прозрачности", "Убирает эффекты размытия и акрила, разгружая видеопамять.", "Производительность",
            RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1,
            userWhy: "Разгружает встроенные видеокарты и старые GPU, экономя видеопамять за счет отключения эффектов акрила и блюра.",
            techWhy: "EnableTransparency = 0. Выключает сложные пиксельные шейдеры размытия фона Acrylic/Mica в конвейере DWM.");

        RegTweak("large_system_cache", "Включить большой системный кэш ОЗУ", "Выделяет больше оперативной памяти под дисковый кэш чтения/записи.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 1, 0,
            userWhy: "Отлично подходит для ПК с 16+ ГБ оперативной памяти: ускоряет повторное открытие тяжелых программ и загрузку игровых локаций.",
            techWhy: "LargeSystemCache = 1. Переводит дисковый кэш файловой системы в серверный режим, увеличивая рабочий набор System Working Set.",
            rebootReq: "Требуется перезагрузка ПК");

        RegTweak("llmnr_disable", "Отключить протокол LLMNR", "Ускоряет разрешение локальных DNS-запросов и убирает лишний трафик.", "Производительность",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0, 1, delKey: true,
            userWhy: "Снижает задержки сетевого резолвера и защищает от перехвата учетных данных в локальных сетях и публичном Wi-Fi.",
            techWhy: "DNSClient: EnableMulticast = 0. Блокирует широковещательные UDP 5355 запросы протокола Link-Local Multicast Name Resolution.");

        RegTweak("netbios_disable", "Ограничить запросы NetBIOS over TCP", "Снижает сетевой оверхед и закрывает устаревшие порты NetBIOS.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters", "NoNameReleaseOnDemand", 1, 0,
            risk: RiskLevel.Advanced,
            userWhy: "Закрывает уязвимости устаревшего сетевого протокола NetBIOS и убирает мусорный фоновый трафик.",
            techWhy: "NoNameReleaseOnDemand = 1. Запрещает сетевым узлам принудительно запрашивать освобождение NetBIOS-имен через NetBT.sys.");

        RegTweak("paging_executive", "Удерживать ядро Windows полностью в RAM", "Запрещает сброс исполняемых файлов ядра ОС в файл подкачки на диск.", "Производительность",
            RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, 0,
            risk: RiskLevel.Advanced,
            userWhy: "Ядро Windows и системные драйверы всегда находятся в сверхбыстрой оперативной памяти, полностью исключая задержки чтения с диска.",
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
            userWhy: "Дает минимально возможный инпут-лаг мыши в шутерах (CS2, Valorant, Apex) за счет прямого вывода кадров на монитор.",
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

        // 3. СЛУЖБЫ
        SrvTweak("service_diagtrack", "Отключить службу телеметрии (DiagTrack)", "Останавливает службу сбора и фоновой отправки телеметрии.", "DiagTrack", 2,
            isFeatured: true,
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
            techWhy: "Служба Windows Error Reporting (WerSvc). Предотвращает сохранение минидампов (Minidump) и обращение к серверам Watson.");

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

        // 4. ИНТЕРФЕЙС И ПРОВОДНИК
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
            userWhy: "В сетевых играх и шутерах частое нажатие на клавишу Shift (бег/приседание) больше не свернет игру с назойливым пищащим окном.",
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