using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Controls;

public partial class DiskDonutChart : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<DiskItemModel>), typeof(DiskDonutChart),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty TotalSizeTextProperty =
        DependencyProperty.Register(nameof(TotalSizeText), typeof(string), typeof(DiskDonutChart),
            new PropertyMetadata("0 ГБ", (d, e) => ((DiskDonutChart)d).UpdateCenterDefault()));

    public static readonly DependencyProperty FolderNameTextProperty =
        DependencyProperty.Register(nameof(FolderNameText), typeof(string), typeof(DiskDonutChart),
            new PropertyMetadata("", (d, e) => ((DiskDonutChart)d).UpdateCenterDefault()));

    public static readonly DependencyProperty ItemClickCommandProperty =
        DependencyProperty.Register(nameof(ItemClickCommand), typeof(ICommand), typeof(DiskDonutChart),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HoveredItemProperty =
        DependencyProperty.Register(nameof(HoveredItem), typeof(DiskItemModel), typeof(DiskDonutChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHoveredItemChanged));

    public static readonly DependencyProperty ItemContextMenuProperty =
        DependencyProperty.Register(nameof(ItemContextMenu), typeof(ContextMenu), typeof(DiskDonutChart),
            new PropertyMetadata(null));

    public IEnumerable<DiskItemModel>? ItemsSource
    {
        get => (IEnumerable<DiskItemModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string TotalSizeText
    {
        get => (string)GetValue(TotalSizeTextProperty);
        set => SetValue(TotalSizeTextProperty, value);
    }

    public string FolderNameText
    {
        get => (string)GetValue(FolderNameTextProperty);
        set => SetValue(FolderNameTextProperty, value);
    }

    public ICommand? ItemClickCommand
    {
        get => (ICommand?)GetValue(ItemClickCommandProperty);
        set => SetValue(ItemClickCommandProperty, value);
    }

    public DiskItemModel? HoveredItem
    {
        get => (DiskItemModel?)GetValue(HoveredItemProperty);
        set => SetValue(HoveredItemProperty, value);
    }

    public ContextMenu? ItemContextMenu
    {
        get => (ContextMenu?)GetValue(ItemContextMenuProperty);
        set => SetValue(ItemContextMenuProperty, value);
    }

    private readonly Dictionary<DiskItemModel, (Path Path, TranslateTransform Transform, double TargetX, double TargetY, Color BaseColor)> _sliceMap = new();

    public DiskDonutChart()
    {
        InitializeComponent();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiskDonutChart chart)
        {
            if (e.OldValue is INotifyCollectionChanged oldColl)
                oldColl.CollectionChanged -= chart.OnCollectionChanged;

            if (e.NewValue is INotifyCollectionChanged newColl)
                newColl.CollectionChanged += chart.OnCollectionChanged;

            chart.DrawSunburst();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(DrawSunburst);
    }

    private static void OnHoveredItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiskDonutChart chart)
        {
            if (e.OldValue is DiskItemModel oldItem && chart._sliceMap.TryGetValue(oldItem, out var oldSlice))
            {
                chart.AnimatePopOut(oldSlice.Path, oldSlice.Transform, 0, 0, 1.0, false);
            }

            if (e.NewValue is DiskItemModel newItem && chart._sliceMap.TryGetValue(newItem, out var newSlice))
            {
                chart.DimOtherSlices(newItem);
                chart.AnimatePopOut(newSlice.Path, newSlice.Transform, newSlice.TargetX, newSlice.TargetY, 1.0, true);

                chart.CenterBadgeText.Text = newItem.IsFolder ? "📁 ПАПКА" : "📄 ФАЙЛ";
                chart.CenterBadgeText.Foreground = new SolidColorBrush(newSlice.BaseColor);
                chart.CenterTitleText.Text = newItem.Name;
                chart.CenterSizeText.Text = newItem.FormattedSize;
                chart.CenterSubText.Text = $"{newItem.Percentage}% объема";
            }
            else if (e.NewValue == null)
            {
                chart.ResetAllSlicesDim();
                chart.UpdateCenterDefault();
            }
        }
    }

    private void UpdateCenterDefault()
    {
        CenterBadgeText.Text = "💾 ТЕКУЩИЙ КАТАЛОГ";
        CenterBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#818CF8"));
        CenterTitleText.Text = FolderNameText;
        CenterSizeText.Text = TotalSizeText;
        CenterSubText.Text = "Двухуровневый Sunburst";
    }

    public void DrawSunburst()
    {
        ChartCanvas.Children.Clear();
        _sliceMap.Clear();
        if (ItemsSource == null) return;

        double center = 200;

        double innerR1 = 88;
        double outerR1 = 138;
        double innerR2 = 144;
        double outerR2 = 192;

        foreach (var item in ItemsSource)
        {
            if (item.SweepAngle <= 0.1) continue;

            double rIn = item.RingLevel == 1 ? innerR1 : innerR2;
            double rOut = item.RingLevel == 1 ? outerR1 : outerR2;

            var path = CreateSliceGeometry(center, center, rIn, rOut, item.StartAngle, item.SweepAngle);
            var color = (Color)ColorConverter.ConvertFromString(item.ColorHex);

            path.Fill = new SolidColorBrush(color);
            path.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0B0E"));
            path.StrokeThickness = item.RingLevel == 1 ? 2.0 : 1.2;
            path.Cursor = item.IsFolder ? Cursors.Hand : Cursors.Arrow;

            double midAngleRad = (item.StartAngle + item.SweepAngle / 2.0 - 90.0) * Math.PI / 180.0;
            double popDistance = item.RingLevel == 1 ? 8.0 : 6.0;
            double targetX = Math.Cos(midAngleRad) * popDistance;
            double targetY = Math.Sin(midAngleRad) * popDistance;

            var transform = new TranslateTransform();
            path.RenderTransform = transform;

            _sliceMap[item] = (path, transform, targetX, targetY, color);

            path.ToolTip = new ToolTip
            {
                Content = $"{(item.RingLevel == 2 ? "↳ " : "")}{item.Name}\nРазмер: {item.FormattedSize} ({item.Percentage}%)"
            };

            path.MouseRightButtonUp += (s, e) =>
            {
                if (ItemContextMenu != null)
                {
                    ItemContextMenu.PlacementTarget = path;
                    ItemContextMenu.Tag = item;
                    ItemContextMenu.IsOpen = true;
                    e.Handled = true;
                }
            };

            path.MouseEnter += (s, e) =>
            {
                HoveredItem = item;
                item.IsHovered = true;
            };

            path.MouseLeave += (s, e) =>
            {
                item.IsHovered = false;
                if (HoveredItem == item) HoveredItem = null;
            };

            path.MouseLeftButtonDown += (s, e) =>
            {
                if (item.IsFolder && ItemClickCommand != null && ItemClickCommand.CanExecute(item))
                {
                    ItemClickCommand.Execute(item);
                }
            };

            ChartCanvas.Children.Add(path);
        }
        // === МЯГКАЯ ПЛАВНАЯ АНИМАЦИЯ ПРОКРУТА ДИСКА ===
        try
        {
            ChartCanvas.RenderTransformOrigin = new Point(0.5, 0.5);
            var rotateTransform = new RotateTransform();
            ChartCanvas.RenderTransform = rotateTransform;

            var spinAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = -18,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(850),
                EasingFunction = new System.Windows.Media.Animation.QuarticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };

            var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.45,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(500)
            };

            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
            ChartCanvas.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }
        catch { }

        UpdateCenterDefault();
    }

    private void DimOtherSlices(DiskItemModel highlighted)
    {
        foreach (var kvp in _sliceMap)
        {
            if (kvp.Key != highlighted)
            {
                kvp.Value.Path.Opacity = 0.45;
            }
        }
    }

    private void ResetAllSlicesDim()
    {
        foreach (var kvp in _sliceMap)
        {
            kvp.Value.Path.Opacity = 1.0;
            kvp.Value.Path.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0B0E"));
            kvp.Value.Path.StrokeThickness = kvp.Key.RingLevel == 1 ? 2.0 : 1.2;
        }
    }

    private void AnimatePopOut(Path path, TranslateTransform transform, double toX, double toY, double targetOpacity, bool isHighlighted)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animX = new DoubleAnimation(toX, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
        var animY = new DoubleAnimation(toY, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };

        transform.BeginAnimation(TranslateTransform.XProperty, animX);
        transform.BeginAnimation(TranslateTransform.YProperty, animY);
        path.Opacity = targetOpacity;

        if (isHighlighted)
        {
            path.Stroke = Brushes.White;
            path.StrokeThickness = 2.5;
            Canvas.SetZIndex(path, 10);
        }
        else
        {
            path.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0B0E"));
            path.StrokeThickness = 1.5;
            Canvas.SetZIndex(path, 0);
        }
    }

    private Path CreateSliceGeometry(double cx, double cy, double rInner, double rOuter, double startAngle, double sweepAngle)
    {
        double a1 = (startAngle - 90) * Math.PI / 180.0;
        double a2 = (startAngle + sweepAngle - 90) * Math.PI / 180.0;

        Point p1 = new(cx + rOuter * Math.Cos(a1), cy + rOuter * Math.Sin(a1));
        Point p2 = new(cx + rOuter * Math.Cos(a2), cy + rOuter * Math.Sin(a2));
        Point p3 = new(cx + rInner * Math.Cos(a2), cy + rInner * Math.Sin(a2));
        Point p4 = new(cx + rInner * Math.Cos(a1), cy + rInner * Math.Sin(a1));

        bool isLargeArc = sweepAngle > 180.0;

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(p1, true, true);
            ctx.ArcTo(p2, new Size(rOuter, rOuter), 0, isLargeArc, SweepDirection.Clockwise, true, false);
            ctx.LineTo(p3, true, false);
            ctx.ArcTo(p4, new Size(rInner, rInner), 0, isLargeArc, SweepDirection.Counterclockwise, true, false);
        }
        geom.Freeze();

        return new Path { Data = geom };
    }
}