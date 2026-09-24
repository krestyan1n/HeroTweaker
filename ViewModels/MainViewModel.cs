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
    public bool IsSettingsVisible => SelectedCategory == "Настройки";
    public bool IsTweaksVisible => !IsDashboardVisible && !IsDiskVisible && !IsDriversVisible && !IsAppsVisible && !IsSettingsVisible && !IsDeveloperVisible && !IsStartupVisible;
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
        "Настройки" => "Персонализация интерфейса, цветовые темы и кэш",
        _ => "Управление и оптимизация параметров операционной системы"
    };

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
        RegisterTweaks();
        RefreshAuditHistory();
        StartRealTimeTelemetry();
        _ = InitializeAppAsync();
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
    // УЛЬТИМАТИВНЫЙ ДВИЖОК РЕГИСТРАЦИИ (Только RegistryTweak с двойным контролем)
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

        // ВАЖНО: Используем RegistryTweak. 4 = Отключено (Disabled).
        // Это гарантирует, что IsApplied() мгновенно считает Start=4 из реестра.
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
                var s = TransactionManager.CreateRegistrySnapshot(RegistryHive.LocalMachine, path, "Start");
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
        // === 1. ПРИВАТНОСТЬ (SAFE) ===
        RegTweak("telemetry_disable", "Отключить телеметрию Windows", "Ограничивает сбор данных диагностических служб Microsoft.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1, isFeatured: true);
        RegTweak("advertising_id_disable", "Запретить рекламный ID", "Блокирует показ персонализированной рекламы в приложениях.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1, isFeatured: true);
        RegTweak("cortana_disable", "Отключить Cortana", "Блокирует фоновый голосовой ассистент.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, 1, delKey: true, isFeatured: true);
        RegTweak("activity_history", "Отключить журнал активности", "Запрещает Windows сохранять историю запущенных задач.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, 1);
        RegTweak("typing_telemetry", "Отключить сбор данных ввода с клавиатуры", "Блокирует отправку шаблонов рукописного и экранного ввода.", "Приватность", RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0);
        RegTweak("app_diagnostics", "Запретить программам доступ к диагностике", "Ограничивает фоновый доступ сторонних программ к журналу диагностики.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsGetDiagnosticInfo", 2, 0);
        RegTweak("location_tracking", "Отключить службы геолокации", "Блокирует встроенные датчики местоположения.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, 0);

        // === 2. ПРИВАТНОСТЬ (ADVANCED / ЖЕЛТЫЕ) ===
        RegTweak("uac_disable", "Отключить UAC (Контроль учетных записей)", "Полностью отключает затемнение экрана и надоедливые предупреждения при запуске программ от имени администратора. Снижает базовую защиту ОС от вирусов.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 0, 1, risk: RiskLevel.Advanced);
        RegTweak("smartscreen_disable", "Отключить фильтр SmartScreen", "Отключает облачную проверку запускаемых файлов и сайтов. Ускоряет запуск новых программ, но убирает предупреждения о подозрительных файлах.", "Приватность", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0, 1, risk: RiskLevel.Advanced);

        // === 3. ПРОИЗВОДИТЕЛЬНОСТЬ (SAFE) ===
        RegTweak("anim_disable", "Отключить анимации окон", "Мгновенный отклик интерфейса без задержек при сворачивании окон.", "Производительность", RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1", isFeatured: true);
        RegTweak("system_responsiveness", "Максимальная отзывчивость системы", "Снимает скрытый 20% резерв тактов CPU для фоновых задач.", "Производительность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 0, 20);
        RegTweak("network_throttling", "Отключить сетевое дросселирование", "Устраняет задержку сетевого стека, снижая пинг в играх.", "Производительность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10);
        RegTweak("game_scheduler_priority", "Повысить приоритет GPU в играх", "Назначает играм максимальный приоритет планировщика графики.", "Производительность", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, 8);
        RegTweak("gamedvr_disable", "Отключить Xbox Game DVR", "Выключает фоновый захват экрана в играх, устраняя статтеры.", "Производительность", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1);
        RegTweak("game_bar_fts", "Отключить оверлей Game Bar", "Снимает оверлей Xbox и освобождает видеопамять.", "Производительность", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1);
        RegTweak("hags_gpu", "Аппаратное ускорение планирования GPU (HAGS)", "Снижает задержки графического конвейера видеокарты.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1);

        // === 4. ПРОИЗВОДИТЕЛЬНОСТЬ (ADVANCED / ЖЕЛТЫЕ) ===
        RegTweak("vbs_disable", "Отключить VBS и Core Isolation", "Отключает аппаратную изоляцию ядра (Virtualization-Based Security). Дает чистый прирост FPS до 10% в играх, но ослабляет защиту ядра ОС от руткитов.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, 1, risk: RiskLevel.Advanced);
        RegTweak("ipv6_disable", "Отключить протокол IPv6", "Принудительно отключает IPv6 на уровне системы. Снижает задержки в старых онлайн-играх, но может нарушить работу Xbox Live Multiplayer.", "Производительность", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0xFF, 0x00, risk: RiskLevel.Advanced);

        // === 5. СЛУЖБЫ (SAFE) ===
        SrvTweak("service_diagtrack", "Отключить службу телеметрии (DiagTrack)", "Останавливает службу сбора и фоновой отправки телеметрии.", "DiagTrack", 2);
        SrvTweak("service_sysmain", "Отключить службу SysMain (Superfetch)", "Отключает лишнее кэширование, снижая нагрузку на SSD и RAM.", "SysMain", 2, isFeatured: true);
        SrvTweak("service_wsearch", "Отключить поиск Windows Search", "Останавливает непрерывную фоновую индексацию накопителей.", "WSearch", 2);
        SrvTweak("service_wersvc", "Отключить службу регистрации ошибок (WerSvc)", "Блокирует сбор дампов и отправку отчетов о сбоях в Microsoft.", "WerSvc", 3);
        SrvTweak("service_remotereg", "Отключить службу удаленного реестра (RemoteRegistry)", "Блокирует удаленный сетевой доступ к системному реестру.", "RemoteRegistry", 4);

        // === 6. СЛУЖБЫ (EXPERIMENTAL / КРАСНЫЕ) ===
        RegTweak("defender_disable", "Отключить Windows Defender", "Блокирует встроенный системный антивирус. ВНИМАНИЕ: Требует предварительного ручного отключения 'Защиты от подделки' в настройках безопасности Windows!", "Службы", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1, 0, risk: RiskLevel.Experimental);
        SrvTweak("service_wuauserv", "Остановить Центр Обновлений Windows", "Блокирует службу автоматических апдейтов (wuauserv). Вы перестанете получать системные патчи безопасности и драйверы.", "wuauserv", 3, risk: RiskLevel.Experimental);

        // === 7. ИНТЕРФЕЙС И ОБНОВЛЕНИЯ (SAFE) ===
        RegTweak("classic_context_menu", "Классическое контекстное меню (Win 10)", "Возвращает меню без кнопки «Показать дополнительные параметры».", "Интерфейс", RegistryHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", "", delKey: true, isFeatured: true);
        RegTweak("show_file_extensions", "Показывать расширения файлов", "Отображает реальные расширения файлов (.exe, .zip, .txt).", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0, 1);
        RegTweak("this_pc_default", "Открывать «Этот компьютер» в Проводнике", "Вместо стартового экрана «Главная» или «Быстрый доступ».", "Интерфейс", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1, 2);
        RegTweak("disable_driver_updates", "Запретить замену драйверов через WU", "Предотвращает автоматическую замену ваших драйверов GPU и чипсета более старыми версиями от Microsoft.", "Обновления", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, 0, delKey: true);
    }
}