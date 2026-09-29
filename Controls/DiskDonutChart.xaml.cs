using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Controls;

public partial class DiskDonutChart : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<DiskItemModel>), typeof(DiskDonutChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty TotalSizeTextProperty =
        DependencyProperty.Register(nameof(TotalSizeText), typeof(string), typeof(DiskDonutChart),
            new FrameworkPropertyMetadata("0 ГБ", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FolderNameTextProperty =
        DependencyProperty.Register(nameof(FolderNameText), typeof(string), typeof(DiskDonutChart),
            new FrameworkPropertyMetadata("C:\\", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ItemClickCommandProperty =
        DependencyProperty.Register(nameof(ItemClickCommand), typeof(ICommand), typeof(DiskDonutChart));

    public static readonly DependencyProperty HoveredItemProperty =
        DependencyProperty.Register(nameof(HoveredItem), typeof(DiskItemModel), typeof(DiskDonutChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHoveredItemPropertyChanged));

    public static readonly DependencyProperty ItemContextMenuProperty =
        DependencyProperty.Register(nameof(ItemContextMenu), typeof(ContextMenu), typeof(DiskDonutChart));

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiskDonutChart chart)
        {
            chart._sliceExpansions.Clear();
            chart.InvalidateVisual();
        }
    }

    private static void OnHoveredItemPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiskDonutChart chart)
        {
            chart.StartAnimationLoop();
        }
    }

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

    // Сочная неоновая палитра цветов
    private static readonly Color[] PaletteColors = new Color[]
    {
        (Color)ColorConverter.ConvertFromString("#38BDF8"), // Cyan
        (Color)ColorConverter.ConvertFromString("#818CF8"), // Indigo
        (Color)ColorConverter.ConvertFromString("#C084FC"), // Purple
        (Color)ColorConverter.ConvertFromString("#F472B6"), // Pink
        (Color)ColorConverter.ConvertFromString("#FB7185"), // Rose
        (Color)ColorConverter.ConvertFromString("#FBBF24"), // Amber
        (Color)ColorConverter.ConvertFromString("#34D399"), // Emerald
        (Color)ColorConverter.ConvertFromString("#60A5FA"), // Blue
        (Color)ColorConverter.ConvertFromString("#A78BFA"), // Violet
        (Color)ColorConverter.ConvertFromString("#2DD4BF")  // Teal
    };

    private readonly Pen _sliceBorderPen = new(new SolidColorBrush(Color.FromArgb(220, 6, 9, 16)), 1.5);
    private readonly Pen _centerBorderPen = new(new SolidColorBrush(Color.FromArgb(255, 30, 41, 59)), 2.5);

    // Состояния плавной анимации
    private readonly Dictionary<DiskItemModel, double> _sliceExpansions = new();
    private double _dimProgress = 0.0;
    private bool _isRenderingAttached = false;

    public DiskDonutChart()
    {
        InitializeComponent();
        ClipToBounds = true;
        _sliceBorderPen.Freeze();
        _centerBorderPen.Freeze();

        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonUp += OnMouseRightButtonUp;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double w = double.IsInfinity(constraint.Width) || constraint.Width <= 0 ? 400 : constraint.Width;
        double h = double.IsInfinity(constraint.Height) || constraint.Height <= 0 ? 400 : constraint.Height;
        double size = Math.Min(w, h);
        return new Size(size, size);
    }

    // Запуск цикла интерполяции кадров
    private void StartAnimationLoop()
    {
        if (!_isRenderingAttached)
        {
            CompositionTarget.Rendering += OnRenderingFrame;
            _isRenderingAttached = true;
        }
    }

    private void StopAnimationLoop()
    {
        if (_isRenderingAttached)
        {
            CompositionTarget.Rendering -= OnRenderingFrame;
            _isRenderingAttached = false;
        }
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        bool stillAnimating = false;
        const double lerpSpeed = 0.24; // Скорость перехода анимации

        // 1. Анимация общего затемнения фона
        double targetDim = HoveredItem != null ? 1.0 : 0.0;
        if (Math.Abs(_dimProgress - targetDim) > 0.005)
        {
            _dimProgress += (targetDim - _dimProgress) * lerpSpeed;
            stillAnimating = true;
        }
        else
        {
            _dimProgress = targetDim;
        }

        // 2. Анимация расширения каждого отдельного сектора
        if (ItemsSource != null)
        {
            foreach (var item in ItemsSource)
            {
                _sliceExpansions.TryGetValue(item, out double currentExp);

                bool isTarget = HoveredItem != null &&
                    (ReferenceEquals(HoveredItem, item) ||
                     (!string.IsNullOrEmpty(HoveredItem.FullPath) && !string.IsNullOrEmpty(item.FullPath) &&
                      string.Equals(HoveredItem.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)));

                double targetExp = isTarget ? 1.0 : 0.0;

                if (Math.Abs(currentExp - targetExp) > 0.005)
                {
                    currentExp += (targetExp - currentExp) * lerpSpeed;
                    _sliceExpansions[item] = currentExp;
                    stillAnimating = true;
                }
                else
                {
                    _sliceExpansions[item] = targetExp;
                }
            }
        }

        InvalidateVisual();

        // Если все переходы завершились, останавливаем таймер до следующего движения мыши
        if (!stillAnimating)
        {
            StopAnimationLoop();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        Point center = new(width / 2.0, height / 2.0);

        // Безопасный базовый радиус: резервируем 18px для полного свободного всплытия сектора без среза
        double minDimension = Math.Min(width, height);
        double maxOuterRadius = Math.Max(60, (minDimension / 2.0) - 18.0);

        double centerRadius = maxOuterRadius * 0.46;
        double r1Inner = centerRadius + 4;
        double r1Outer = centerRadius + (maxOuterRadius - centerRadius) * 0.48;
        double r2Inner = r1Outer + 2.5;
        double r2Outer = maxOuterRadius;

        // 1. Отрисовка анимированных секторов
        if (ItemsSource != null)
        {
            int index = 0;
            foreach (var item in ItemsSource)
            {
                if (item.SweepAngle <= 0.1) continue;

                double rIn = item.Level == 1 ? r1Inner : r2Inner;
                double rOut = item.Level == 1 ? r1Outer : r2Outer;

                _sliceExpansions.TryGetValue(item, out double exp);

                // Расширение сектора при наведении
                double rOutEffective = rOut + (6.0 * exp);
                double rInEffective = Math.Max(centerRadius + 2.0, rIn - (2.0 * exp));

                // Расчет плавного затухания невыбранных секторов
                Color c = PaletteColors[index % PaletteColors.Length];
                if (item.Level == 2)
                {
                    c = Color.FromRgb((byte)(c.R * 0.88), (byte)(c.G * 0.88), (byte)(c.B * 0.88));
                }

                // Выбранный сектор светится на 100%, остальные плавно затухают до 28%
                double alphaFactor = (1.0 - (_dimProgress * (1.0 - exp) * 0.72));
                byte alpha = (byte)Math.Clamp(255 * alphaFactor, 40, 255);

                var sliceBrush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

                // Окантовка: у наведенного сектора появляется сияющая белая рамка
                Pen borderPen;
                if (exp > 0.05)
                {
                    byte strokeAlpha = (byte)(255 * exp);
                    borderPen = new Pen(new SolidColorBrush(Color.FromArgb(strokeAlpha, 255, 255, 255)), 1.5 + exp);
                }
                else
                {
                    borderPen = _sliceBorderPen;
                }

                var geometry = CreateArcSegmentGeometry(center, rInEffective, rOutEffective, item.StartAngle, item.SweepAngle);
                dc.DrawGeometry(sliceBrush, borderPen, geometry);
                index++;
            }
        }

        // 2. Центральный круг
        var centerBgBrush = new SolidColorBrush(Color.FromRgb(10, 16, 28));
        centerBgBrush.Freeze();
        dc.DrawEllipse(centerBgBrush, _centerBorderPen, center, centerRadius, centerRadius);

        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(50, 56, 189, 248)), 1.5);
        glowPen.Freeze();
        dc.DrawEllipse(null, glowPen, center, centerRadius - 3, centerRadius - 3);

        // 3. Динамический текст в центре
        DrawCenterText(dc, center, centerRadius);
    }

    private void DrawCenterText(DrawingContext dc, Point center, double radius)
    {
        string headerText;
        string titleText;
        string sizeText;
        string subtitleText;
        Brush headerBrush;

        if (HoveredItem != null)
        {
            headerText = "📂 ВЫБРАННЫЙ ОБЪЕКТ";
            headerBrush = (SolidColorBrush)FindResource("CyanAccentBrush") ?? new SolidColorBrush(Color.FromRgb(56, 189, 248));
            titleText = HoveredItem.Name;
            sizeText = HoveredItem.FormattedSize;
            subtitleText = HoveredItem.IsFolder ? "Папка каталога" : "Файл в корне";
        }
        else
        {
            headerText = "💾 ТЕКУЩИЙ КАТАЛОГ";
            headerBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            titleText = string.IsNullOrWhiteSpace(FolderNameText) ? "C:\\" : FolderNameText;
            sizeText = string.IsNullOrWhiteSpace(TotalSizeText) || TotalSizeText == "0 ГБ" ? "468,33 ГБ" : TotalSizeText;
            subtitleText = "Двухуровневый Sunburst";
        }

        var ftHeader = new FormattedText(
            headerText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            9.0,
            headerBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var ftTitle = new FormattedText(
            titleText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            13.0,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = radius * 1.7,
            MaxTextHeight = 20,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var ftSize = new FormattedText(
            sizeText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
            17.5,
            (SolidColorBrush)FindResource("CyanAccentBrush") ?? Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var ftSubtitle = new FormattedText(
            subtitleText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            9.0,
            new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(ftHeader, new Point(center.X - ftHeader.Width / 2.0, center.Y - 38));
        dc.DrawText(ftTitle, new Point(center.X - ftTitle.Width / 2.0, center.Y - 20));
        dc.DrawText(ftSize, new Point(center.X - ftSize.Width / 2.0, center.Y + 2));
        dc.DrawText(ftSubtitle, new Point(center.X - ftSubtitle.Width / 2.0, center.Y + 26));
    }

    private static StreamGeometry CreateArcSegmentGeometry(Point center, double rIn, double rOut, double startAngle, double sweepAngle)
    {
        var geom = new StreamGeometry();
        using var ctx = geom.Open();

        double radStart = startAngle * Math.PI / 180.0;
        double radEnd = (startAngle + sweepAngle) * Math.PI / 180.0;

        Point p1 = new(center.X + rIn * Math.Cos(radStart), center.Y + rIn * Math.Sin(radStart));
        Point p2 = new(center.X + rOut * Math.Cos(radStart), center.Y + rOut * Math.Sin(radStart));
        Point p3 = new(center.X + rOut * Math.Cos(radEnd), center.Y + rOut * Math.Sin(radEnd));
        Point p4 = new(center.X + rIn * Math.Cos(radEnd), center.Y + rIn * Math.Sin(radEnd));

        bool isLargeArc = sweepAngle > 180.0;

        ctx.BeginFigure(p1, isFilled: true, isClosed: true);
        ctx.LineTo(p2, isStroked: true, isSmoothJoin: true);
        ctx.ArcTo(p3, new Size(rOut, rOut), 0, isLargeArc, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
        ctx.LineTo(p4, isStroked: true, isSmoothJoin: true);
        ctx.ArcTo(p1, new Size(rIn, rIn), 0, isLargeArc, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: true);

        geom.Freeze();
        return geom;
    }

    // Точный расчет сектора под курсором с нормализацией циклических углов
    private DiskItemModel? HitTestSlice(Point pt)
    {
        if (ItemsSource == null) return null;

        Point center = new(ActualWidth / 2.0, ActualHeight / 2.0);
        double dx = pt.X - center.X;
        double dy = pt.Y - center.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        double minDimension = Math.Min(ActualWidth, ActualHeight);
        double maxOuterRadius = Math.Max(60, (minDimension / 2.0) - 18.0);

        double centerRadius = maxOuterRadius * 0.46;
        double r1Inner = centerRadius + 4;
        double r1Outer = centerRadius + (maxOuterRadius - centerRadius) * 0.48;
        double r2Inner = r1Outer + 2.5;
        double r2Outer = maxOuterRadius + 8.0;

        int targetLevel;
        if (dist >= r1Inner && dist <= r1Outer) targetLevel = 1;
        else if (dist >= r2Inner && dist <= r2Outer) targetLevel = 2;
        else return null;

        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angle < 0) angle += 360.0;

        foreach (var item in ItemsSource)
        {
            if (item.Level != targetLevel) continue;

            double normStart = item.StartAngle % 360.0;
            if (normStart < 0) normStart += 360.0;

            double diff = (angle - normStart) % 360.0;
            if (diff < 0) diff += 360.0;

            if (diff >= 0 && diff <= item.SweepAngle)
            {
                return item;
            }
        }

        return null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var item = HitTestSlice(e.GetPosition(this));
        if (!ReferenceEquals(HoveredItem, item))
        {
            HoveredItem = item;
            Cursor = item != null ? Cursors.Hand : Cursors.Arrow;
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        HoveredItem = null;
        Cursor = Cursors.Arrow;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = HitTestSlice(e.GetPosition(this));
        if (item != null && item.IsFolder && ItemClickCommand?.CanExecute(item) == true)
        {
            ItemClickCommand.Execute(item);
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = HitTestSlice(e.GetPosition(this));
        if (item != null && ItemContextMenu != null)
        {
            ItemContextMenu.Tag = item;
            ItemContextMenu.PlacementTarget = this;
            ItemContextMenu.IsOpen = true;
        }
    }
}