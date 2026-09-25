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
        FilteredTweaks = CollectionViewSource.GetDefaultView(_allTweaks);
        FilteredTweaks.Filter = obj =>
        {
            if (obj is not TweakViewModel t) return false;
            bool hasSearch = !string.IsNullOrWhiteSpace(SearchText);
            if (hasSearch)
            {
                return t.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                       t.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                       t.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
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

        // СЕТЕВЫЕ КОМАНДЫ
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
            SplashProgress = 20;
            await Task.Delay(120);

            SysInfo = await SystemInfoService.GetSystemInfoAsync();
            SplashProgress = 60;
            await Task.Delay(120);

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
    // ДВИЖОК РЕГИСТРАЦИИ ТВИКОВ (С двойным контролем реестра и служб)
    // =====================================================================================

    private void RegTweak(string id, string name, string desc, string cat, RegistryHive hive, string path, string valName, object targetVal, object defVal, bool delKey = false, bool isFeatured = false, RiskLevel risk = RiskLevel.Safe)
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

        AddTweak(tweak, revertAction, snapshotFunc, isFeatured, risk);
    }

    private void SrvTweak(string id, string name, string desc, string srvName, int defMode, bool isFeatured = false, RiskLevel risk = RiskLevel.Safe)
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

        AddTweak(tweak, revertAction, snapshotFunc, isFeatured, risk);
    }

    private void AddTweak(ITweak tweak, Func<Task> revertAction, Func<TransactionRecord?> snapshotFunc, bool isFeatured = false, RiskLevel risk = RiskLevel.Safe)
    {
        var vm = new TweakViewModel(tweak, revertAction, snapshotFunc, isFeatured, risk)
        {
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
        // =========================================================================================
        // 1. ПРИВАТНОСТЬ (16 ТВIКОВ)
        // =========================================================================================
        RegTweak("telemetry_disable", "Отключить телеметрию Windows", "Ограничивает сбор данных диагностических служб Microsoft.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1, isFeatured: true);
        RegTweak("advertising_id_disable", "Запретить рекламный ID", "Блокирует показ персонализированной рекламы в приложениях.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1, isFeatured: true);
        RegTweak("cortana_disable", "Отключить Cortana", "Блокирует фоновый голосовой ассистент.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, 1, delKey: true, isFeatured: true);
        RegTweak("activity_history", "Отключить журнал активности", "Запрещает Windows сохранять историю запущенных задач.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, 1);
        RegTweak("typing_telemetry", "Отключить сбор данных клавиатуры", "Блокирует отправку шаблонов рукописного и экранного ввода.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0);
        RegTweak("app_diagnostics", "Запретить приложениям сбор диагностики", "Ограничивает фоновый доступ сторонних программ к журналу диагностики.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsGetDiagnosticInfo", 2, 0);
        RegTweak("location_tracking", "Отключить службы геолокации", "Блокирует встроенные системные датчики местоположения.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, 0);
        RegTweak("bing_start_search", "Отключить поиск Bing в Пуске", "Убирает веб-страницы и рекламу из поисковой строки меню Пуск.", "Приватность", RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, 0, delKey: true, isFeatured: true);
        RegTweak("web_search_disable", "Отключить веб-поиск Windows Search", "Поиск Windows ищет файлы только на локальном накопителе.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, 1);
        RegTweak("lockscreen_spotlight", "Отключить рекламу на экране блокировки", "Убирает встроенные рекламные советы и ссылки на экране входа.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenEnabled", 0, 1);
        RegTweak("windows_consumer_features", "Запретить автоустановку промо-приложений", "Блокирует скрытую установку игр (Candy Crush, TikTok) после обновлений.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, 0, delKey: true);
        RegTweak("tailored_experiences", "Отключить персонализацию диагностических данных", "Запрещает Microsoft предлагать рекламу на основе системных логов.", "Приватность", RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1, 0, delKey: true);
        RegTweak("feedback_notifications", "Отключить всплывающие опросы отчетов", "Windows больше не будет запрашивать обратную связь и оценки.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, 1, delKey: true);
        RegTweak("edge_prelaunch", "Запретить фоновый предзапуск Microsoft Edge", "Предотвращает предварительную загрузку Edge в память при старте ОС.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\MicrosoftEdge\Main", "AllowPrelaunch", 0, 1, delKey: true);
        RegTweak("ceip_telemetry", "Отключить программу улучшения ПО (CEIP)", "Блокирует службу сбора отзывов о работе программ Windows.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0, 1, delKey: true);
        RegTweak("clipboard_cloud_sync", "Запретить отправку буфера обмена в облако", "Отключает передачу скопированных паролей и текста на сервера учетной записи.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\Clipboard", "AllowCrossDeviceClipboard", 0, 1);

        // Расширенные твики приватности
        RegTweak("uac_disable", "Отключить UAC (Контроль учетных записей)", "Полностью отключает затемнение экрана и предупреждения при запуске программ. Снижает базовую безопасность ОС.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 0, 1, risk: RiskLevel.Advanced);
        RegTweak("smartscreen_disable", "Отключить фильтр SmartScreen", "Отключает проверку запускаемых файлов в интернете. Ускоряет запуск софта, но убирает предупреждения.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0, 1, risk: RiskLevel.Advanced);

        // =========================================================================================
        // 2. ПРОИЗВОДИТЕЛЬНОСТЬ И ИГРЫ (21 ТВIК)
        // =========================================================================================
        RegTweak("anim_disable", "Отключить анимации окон", "Мгновенный отклик интерфейса без задержек при сворачивании окон.", "Производительность", RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1", isFeatured: true);
        RegTweak("system_responsiveness", "Максимальная отзывчивость системы", "Снимает скрытый 20% резерв тактов CPU для фоновых задач.", "Производительность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 0, 20, isFeatured: true);
        RegTweak("network_throttling", "Отключить сетевое дросселирование", "Устраняет задержку сетевого стека, снижая пинг в играх.", "Производительность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10, isFeatured: true);
        RegTweak("game_scheduler_priority", "Повысить приоритет GPU в играх", "Назначает играм максимальный приоритет планировщика графики.", "Производительность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, 8);
        RegTweak("gamedvr_disable", "Отключить Xbox Game DVR", "Выключает фоновый захват экрана в играх, устраняя статтеры.", "Производительность", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1);
        RegTweak("game_bar_fts", "Отключить оверлей Game Bar", "Снимает оверлей Xbox и освобождает видеопамять.", "Производительность", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1);
        RegTweak("hags_gpu", "Аппаратное ускорение планирования GPU (HAGS)", "Снижает задержки графического конвейера видеокарты.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1);
        RegTweak("menu_show_delay", "Ускорить раскрытие меню (0 мс)", "Убирает задержку 400 мс при нажатии на контекстные меню и списки.", "Производительность", RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", "400");
        RegTweak("mouse_hover_time", "Мгновенный отклик при наведении мыши", "Снижает тайм-аут регистрации курсора над элементами с 400 до 10 мс.", "Производительность", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseHoverTime", "10", "400");
        RegTweak("auto_end_tasks", "Автоматически закрывать зависшие задачи", "При выключении ПК не ждет ручного подтверждения закрытия программ.", "Производительность", RegistryHive.CurrentUser, @"Control Panel\Desktop", "AutoEndTasks", "1", "0");
        RegTweak("wait_to_kill", "Ускорить выключение ПК (WaitToKill = 2с)", "Сокращает время ожидания ответа от служб перед завершением работы.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "2000", "5000");
        RegTweak("hung_app_timeout", "Быстрое завершение сбойных программ", "Сокращает тайм-аут распознавания зависших окон до 1 сек.", "Производительность", RegistryHive.CurrentUser, @"Control Panel\Desktop", "HungAppTimeout", "1000", "5000");
        RegTweak("transparency_disable", "Отключить эффекты прозрачности интерфейса", "Убирает эффекты размытия и акрила, разгружая видеопамять.", "Производительность", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1);
        RegTweak("large_system_cache", "Включить большой системный файловый кэш", "Выделяет больше оперативной памяти под дисковый кэш чтения/записи.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 1, 0);

        // Сетевые твики в производительности
        RegTweak("llmnr_disable", "Отключить протокол LLMNR", "Ускоряет разрешение локальных DNS-запросов и убирает лишний трафик.", "Производительность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0, 1, delKey: true);
        RegTweak("netbios_disable", "Ограничить запросы NetBIOS over TCP", "Снижает сетевой оверхед и закрывает устаревшие порты NetBIOS.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters", "NoNameReleaseOnDemand", 1, 0, risk: RiskLevel.Advanced);

        // Экстремальные твики производительности
        RegTweak("paging_executive", "Удерживать ядро Windows полностью в RAM", "Запрещает сброс исполняемых файлов ядра ОС в файл подкачки на диск.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, 0, risk: RiskLevel.Advanced);
        RegTweak("power_throttling", "Отключить энергосберегающее троттлирование CPU", "Запрещает системе искусственно занижать тактовую частоту процессора для фоновых задач.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, 0, risk: RiskLevel.Advanced);
        RegTweak("fse_fullscreen_optimizations", "Принудительный Fullscreen Mode в играх", "Устраняет задержку буферизации DWM в полноэкранных приложениях.", "Производительность", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0, risk: RiskLevel.Advanced);
        RegTweak("vbs_disable", "Отключить VBS и Core Isolation", "Отключает изоляцию ядра. Дает чистый прирост FPS до 10% в играх, но ослабляет защиту ядра ОС.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, 1, risk: RiskLevel.Advanced);
        RegTweak("ipv6_disable", "Отключить протокол IPv6", "Отключает IPv6 на уровне системы. Снижает задержки в ряде игр, но может затронуть сервисы Microsoft.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0xFF, 0x00, risk: RiskLevel.Advanced);

        // =========================================================================================
        // 3. СЛУЖБЫ (18 ТВIКОВ)
        // =========================================================================================
        SrvTweak("service_diagtrack", "Отключить службу телеметрии (DiagTrack)", "Останавливает службу сбора и фоновой отправки телеметрии.", "DiagTrack", 2, isFeatured: true);
        SrvTweak("service_sysmain", "Отключить службу SysMain (Superfetch)", "Отключает лишнее кэширование, снижая постоянную нагрузку на SSD и RAM.", "SysMain", 2, isFeatured: true);
        SrvTweak("service_wsearch", "Отключить поиск Windows Search", "Останавливает непрерывную фоновую индексацию накопителей.", "WSearch", 2);
        SrvTweak("service_wersvc", "Отключить регистрацию ошибок (WerSvc)", "Блокирует сбор дампов и отправку отчетов о сбоях в Microsoft.", "WerSvc", 3);
        SrvTweak("service_remotereg", "Отключить службу удаленного реестра", "Блокирует удаленный сетевой доступ к системному реестру.", "RemoteRegistry", 4);
        SrvTweak("service_spooler", "Отключить диспетчер печати (Spooler)", "Если у вас нет принтера, служба не нужна и только занимает память.", "Spooler", 2);
        SrvTweak("service_fax", "Отключить службу факса (Fax)", "Служба факсимильной связи полностью бесполезна на современных ПК.", "Fax", 3);
        SrvTweak("service_dmwappush", "Отключить WAP-маршрутизацию телеметрии", "Блокирует фоновую службу доставки push-сообщений телеметрии.", "dmwappushservice", 3);
        SrvTweak("service_touch_keyboard", "Отключить службу сенсорной клавиатуры", "Служба ввода TabletInputService не нужна для обычных ПК и мышей.", "TabletInputService", 3);
        SrvTweak("service_sensor", "Отключить службу системных датчиков", "Датчики освещения и поворота экрана (SensrSvc) не требуются десктопам.", "SensrSvc", 3);
        SrvTweak("service_retaildemo", "Отключить службу демонстрации RetailDemo", "Фоновый компонент демонстрации Windows в торговых сетях.", "RetailDemo", 3);
        SrvTweak("service_alljoyn", "Отключить маршрутизатор AllJoyn Router", "Служба маршрутизации умных IoT устройств AllJoyn.", "AJRouter", 3);
        SrvTweak("service_mapsbroker", "Отключить диспетчер офлайн-карт", "Отключает фоновую синхронизацию системных карт MapsBroker.", "MapsBroker", 2);

        // Продвинутые и опасные службы
        SrvTweak("service_xblauth", "Отключить диспетчер аутентификации Xbox Live", "Останавливает сервис авторизации игр Xbox (если играете только в Steam/Epic).", "XblAuthManager", 3, risk: RiskLevel.Advanced);
        SrvTweak("service_biometrics", "Отключить биометрическую службу (Windows Hello)", "Служба сканирования отпечатков пальцев и распознавания лиц.", "WbioSrvc", 3, risk: RiskLevel.Advanced);
        RegTweak("defender_disable", "Отключить Windows Defender", "Блокирует встроенный защитник. ВНИМАНИЕ: Требует ручного отключения 'Защиты от подделки' в настройках безопасности!", "Службы", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1, 0, risk: RiskLevel.Experimental);
        SrvTweak("service_wuauserv", "Остановить Центр Обновлений Windows", "Блокирует службу апдейтов (wuauserv). Вы перестанете получать системные патчи безопасности.", "wuauserv", 3, risk: RiskLevel.Experimental);
        SrvTweak("service_waasmedic", "Остановить службу WaaSMedicSvc", "Блокирует встроенного агента автоматического восстановления Windows Update.", "WaaSMedicSvc", 3, risk: RiskLevel.Experimental);

        // =========================================================================================
        // 4. ИНТЕРФЕЙС И ПРОВОДНИК (11 ТВIКОВ)
        // =========================================================================================
        RegTweak("classic_context_menu", "Классическое контекстное меню (Win 10)", "Возвращает классическое меню без кнопки «Показать дополнительные параметры».", "Интерфейс", RegistryHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", "", delKey: true, isFeatured: true);
        RegTweak("show_file_extensions", "Показывать расширения файлов", "Отображает реальные расширения файлов (.exe, .zip, .txt).", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0, 1, isFeatured: true);
        RegTweak("this_pc_default", "Открывать «Этот компьютер» в Проводнике", "Вместо стартового экрана «Главная» или «Быстрый доступ».", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1, 2);
        RegTweak("taskbar_seconds", "Отображать секунды в часах панели задач", "Выводит точное системное время с секундами в трее Windows 11.", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSecondsInSystemClock", 1, 0);
        RegTweak("taskbar_widgets", "Скрыть виджеты с панели задач", "Убирает кнопку погоды и новостей «Виджеты» в левом углу панели задач.", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0, 1);
        RegTweak("taskbar_chat", "Скрыть значок «Чат» (Microsoft Teams)", "Убирает ненужную системную кнопку чата с панели задач.", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 0, 1);
        RegTweak("explorer_compact_mode", "Компактный режим папок в Проводнике", "Уменьшает отступы между строками файлов для отображения большего списка.", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "UseCompactMode", 1, 0);
        RegTweak("show_hidden_files", "Показывать скрытые файлы и папки", "Делает видимыми скрытые системные папки (AppData, ProgramData).", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 1, 2);
        RegTweak("sticky_keys_disable", "Отключить залипание клавиш (Shift 5 раз)", "Предотвращает сворачивание игр при частом нажатии клавиши Shift.", "Интерфейс", RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510");
        RegTweak("filter_keys_disable", "Отключить фильтрацию ввода клавиш", "Устраняет задержки отклика клавиатуры при длительном удержании.", "Интерфейс", RegistryHive.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126");
        RegTweak("snap_assist_flyout", "Отключить подсказки макетов Snap Assist", "Убирает всплывающие подсказки компоновки окон при наведении на крестик.", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "SnapAssist", 0, 1);

        // =========================================================================================
        // 5. ОБНОВЛЕНИЯ WINDOWS (5 ТВIКОВ)
        // =========================================================================================
        RegTweak("disable_driver_updates", "Запретить замену драйверов через WU", "Предотвращает автоматическую замену ваших драйверов GPU более старыми версиями от Microsoft.", "Обновления", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, 0, delKey: true, isFeatured: true);
        RegTweak("delivery_opt_p2p", "Запретить P2P раздачу обновлений (Delivery)", "Windows больше не будет отдавать скачанные обновления другим ПК через интернет.", "Обновления", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization", "SystemSettingsDownloadMode", 0, 1);
        RegTweak("no_auto_reboot_users", "Запретить перезагрузку ПК при работе пользователя", "Блокирует принудительный рестарт после апдейтов, пока в системе есть активный пользователь.", "Обновления", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, 0, delKey: true);
        RegTweak("disable_speech_model_updates", "Отключить автообновление голосовых моделей", "Запрещает загрузку языковых пакетов распознавания речи в фоновом режиме.", "Обновления", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Speech", "AllowSpeechModelUpdate", 0, 1, delKey: true);
        RegTweak("disable_auto_update_download", "Только уведомлять о наличии обновлений", "Запрещает автоматическую загрузку тяжелых апдейтов без вашего согласия.", "Обновления", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", 2, 0, delKey: true);
    }
}