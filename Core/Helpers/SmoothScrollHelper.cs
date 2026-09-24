using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace HeroTweaker.Core.Helpers;

public static class SmoothScrollHelper
{
    public static readonly DependencyProperty IsSmoothScrollEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsSmoothScrollEnabled",
            typeof(bool),
            typeof(SmoothScrollHelper),
            new PropertyMetadata(false, OnIsSmoothScrollEnabledChanged));

    public static bool GetIsSmoothScrollEnabled(DependencyObject obj) => (bool)obj.GetValue(IsSmoothScrollEnabledProperty);
    public static void SetIsSmoothScrollEnabled(DependencyObject obj, bool value) => obj.SetValue(IsSmoothScrollEnabledProperty, value);

    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedOffset",
            typeof(double),
            typeof(SmoothScrollHelper),
            new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private static void OnIsSmoothScrollEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
        {
            if ((bool)e.NewValue)
                sv.PreviewMouseWheel += OnPreviewMouseWheel;
            else
                sv.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static double _targetOffset = 0;

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        e.Handled = true;

        if (Math.Abs(_targetOffset - sv.VerticalOffset) > 200)
            _targetOffset = sv.VerticalOffset;

        _targetOffset = Math.Clamp(_targetOffset - (e.Delta * 0.9), 0, sv.ScrollableHeight);

        var animation = new DoubleAnimation
        {
            To = _targetOffset,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        sv.BeginAnimation(AnimatedOffsetProperty, animation);
    }
}