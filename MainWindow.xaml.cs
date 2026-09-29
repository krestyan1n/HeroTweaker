using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HeroTweaker.Core.Models;
using HeroTweaker.ViewModels;

namespace HeroTweaker;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContext = new MainViewModel();

        Loaded += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedCategory))
        {
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

    // Подсветка строки в правом списке папок
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
        if (sender is FrameworkElement el && el.ContextMenu != null && el.DataContext is DiskItemModel item)
        {
            el.ContextMenu.Tag = item;
        }
    }
}