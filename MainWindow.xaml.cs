using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using HeroTweaker.Core.Models;
using HeroTweaker.Core.Services;
using HeroTweaker.ViewModels;

namespace HeroTweaker;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DarkFrameColorRef = 0x00110F0B;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private static readonly string[] CategoryOrder =
    {
        "Информация", "Диск", "Драйверы", "Приложения", "Developer", "Автозагрузка", "Сеть & Пинг",
        "Приватность", "Производительность", "Службы", "Интерфейс", "Обновления", "Настройки"
    };

    private string _previousCategory = "Информация";

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => ApplyDarkWindowFrame();
        StateChanged += (_, _) => ApplyDarkWindowFrame();

        DataContext = new MainViewModel();

        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                await Dispatcher.InvokeAsync(() => PositionNavigationIndicator(false), DispatcherPriority.ContextIdle);
                await vm.StartInitializationAsync();
            }
        };
    }

    private void ApplyDarkWindowFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Windows 11 can draw a light one-pixel DWM border even for a borderless WPF window.
        // Explicit dark DWM colors keep the system frame invisible behind the custom chrome.
        var darkMode = 1;
        var frameColor = DarkFrameColorRef;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref frameColor, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref frameColor, sizeof(int));
    }

    private void WindowChrome_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsDescendantOfButton(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private static bool IsDescendantOfButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedCategory))
        {
            var currentCategory = (sender as MainViewModel)?.SelectedCategory ?? _previousCategory;
            var previousCategory = _previousCategory;
            _previousCategory = currentCategory;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                TweaksScrollViewer?.ScrollToTop();
                DashboardScrollViewer?.ScrollToTop();
                NetworkScrollViewer?.ScrollToTop();
                DeveloperScrollViewer?.ScrollToTop();
                StartupScrollViewer?.ScrollToTop();
                SettingsScrollViewer?.ScrollToTop();
                CatalogScrollViewer?.ScrollToTop();
                InstalledScrollViewer?.ScrollToTop();
                RestoreScrollViewer?.ScrollToTop();
                DiskListScrollViewer?.ScrollToTop();
                DiskCleanupScrollViewer?.ScrollToTop();
                AnimateContentChange(previousCategory, currentCategory);
                PositionNavigationIndicator(true);
            });
        }
        else if (e.PropertyName == nameof(MainViewModel.HoveredDiskItem))
        {
            if (DataContext is MainViewModel vm)
            {
                HighlightHoveredDiskRow(vm.HoveredDiskItem, vm.CurrentDiskPath);
            }
        }
    }

    private void AnimateContentChange(string previousCategory, string currentCategory)
    {
        if (ContentHost == null || ContentTransition == null) return;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var direction = GetCategoryIndex(currentCategory) >= GetCategoryIndex(previousCategory) ? 1 : -1;
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.76, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = easing
        });
        ContentTransition.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(22 * direction, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easing
        });
    }

    private static int GetCategoryIndex(string category)
    {
        var index = Array.IndexOf(CategoryOrder, category);
        return index < 0 ? 0 : index;
    }

    private void PositionNavigationIndicator(bool animate)
    {
        if (DataContext is not MainViewModel vm || TopNavigationHost == null || ActiveNavIndicator == null || ActiveNavIndicatorTransform == null)
            return;

        var target = FindName($"Nav{GetNavigationName(vm.SelectedCategory)}") as FrameworkElement;
        if (target == null || target.ActualWidth <= 0) return;

        var point = target.TransformToAncestor(TopNavigationHost).Transform(new Point(0, 0));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (!animate || ActiveNavIndicator.ActualWidth <= 0)
        {
            ActiveNavIndicator.BeginAnimation(WidthProperty, null);
            ActiveNavIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            ActiveNavIndicator.Width = target.ActualWidth;
            ActiveNavIndicatorTransform.X = point.X;
            return;
        }

        ActiveNavIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(target.ActualWidth, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easing
        });
        ActiveNavIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(point.X, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = easing
        });
    }

    private static string GetNavigationName(string category) => category switch
    {
        "Информация" => "Dashboard",
        "Диск" => "Disk",
        "Драйверы" => "Drivers",
        "Приложения" => "Apps",
        "Developer" => "Developer",
        "Автозагрузка" => "Startup",
        "Сеть & Пинг" => "Network",
        "Приватность" => "Privacy",
        "Производительность" => "Performance",
        "Службы" => "Services",
        "Интерфейс" => "Interface",
        "Обновления" => "Updates",
        "Настройки" => "Settings",
        _ => "Dashboard"
    };

    private void HighlightHoveredDiskRow(DiskItemModel? hovered, string currentRootPath)
    {
        var listControl = FindName("Level1ItemsControl") as ItemsControl
            ?? (DiskListScrollViewer != null ? FindVisualChild<ItemsControl>(DiskListScrollViewer) : null);

        if (listControl == null) return;

        for (int i = 0; i < listControl.Items.Count; i++)
        {
            var container = listControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            var border = container as Border ?? FindVisualChild<Border>(container);
            if (border == null) continue;

            var item = listControl.Items[i] as DiskItemModel;
            bool isMatch = false;

            if (hovered != null && item != null)
            {
                if (ReferenceEquals(hovered, item) ||
                    string.Equals(hovered.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    isMatch = true;
                }
                else if (item.IsFolder &&
                         !string.IsNullOrEmpty(item.FullPath) &&
                         !string.IsNullOrEmpty(hovered.FullPath) &&
                         !string.Equals(item.FullPath.TrimEnd('\\'), currentRootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    string parentPrefix = item.FullPath.TrimEnd('\\') + "\\";
                    if (hovered.FullPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = true;
                    }
                }
            }

            if (isMatch)
            {
                border.Background = new SolidColorBrush(Color.FromArgb(0x38, 0x38, 0xBD, 0xF8));
                border.BorderBrush = (Brush)FindResource("CyanAccentBrush");
                border.BringIntoView();
            }
            else
            {
                border.Background = (Brush)FindResource("CardBgBrush");
                border.BorderBrush = (Brush)FindResource("BorderBrush");
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private void FixHealthPenalty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HealthPenaltyItem item)
        {
            item.FixAction?.Invoke();
        }
    }

    private void DriveSelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && DataContext is MainViewModel vm)
        {
            var menu = new ContextMenu();
            foreach (var d in vm.AvailableDrives)
            {
                var item = new MenuItem
                {
                    Header = d.DisplayName,
                    Tag = d.RootPath
                };
                item.Click += async (s, args) =>
                {
                    if (s is MenuItem mi && mi.Tag is string root)
                    {
                        await vm.NavigateDiskToPathAsync(root);
                    }
                };
                menu.Items.Add(item);
            }
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        }
    }

    private void DiskRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is DiskItemModel item && DataContext is MainViewModel vm)
        {
            vm.HoveredDiskItem = item;
        }
    }

    private void DiskRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.HoveredDiskItem = null;
        }
    }

    private void DiskRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is DiskItemModel item)
        {
            if (el.ContextMenu != null)
            {
                el.ContextMenu.Tag = item;
            }
        }
    }

    // === ПРЯМЫЕ ОБРАБОТЧИКИ КОНТЕКСТНОГО МЕНЮ (С ВЫЗОВОМ ПЕРЕСКАНИРОВАНИЯ) ===

    private DiskItemModel? GetTargetDiskItem(object sender)
    {
        if (sender is MenuItem mi)
        {
            if (mi.Parent is ContextMenu cm && cm.Tag is DiskItemModel item)
                return IsSafeDeletionTarget(item) ? item : null;

            if (DataContext is MainViewModel vm && vm.HoveredDiskItem != null)
                return IsSafeDeletionTarget(vm.HoveredDiskItem) ? vm.HoveredDiskItem : null;
        }
        return null;
    }

    private static bool IsSafeDeletionTarget(DiskItemModel item)
    {
        if (item.IsSynthetic || string.IsNullOrWhiteSpace(item.FullPath)) return false;

        string path = item.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = (Path.GetPathRoot(item.FullPath) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var item = GetTargetDiskItem(sender);
        if (item != null && !string.IsNullOrWhiteSpace(item.FullPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.FullPath}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var item = GetTargetDiskItem(sender);
        if (item != null && !string.IsNullOrWhiteSpace(item.FullPath))
        {
            try
            {
                Clipboard.SetText(item.FullPath);
            }
            catch { }
        }
    }

    private async void DeleteDiskItem_Click(object sender, RoutedEventArgs e)
    {
        var item = GetTargetDiskItem(sender);
        if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
        {
            MessageBox.Show("Не удалось определить выбранный объект для удаления.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string targetPath = item.FullPath;
        string name = item.Name;

        var res = MessageBox.Show(
            $"Переместить в Корзину?\n\n{targetPath}",
            "Перемещение в Корзину",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        if (DataContext is not MainViewModel vm) return;

        vm.IsGlobalBusy = true;
        vm.StatusMessage = $"Перемещение в корзину: {name}...";

        try
        {
            bool success = await Task.Run(() => DiskAnalyzerService.MoveToRecycleBin(targetPath));

            if (success)
            {
                DiskCacheService.ClearAllCache();
                await vm.NavigateDiskToPathAsync(vm.CurrentDiskPath, forceRefresh: true);
                vm.RefreshAvailableDrives();
            }
            else
            {
                MessageBox.Show(
                    $"Не удалось переместить в Корзину:\n{targetPath}\n\nВозможно, файл открыт в другой программе или требуются права администратора.",
                    "Ошибка удаления",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            vm.IsGlobalBusy = false;
            vm.StatusMessage = string.Empty;
        }
    }
}
