using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HeroTweaker.Core.Models;
using HeroTweaker.Core.Services;
using HeroTweaker.ViewModels;

namespace HeroTweaker;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 1. Создаем ViewModel и привязываем контекст данных
        var vm = new MainViewModel();
        DataContext = vm;

        // 2. Применяем сохраненную тему к окну
        ThemeService.ApplyTheme(vm.SelectedThemeId);

        // 3. Безопасная автопрокрутка терминала логов
        ((INotifyCollectionChanged)vm.ConsoleLogs).CollectionChanged += (_, _) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                var scroller = FindName("ConsoleScroller") as ScrollViewer;
                scroller?.ScrollToEnd();
            });
        };
    }

    private void DriveSelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OpenRowContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DiskItemModel item)
        {
            var menu = FindResource("DiskItemContextMenu") as ContextMenu;
            if (menu != null)
            {
                menu.PlacementTarget = btn;
                menu.Tag = item;
                menu.IsOpen = true;
            }
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