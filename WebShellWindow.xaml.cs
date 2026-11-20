using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using HeroTweaker.Core.Models;
using Microsoft.Web.WebView2.Core;
using HeroTweaker.ViewModels;
using HeroTweaker.Core.Services;
using MessageBox = HeroTweaker.Core.Services.HeroDialog;

namespace HeroTweaker;

public partial class WebShellWindow : Window
{
    private const string VirtualHost = "herotweaker.local";
    private readonly MainViewModel _viewModel;
    private bool _webViewInitialized;
    private bool _initializationStarted;

    public WebShellWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        Title = $"HeroTweaker v{_viewModel.AppVersionDisplay}";
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            await InitializeWebViewAsync();
            var updateError = SelfUpdateService.TakePendingUpdateError();
            if (!string.IsNullOrWhiteSpace(updateError))
                MessageBox.Show(updateError, "Обновление не завершено", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        Closed += (_, _) => AppWebView.Dispose();
    }

    private string UiDirectory => Path.Combine(AppContext.BaseDirectory, "ui-dist");

    private async Task InitializeWebViewAsync()
    {
        BootstrapPanel.Visibility = Visibility.Visible;
        WebViewFailurePanel.Visibility = Visibility.Collapsed;
        AppWebView.Visibility = Visibility.Collapsed;
        try
        {
            if (!Directory.Exists(Path.Combine(UiDirectory, "assets")))
                throw new DirectoryNotFoundException($"Не найдены файлы интерфейса: {UiDirectory}");

            if (!WebView2RuntimeInstaller.TryGetInstalledVersion(out var runtimeVersion))
            {
                BootstrapStatus.Text = "Microsoft Edge WebView2 Runtime не найден. Он нужен для работы интерфейса.";
                var answer = MessageBox.Show(
                    "Для запуска нового интерфейса требуется Microsoft Edge WebView2 Runtime.\n\n" +
                    "Установить официальный Evergreen Runtime от Microsoft сейчас? Будет загружен небольшой установщик, проверена его подпись Microsoft и запущена тихая установка.",
                    "Требуется WebView2 Runtime",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
                    Close();
                    return;
                }

                var progress = new Progress<string>(text => BootstrapStatus.Text = text);
                await WebView2RuntimeInstaller.InstallEvergreenRuntimeAsync(progress);
                BootstrapStatus.Text = "WebView2 Runtime установлен. Запускаю интерфейс…";
            }
            else
            {
                BootstrapStatus.Text = $"WebView2 Runtime {runtimeVersion} найден. Запускаю интерфейс…";
            }

            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeroTweaker", "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await AppWebView.EnsureCoreWebView2Async(environment);

            var core = AppWebView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(VirtualHost, UiDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
                    !string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            core.WebMessageReceived += OnWebMessageReceived;
            _webViewInitialized = true;
            core.Navigate($"https://{VirtualHost}/index.html");
            AppWebView.Visibility = Visibility.Visible;
            BootstrapPanel.Visibility = Visibility.Collapsed;

            if (!_initializationStarted)
            {
                _initializationStarted = true;
                _ = _viewModel.StartInitializationAsync();
            }
        }
        catch (Exception ex)
        {
            AppWebView.Visibility = Visibility.Collapsed;
            BootstrapPanel.Visibility = Visibility.Collapsed;
            WebViewFailureMessage.Text = ex.Message + Environment.NewLine + Environment.NewLine +
                "Проверь подключение к интернету и повтори запуск. Если Runtime не установлен, используй кнопку загрузки Microsoft ниже.";
            WebViewFailurePanel.Visibility = Visibility.Visible;
        }
    }

    private async void RetryWebView_Click(object sender, RoutedEventArgs e) => await InitializeWebViewAsync();

    private void DownloadWebView2_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            var type = ReadString(root, "type");
            switch (type)
            {
                case "ready":
                case "snapshot":
                    SendSnapshot();
                    break;
                case "navigate":
                    var category = ReadString(root, "category");
                    if (IsKnownCategory(category))
                    {
                        _viewModel.SelectedCategory = category;
                        SendSnapshot();
                    }
                    break;
                case "window":
                    HandleWindowAction(ReadString(root, "action"));
                    break;
                case "windowDrag":
                    BeginNativeWindowDrag();
                    break;
                case "action":
                    RunAction(ReadString(root, "command"));
                    break;
                case "selectDrive":
                    SelectDrive(ReadString(root, "path"));
                    break;
                case "diskDrill":
                    DrillDisk(ReadString(root, "path"));
                    break;
                case "diskNavigate":
                    NavigateDisk(ReadString(root, "path"));
                    break;
                case "diskUp":
                    Execute(_viewModel.GoUpDiskCommand);
                    break;
                case "diskRefresh":
                    Execute(_viewModel.RefreshDiskCommand);
                    break;
                case "diskAction":
                    RunDiskItemAction(ReadString(root, "action"), ReadString(root, "path"));
                    break;
                case "diskTab":
                    _viewModel.DiskSubTabIndex = Math.Clamp((int)ReadDouble(root, "index"), 0, 1);
                    SendSnapshot();
                    break;
                case "cleanupTarget":
                    SetCleanupTarget(ReadString(root, "id"), ReadBool(root, "value"));
                    break;
                case "gpuControl":
                    SetGpuControl(ReadString(root, "control"), ReadDouble(root, "value"));
                    break;
                case "gpuAction":
                    await RunGpuActionAsync(ReadString(root, "command"));
                    break;
                case "gpuProfileRemove":
                    RemoveGpuProfile(ReadString(root, "path"));
                    break;
                case "gpuSystemSetting":
                    var setting = ReadString(root, "target");
                    if (_viewModel.OpenGpuSystemSettingsCommand.CanExecute(setting))
                        _viewModel.OpenGpuSystemSettingsCommand.Execute(setting);
                    break;
                case "tweakSearch":
                    _viewModel.SearchText = ReadString(root, "value");
                    SendSnapshot();
                    break;
                case "tweakTarget":
                    SetTweakTarget(ReadString(root, "id"), ReadBool(root, "value"));
                    break;
                case "tweakExpand":
                    ToggleTweakExpansion(ReadString(root, "id"));
                    break;
                case "tweakAction":
                    RunTweakAction(ReadString(root, "command"));
                    break;
                case "selectAdapter":
                    SelectAdapter(ReadString(root, "id"));
                    break;
                case "applyDns":
                    ApplyDns(ReadString(root, "id"));
                    break;
                case "networkAction":
                    RunNetworkAction(ReadString(root, "command"));
                    break;
                case "benchmarkCapture":
                    CaptureBenchmark(ReadString(root, "kind"));
                    break;
                case "benchmarkDuration":
                    _viewModel.BenchmarkDurationSeconds = (int)Math.Clamp(ReadDouble(root, "value"), 15, 600);
                    break;
                case "benchmarkImport":
                    var importKind = ReadString(root, "kind");
                    if (importKind is "baseline" or "after")
                        _viewModel.ImportBenchmarkCsvCommand.Execute(importKind);
                    break;
                case "appsTab":
                    _viewModel.AppsSubTabIndex = Math.Clamp((int)ReadDouble(root, "index"), 0, 2);
                    SendSnapshot();
                    break;
                case "appsSearch":
                    _viewModel.AppsSearchText = ReadString(root, "value");
                    SendSnapshot();
                    break;
                case "appTarget":
                    SetAppTarget(ReadString(root, "kind"), ReadString(root, "id"), ReadBool(root, "value"));
                    break;
                case "startupTarget":
                    SetStartupTarget(ReadString(root, "id"), ReadBool(root, "value"));
                    break;
                case "themeSelect":
                    SelectTheme(ReadString(root, "id"));
                    break;
                case "inspectInput":
                    _viewModel.InspectCommandInput = ReadString(root, "value");
                    SendSnapshot();
                    break;
                case "moduleAction":
                    RunModuleAction(ReadString(root, "command"), ReadString(root, "id"));
                    break;
            }
        }
        catch (JsonException) { }
        catch (Exception ex) { SendToast($"Действие не выполнено: {ex.Message}"); }
    }

    private void SendSnapshot()
    {
        if (!_webViewInitialized) return;
        var info = _viewModel.SysInfo;
        var adapter = _viewModel.GpuAdapters.FirstOrDefault();
        var systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var systemDrive = _viewModel.AvailableDrives.FirstOrDefault(d =>
            string.Equals(d.RootPath, systemDriveRoot, StringComparison.OrdinalIgnoreCase));
        var snapshot = new
        {
            version = _viewModel.AppVersionDisplay,
            selectedCategory = _viewModel.SelectedCategory,
            categorySubtitle = _viewModel.CategorySubtitle,
            health = _viewModel.SystemHealthScore,
            healthText = _viewModel.SystemHealthStatusText,
            cpu = _viewModel.RealTimeCpuUsage,
            ram = _viewModel.RealTimeRamUsage,
            ramDetails = _viewModel.RamDetailsFormatted,
            cpuHistory = _viewModel.CpuHistory,
            ramHistory = _viewModel.RamHistory,
            cpuName = info?.CpuName ?? "Определяется…",
            osName = info?.OsName ?? "Windows",
            osBuild = info?.OsBuild ?? string.Empty,
            osArchitecture = info?.OsArchitecture ?? (Environment.Is64BitOperatingSystem ? "x64" : "x86"),
            threads = info?.CpuThreads ?? 0,
            totalRam = info?.TotalRamFormatted ?? "RAM —",
            systemManufacturer = info?.SystemManufacturer ?? "Производитель не определён",
            systemModel = info?.SystemModel ?? "Модель не определена",
            motherboard = info?.Motherboard ?? "Материнская плата не определена",
            biosVersion = info?.BiosVersion ?? "BIOS не определён",
            gpuName = adapter?.Name ?? info?.GpuName ?? "Адаптер не определён",
            gpuVendor = adapter?.VendorName ?? (_viewModel.HasAmdGpu ? "AMD" : _viewModel.HasNvidiaGpu ? "NVIDIA" : string.Empty),
            gpuType = adapter?.AdapterKind ?? "Тип не определён",
            gpuMemory = adapter?.DedicatedMemoryFormatted ?? "VRAM не определена",
            gpuDriverVersion = adapter?.DriverVersion ?? "Драйвер не определён",
            systemDrive = systemDrive == null ? null : new
            {
                name = systemDrive.DisplayName,
                rootPath = systemDrive.RootPath,
                total = systemDrive.TotalSize,
                free = systemDrive.AvailableFreeSpace,
            },
            activeTweaks = _viewModel.ActiveTweaksCount,
            disabledServices = _viewModel.DisabledServicesCount,
            drives = _viewModel.AvailableDrives.Select(d => new
            {
                rootPath = d.RootPath,
                displayName = d.DisplayName,
                totalSize = d.TotalSize,
                availableFreeSpace = d.AvailableFreeSpace,
                usedPercentage = d.UsedPercentage,
            }).ToArray(),
            disk = _viewModel.SelectedCategory == "Диск" ? new
            {
                path = _viewModel.CurrentDiskPath,
                tab = _viewModel.DiskSubTabIndex,
                title = _viewModel.CurrentDiskFolderName,
                total = _viewModel.CurrentDiskTotalFormatted,
                isScanning = _viewModel.IsDiskScanning,
                canGoUp = _viewModel.CanGoUpDisk,
                slices = _viewModel.AllSunburstSlices.Select(item => new
                {
                    name = item.Name,
                    path = item.FullPath,
                    size = item.FormattedSize,
                    bytes = item.SizeBytes,
                    isFolder = item.IsFolder,
                    level = item.Level,
                    color = item.ColorHex,
                    startAngle = item.StartAngle,
                    sweepAngle = item.SweepAngle,
                    percent = item.Percentage,
                }).ToArray(),
                items = _viewModel.Level1ListItems.Select(item => new
                {
                    name = item.Name,
                    path = item.FullPath,
                    size = item.FormattedSize,
                    bytes = item.SizeBytes,
                    isFolder = item.IsFolder,
                    isSynthetic = item.IsSynthetic,
                    percent = item.Percentage,
                }).ToArray(),
                breadcrumbs = _viewModel.Breadcrumbs.Select(item => new { name = item.Title, path = item.FullPath }).ToArray(),
                cleanup = new
                {
                    isScanning = _viewModel.IsScanningCleanable,
                    total = _viewModel.TotalCleanableFormatted,
                    categories = _viewModel.CleanupCategories.Select(item => new
                    {
                        id = item.Id,
                        title = item.Title,
                        description = item.Description,
                        risk = item.Risk.ToString(),
                        size = item.FormattedSize,
                        bytes = item.SizeBytes,
                        files = item.FilesCount,
                        isSelected = item.IsSelected,
                    }).ToArray(),
                },
            } : null,
            gpu = _viewModel.IsGpuAuditVisible ? new
            {
                status = _viewModel.GpuAuditStatus,
                adapters = _viewModel.GpuAdapters.Select(item => new
                {
                    name = item.Name,
                    vendor = item.VendorName,
                    driver = item.DriverVersion,
                    driverDate = item.DriverDate,
                    memory = item.DedicatedMemoryFormatted,
                    type = item.AdapterKind,
                }).ToArray(),
                checks = _viewModel.GpuAuditChecks.Concat(_viewModel.GpuWindowsChecks).Select(item => new
                {
                    title = item.Title,
                    value = item.Value,
                    details = item.Details,
                    severity = item.Severity.ToString(),
                }).ToArray(),
                tuningAvailable = _viewModel.IsAmdGpuTuningAvailable,
                tuningBusy = _viewModel.IsAmdGpuTuningBusy,
                tuningStatus = _viewModel.AmdGpuTuningStatus,
                minFrequency = _viewModel.AmdGpuMinFrequency,
                maxFrequency = _viewModel.AmdGpuMaxFrequency,
                minFrequencyLimit = _viewModel.AmdGpuMinFrequencyLimit,
                maxFrequencyLimit = _viewModel.AmdGpuMaxFrequencyLimit,
                powerLimitAvailable = _viewModel.IsAmdPowerLimitAvailable,
                powerLimit = _viewModel.AmdPowerLimit,
                powerLimitMin = _viewModel.AmdPowerLimitMin,
                powerLimitMax = _viewModel.AmdPowerLimitMax,
                powerStatus = _viewModel.AmdPowerLimitStatus,
                vramFrequencyAvailable = _viewModel.IsAmdVramFrequencyAvailable,
                vramFrequency = _viewModel.AmdVramFrequency,
                vramFrequencyMin = _viewModel.AmdVramFrequencyMin,
                vramFrequencyMax = _viewModel.AmdVramFrequencyMax,
                vramStatus = _viewModel.AmdVramStatus,
                profiles = _viewModel.GpuGameProfiles.Select(item => new { path = item.ExecutablePath, name = item.ExecutableName, preference = item.PreferenceText }).ToArray(),
            } : null,
            tweaks = _viewModel.IsTweaksVisible ? _viewModel.FilteredTweaks.Cast<TweakViewModel>().Select(item => (object)new
            {
                id = item.Id,
                name = item.Name,
                description = item.Description,
                category = item.Category,
                currentState = item.CurrentState,
                targetState = item.TargetState,
                hasPending = item.HasPendingChange,
                risk = item.RiskText,
                userWhy = item.UserWhy,
                technicalDetails = item.TechDetails,
                reboot = item.RebootText,
                expanded = item.IsExpanded,
            }).ToArray() : Array.Empty<object>(),
            applications = _viewModel.SelectedCategory == "Приложения" ? new
            {
                tab = _viewModel.AppsSubTabIndex,
                search = _viewModel.AppsSearchText,
                catalog = _viewModel.CatalogApps.Select(item => new
                {
                    id = item.WingetId,
                    name = item.Name,
                    description = item.Description,
                    category = item.Category,
                    isInstalling = item.IsInstalling,
                }).ToArray(),
                installed = _viewModel.FilteredInstalledPrograms.Cast<InstalledProgramModel>().Select(item => new
                {
                    id = item.RegistryKeyPath,
                    name = item.DisplayName,
                    version = item.DisplayVersion,
                    publisher = item.Publisher,
                    installDate = item.InstallDate,
                    size = item.EstimatedSize,
                    isSelected = item.IsSelected,
                    removalStatus = item.RemovalStatus,
                    supportsQuietUninstall = item.SupportsQuietUninstall,
                }).ToArray(),
                leftovers = _viewModel.AppLeftovers.Select(item => new
                {
                    id = item.Path,
                    program = item.ProgramName,
                    path = item.Path,
                    kind = item.Kind,
                    isSelected = item.IsSelected,
                }).ToArray(),
                selectedUninstallCount = _viewModel.SelectedUninstallCount,
                selectedLeftoverCount = _viewModel.SelectedLeftoverCount,
                hasLeftovers = _viewModel.HasAppLeftovers,
            } : null,
            startup = _viewModel.SelectedCategory == "Автозагрузка" ? new
            {
                isScanning = _viewModel.IsStartupScanning,
                count = _viewModel.StartupCount,
                highImpactCount = _viewModel.HighImpactStartupCount,
                items = _viewModel.StartupItems.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    command = item.Command,
                    publisher = item.Publisher,
                    location = item.Location,
                    isEnabled = item.IsEnabled,
                    impact = item.ImpactText,
                }).ToArray(),
            } : null,
            drivers = _viewModel.SelectedCategory == "Драйверы" ? new
            {
                isRunning = _viewModel.IsConsoleRunning,
                progress = _viewModel.DriverProgress,
                hasUpdates = _viewModel.HasDriverUpdates,
                status = _viewModel.FoundDriversStatusText,
                logs = _viewModel.ConsoleLogs.TakeLast(120).Select(item => new
                {
                    time = item.FormattedTime,
                    prefix = item.Prefix,
                    message = item.Message,
                    level = item.Level.ToString(),
                }).ToArray(),
            } : null,
            developer = _viewModel.SelectedCategory == "Developer" ? new
            {
                isScanning = _viewModel.IsDevScanning,
                brokenPaths = _viewModel.DevBrokenPathsCount,
                components = _viewModel.DevComponents.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    version = item.Version,
                    path = item.ExecutablePath,
                    details = item.Details,
                    status = item.StatusText,
                    statusCode = item.Status.ToString(),
                }).ToArray(),
                paths = _viewModel.PathEntries.Select(item => new
                {
                    path = item.Path,
                    scope = item.TypeText,
                    exists = item.ExistsOnDisk,
                    duplicate = item.IsDuplicate,
                    status = item.StatusText,
                }).ToArray(),
                inspectInput = _viewModel.InspectCommandInput,
                inspectResult = _viewModel.InspectResult == null ? null : new
                {
                    command = _viewModel.InspectResult.Command,
                    found = _viewModel.InspectResult.Found,
                    path = _viewModel.InspectResult.ResolvedExecutablePath,
                    version = _viewModel.InspectResult.VersionOutput,
                    matches = _viewModel.InspectResult.AllMatchingPaths,
                },
            } : null,
            settings = _viewModel.SelectedCategory == "Настройки" ? new
            {
                selectedThemeId = _viewModel.SelectedThemeId,
                themes = _viewModel.AvailableThemes.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    description = item.Description,
                    color = $"#{item.AccentColor.R:X2}{item.AccentColor.G:X2}{item.AccentColor.B:X2}",
                    glowColor = $"#{item.GlowColor.R:X2}{item.GlowColor.G:X2}{item.GlowColor.B:X2}",
                    windowBgColor = $"#{item.WindowBgColor.R:X2}{item.WindowBgColor.G:X2}{item.WindowBgColor.B:X2}",
                    sidebarBgColor = $"#{item.SidebarBgColor.R:X2}{item.SidebarBgColor.G:X2}{item.SidebarBgColor.B:X2}",
                    cardBgColor = $"#{item.CardBgColor.R:X2}{item.CardBgColor.G:X2}{item.CardBgColor.B:X2}",
                    cardHoverColor = $"#{item.CardHoverColor.R:X2}{item.CardHoverColor.G:X2}{item.CardHoverColor.B:X2}",
                    borderColor = $"#{item.BorderColor.R:X2}{item.BorderColor.G:X2}{item.BorderColor.B:X2}",
                }).ToArray(),
                updateStatus = _viewModel.UpdateStatus,
                updateActionText = _viewModel.UpdateActionText,
                updateProgress = _viewModel.UpdateProgress,
                isUpdateChecking = _viewModel.IsUpdateChecking,
                isUpdateInstalling = _viewModel.IsUpdateInstalling,
                hasUpdateAvailable = _viewModel.HasUpdateAvailable,
                canInstallUpdate = _viewModel.CanInstallUpdate,
                history = _viewModel.AuditHistory.Take(80).Select(item => new
                {
                    id = item.Id,
                    time = item.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                    name = item.TweakName,
                    category = item.Category,
                    details = item.Details,
                    isApplied = item.IsApplied,
                    isRolledBack = item.IsRolledBack,
                }).ToArray(),
                diskCache = _viewModel.DiskCacheFormatted,
            } : null,
            pendingCount = _viewModel.PendingCount,
            hasPendingChanges = _viewModel.HasPendingChanges,
            isGlobalBusy = _viewModel.IsGlobalBusy,
            searchText = _viewModel.SearchText,
            network = _viewModel.SelectedCategory == "Сеть & Пинг" ? new
            {
                adapters = _viewModel.AvailableAdapters.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    description = item.Description,
                    ip = item.IpAddress,
                    dns = item.CurrentDns,
                    isUp = item.IsUp,
                    isDefault = item.IsDefaultRoute,
                }).ToArray(),
                selectedAdapterId = _viewModel.SelectedAdapter?.Id,
                selectedAdapterName = _viewModel.SelectedAdapter?.Name,
                isBenchmarking = _viewModel.IsDnsBenchmarking,
                isNagleEnabled = _viewModel.IsNagleEnabled,
                dnsServers = _viewModel.DnsServers.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    address = item.PrimaryIp,
                    secondaryAddress = item.SecondaryIp,
                    description = item.Description,
                    ping = item.PingMs,
                    pingText = item.PingText,
                    isCurrent = item.IsCurrent,
                    isFastest = item.IsFastest,
                }).ToArray(),
            } : null,
            benchmark = _viewModel.SelectedCategory == "Бенчмарк" ? new
            {
                status = _viewModel.BenchmarkStatus,
                isCapturing = _viewModel.IsBenchmarkCapturing,
                duration = _viewModel.BenchmarkDurationSeconds,
                hasComparison = _viewModel.HasBenchmarkComparison,
                averageDelta = _viewModel.BenchmarkAverageFpsDelta,
                telemetryComparison = _viewModel.BenchmarkTelemetryComparisonText,
                baseline = _viewModel.BenchmarkBaseline,
                after = _viewModel.BenchmarkAfter,
            } : null,
            driveStatus = _viewModel.DriveInventoryStatus,
            status = _viewModel.StatusMessage,
            systemReady = !_viewModel.IsAppLoading,
        };
        AppWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "snapshot", payload = snapshot }));
    }

    private void SendToast(string message)
    {
        if (_webViewInitialized)
            AppWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "toast", message }));
    }

    private void RunAction(string command)
    {
        switch (command)
        {
            case "analyzeSystem": _ = _viewModel.RecalculateHealthScoreAsync(); break;
            case "applyRecommendations": Execute(_viewModel.ApplyAllRecommendationsCommand); break;
            case "createRestorePoint": Execute(_viewModel.CreateRestorePointCommand); break;
            case "cleanTemp": Execute(_viewModel.CleanTempCommand); break;
            case "cleanTempAndRam": Execute(_viewModel.CleanTempAndRamCommand); break;
            case "sfcScan": Execute(_viewModel.RunSfcScanCommand); break;
            case "dismRepair": Execute(_viewModel.RunDismRestoreCommand); break;
            case "help": SendToast("Выбери раздел слева или нажми Ctrl+K для поиска."); break;
        }
    }

    private void SelectDrive(string path)
    {
        var drive = _viewModel.AvailableDrives.FirstOrDefault(d => string.Equals(d.RootPath, path, StringComparison.OrdinalIgnoreCase));
        if (drive != null && _viewModel.SelectDriveCommand.CanExecute(path))
            _viewModel.SelectDriveCommand.Execute(path);
    }

    private void DrillDisk(string path)
    {
        var item = _viewModel.Level1ListItems.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item != null && _viewModel.DrillDownDiskCommand.CanExecute(item))
            _viewModel.DrillDownDiskCommand.Execute(item);
    }

    private void RunDiskItemAction(string action, string path)
    {
        var item = _viewModel.Level1ListItems.FirstOrDefault(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;

        var command = action switch
        {
            "open" => _viewModel.OpenItemInExplorerCommand,
            "copy" => _viewModel.CopyPathCommand,
            "delete" => _viewModel.DeleteDiskItemCommand,
            _ => null,
        };
        if (command?.CanExecute(item) == true)
            command.Execute(item);
    }

    private void SetCleanupTarget(string id, bool value)
    {
        var category = _viewModel.CleanupCategories.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (category != null && !_viewModel.IsScanningCleanable)
        {
            category.IsSelected = value;
            SendSnapshot();
        }
    }

    private async void NavigateDisk(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (_viewModel.Breadcrumbs.Any(b => string.Equals(b.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) && Directory.Exists(fullPath))
                await _viewModel.NavigateDiskToPathAsync(fullPath);
        }
        catch (Exception ex) { SendToast($"Не удалось открыть папку: {ex.Message}"); }
    }

    private void SetGpuControl(string control, double value)
    {
        if (!double.IsFinite(value)) return;
        switch (control)
        {
            case "minFrequency": _viewModel.AmdGpuMinFrequency = Math.Clamp(value, _viewModel.AmdGpuMinFrequencyLimit, _viewModel.AmdGpuMaxFrequencyLimit); break;
            case "maxFrequency": _viewModel.AmdGpuMaxFrequency = Math.Clamp(value, _viewModel.AmdGpuMinFrequencyLimit, _viewModel.AmdGpuMaxFrequencyLimit); break;
            case "powerLimit": _viewModel.AmdPowerLimit = Math.Clamp(value, _viewModel.AmdPowerLimitMin, _viewModel.AmdPowerLimitMax); break;
            case "vramFrequency": _viewModel.AmdVramFrequency = Math.Clamp(value, _viewModel.AmdVramFrequencyMin, _viewModel.AmdVramFrequencyMax); break;
        }
    }

    private void RunGpuAction(string command)
    {
        switch (command)
        {
            case "refreshAudit": Execute(_viewModel.RefreshGpuAuditCommand); break;
            case "applyCore": Execute(_viewModel.ApplyAmdGpuTuningCommand); break;
            case "applyPower": Execute(_viewModel.ApplyAmdPowerLimitCommand); break;
            case "applyVram": Execute(_viewModel.ApplyAmdVramFrequencyCommand); break;
            case "reset": Execute(_viewModel.ResetAmdGpuTuningCommand); break;
            case "addProfile": Execute(_viewModel.AddHighPerformanceGameProfileCommand); break;
        }
    }

    private async Task RunGpuActionAsync(string command)
    {
        if (command == "refreshTuning")
        {
            var refreshTask = _viewModel.RefreshAmdGpuTuningAsync();
            SendSnapshot();
            await refreshTask;
            SendSnapshot();
            return;
        }

        RunGpuAction(command);
        SendSnapshot();
    }

    private void RemoveGpuProfile(string path)
    {
        var profile = _viewModel.GpuGameProfiles.FirstOrDefault(item => string.Equals(item.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        if (profile != null && _viewModel.RemoveGpuGameProfileCommand.CanExecute(profile))
            _viewModel.RemoveGpuGameProfileCommand.Execute(profile);
        SendSnapshot();
    }

    private void SetTweakTarget(string id, bool value)
    {
        var tweak = _viewModel.FilteredTweaks.Cast<TweakViewModel>().FirstOrDefault(item => item.Id == id);
        if (tweak != null && !_viewModel.IsGlobalBusy)
        {
            tweak.TargetState = value;
            SendSnapshot();
        }
    }

    private void SetAppTarget(string kind, string id, bool value)
    {
        if (kind == "installed")
        {
            var program = _viewModel.InstalledPrograms.FirstOrDefault(item => string.Equals(item.RegistryKeyPath, id, StringComparison.OrdinalIgnoreCase));
            if (program != null) program.IsSelected = value;
        }
        else if (kind == "leftover")
        {
            var leftover = _viewModel.AppLeftovers.FirstOrDefault(item => string.Equals(item.Path, id, StringComparison.OrdinalIgnoreCase));
            if (leftover != null) leftover.IsSelected = value;
        }

        SendSnapshot();
    }

    private void SetStartupTarget(string id, bool value)
    {
        var item = _viewModel.StartupItems.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item == null || _viewModel.IsStartupScanning) return;
        item.IsEnabled = value;
        if (_viewModel.ToggleStartupItemCommand.CanExecute(item))
            _viewModel.ToggleStartupItemCommand.Execute(item);
        SendSnapshot();
    }

    private void SelectTheme(string id)
    {
        if (_viewModel.AvailableThemes.Any(theme => string.Equals(theme.Id, id, StringComparison.Ordinal)))
            _viewModel.SelectedThemeId = id;
        SendSnapshot();
    }

    private void RunModuleAction(string command, string id)
    {
        switch (command)
        {
            case "refreshApps": Execute(_viewModel.RefreshInstalledProgramsCommand); break;
            case "installCatalogApp":
                var app = _viewModel.CatalogApps.FirstOrDefault(item => item.WingetId == id);
                if (app != null && _viewModel.InstallCatalogAppCommand.CanExecute(app)) _viewModel.InstallCatalogAppCommand.Execute(app);
                break;
            case "uninstallApps": Execute(_viewModel.UninstallSelectedProgramsCommand); break;
            case "scanLeftovers": Execute(_viewModel.ScanAppLeftoversCommand); break;
            case "removeLeftovers": Execute(_viewModel.RemoveSelectedLeftoversCommand); break;
            case "restoreStore": Execute(_viewModel.RestoreStoreCommand); break;
            case "restoreUwp": Execute(_viewModel.RestoreAllUwpCommand); break;
            case "scanStartup": Execute(_viewModel.ScanStartupCommand); break;
            case "scanCleanup": Execute(_viewModel.ScanCleanupCategoriesCommand); break;
            case "cleanSelectedCategories": Execute(_viewModel.ExecuteCleanDiskCategoriesCommand); break;
            case "chooseDiskFolder": Execute(_viewModel.ChooseDiskFolderCommand); break;
            case "driverSearch": Execute(_viewModel.SearchDriverUpdatesCommand); break;
            case "driverBackup": Execute(_viewModel.BackupDriversCommand); break;
            case "driverRestore": Execute(_viewModel.RestoreDriversCommand); break;
            case "installVCRuntimes": Execute(_viewModel.InstallVCRuntimesCommand); break;
            case "rescanPnp": Execute(_viewModel.RescanPnpDevicesCommand); break;
            case "installDriverFolder": Execute(_viewModel.InstallDriverFromFolderCommand); break;
            case "scanDeveloper": Execute(_viewModel.ScanDeveloperDoctorCommand); break;
            case "cleanBrokenPaths": Execute(_viewModel.CleanBrokenPathsCommand); break;
            case "inspectCommand": Execute(_viewModel.InspectCommandOwnerCommand); break;
            case "checkUpdates": Execute(_viewModel.CheckForUpdatesCommand); break;
            case "installUpdate": Execute(_viewModel.InstallUpdateCommand); break;
            case "openRelease": Execute(_viewModel.OpenUpdateUrlCommand); break;
            case "installDriverUpdates": Execute(_viewModel.InstallDriverUpdatesCommand); break;
            case "restorePoint": Execute(_viewModel.CreateRestorePointCommand); break;
            case "cleanDiskCache": Execute(_viewModel.ClearDiskCacheCommand); break;
            case "rollback":
                if (Guid.TryParse(id, out var transactionId)) _viewModel.RollbackTransactionCommand.Execute(transactionId);
                break;
        }
    }

    private void ToggleTweakExpansion(string id)
    {
        var tweak = _viewModel.FilteredTweaks.Cast<TweakViewModel>().FirstOrDefault(item => item.Id == id);
        if (tweak != null)
        {
            tweak.IsExpanded = !tweak.IsExpanded;
            SendSnapshot();
        }
    }

    private void RunTweakAction(string command)
    {
        switch (command)
        {
            case "apply": Execute(_viewModel.ApplyAllPendingCommand); break;
            case "discard": Execute(_viewModel.DiscardAllPendingCommand); break;
        }
    }

    private void SelectAdapter(string id)
    {
        _viewModel.SelectedAdapter = _viewModel.AvailableAdapters.FirstOrDefault(adapter => adapter.Id == id);
        SendSnapshot();
    }

    private void ApplyDns(string id)
    {
        var dns = _viewModel.DnsServers.FirstOrDefault(server => server.Id == id);
        if (dns != null && _viewModel.ApplyDnsServerCommand.CanExecute(dns))
            _viewModel.ApplyDnsServerCommand.Execute(dns);
    }

    private void RunNetworkAction(string command)
    {
        switch (command)
        {
            case "refreshAdapters": Execute(_viewModel.RefreshAdaptersCommand); break;
            case "dnsBenchmark": Execute(_viewModel.RunDnsBenchmarkCommand); break;
            case "resetDns": Execute(_viewModel.ResetDnsToDhcpCommand); break;
            case "flushDns": Execute(_viewModel.FlushDnsCacheCommand); break;
            case "resetWinsock": Execute(_viewModel.ResetWinsockCommand); break;
            case "toggleNagle": Execute(_viewModel.ToggleNagleCommand); break;
            case "hardResetStack": Execute(_viewModel.HardResetNetworkStackCommand); break;
            case "rescanPnp": Execute(_viewModel.RescanPnpDevicesCommand); break;
            case "installDriverFolder": Execute(_viewModel.InstallDriverFromFolderCommand); break;
            case "installLoopback": Execute(_viewModel.InstallLoopbackAdapterCommand); break;
            case "rndisHelp": Execute(_viewModel.ShowRndisHelpCommand); break;
        }
    }

    private void CaptureBenchmark(string kind)
    {
        if ((kind is "baseline" or "after") && _viewModel.CaptureBenchmarkCommand.CanExecute(kind))
            _viewModel.CaptureBenchmarkCommand.Execute(kind);
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void HandleWindowAction(string action)
    {
        switch (action)
        {
            case "minimize": WindowState = WindowState.Minimized; break;
            case "maximize": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
            case "close": Close(); break;
        }
    }

    private void BeginNativeWindowDrag()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    private const int WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static bool IsKnownCategory(string category) => category is
        "Информация" or "Диск" or "Драйверы" or "AMD GPU" or "NVIDIA GPU" or "Приложения" or
        "Developer" or "Автозагрузка" or "Сеть & Пинг" or "Приватность" or "Производительность" or
        "Службы" or "Интерфейс" or "Обновления" or "Настройки" or "Бенчмарк";

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : double.NaN;

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True;
}
