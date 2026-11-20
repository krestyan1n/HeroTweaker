using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HeroTweaker.Core.Services;

/// <summary>Branded, owner-modal replacement for the plain Windows message box.</summary>
public static class HeroDialog
{
    public static MessageBoxResult Show(string text) => Show(text, "HeroTweaker");
    public static MessageBoxResult Show(string text, string caption) => Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) => Show(text, caption, buttons, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image) => Show(text, caption, buttons, image, MessageBoxResult.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var owner = Application.Current?.Windows.Cast<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;
        var window = new Window
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "HeroTweaker" : caption,
            Width = 500,
            MinWidth = 360,
            MaxWidth = 640,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = owner?.Topmost ?? false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Arial"),
            FontSize = 14
        };

        var card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(22),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(17, 25, 29)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 67, 71)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 26, ShadowDepth = 8, Opacity = .48 }
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 16) };
        var badgeColor = image switch
        {
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => "#E4867D",
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => "#D8B76F",
            MessageBoxImage.Information or MessageBoxImage.Asterisk => "#75BFFF",
            _ => "#72D7C2"
        };
        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
            Background = Brush("#1B292D"), BorderBrush = Brush(badgeColor), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = image is MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop ? "!" : "H", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brush(badgeColor), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }
        };
        DockPanel.SetDock(badge, Dock.Left);
        heading.Children.Add(badge);
        var close = new Button
        {
            Content = "×", Width = 30, Height = 30, FontSize = 19, Foreground = Brush("#9CAEB2"),
            Background = Brush("#172126"), BorderBrush = Brush("#304247"), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand, IsCancel = true, Margin = new Thickness(12, 0, 0, 0)
        };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right);
        heading.Children.Add(close);
        var headingText = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        headingText.Children.Add(new TextBlock { Text = "HERO / SYSTEM", FontSize = 10, Foreground = Brush("#72D7C2"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        headingText.Children.Add(new TextBlock { Text = window.Title, FontSize = 17, Foreground = Brush("#E7EDF2"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        heading.Children.Add(headingText);
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);

        var message = new TextBlock
        {
            Text = text ?? string.Empty,
            Foreground = Brush("#B8C6CA"),
            FontSize = 14,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            Margin = new Thickness(0, 0, 0, 24)
        };
        Grid.SetRow(message, 1);
        layout.Children.Add(message);

        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        MessageBoxResult result = MessageBoxResult.None;
        var buttonResults = GetButtons(buttons);
        foreach (var buttonResult in buttonResults)
        {
            bool isDefault = buttonResult == (defaultResult == MessageBoxResult.None ? buttonResults[0] : defaultResult);
            var button = new Button
            {
                Content = ButtonLabel(buttonResult),
                MinWidth = 90,
                Height = 36,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(isDefault ? "#07110F" : "#C3CFD2"),
                Background = Brush(isDefault ? "#72D7C2" : "#1A252A"),
                BorderBrush = Brush(isDefault ? "#72D7C2" : "#34464C"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = isDefault,
                IsCancel = buttonResult is MessageBoxResult.Cancel or MessageBoxResult.No
            };
            button.Template = ButtonTemplate();
            button.Click += (_, _) => { result = buttonResult; window.DialogResult = buttonResult is not MessageBoxResult.None; window.Close(); };
            buttonsPanel.Children.Add(button);
        }
        Grid.SetRow(buttonsPanel, 2);
        layout.Children.Add(buttonsPanel);
        card.Child = layout;
        window.Content = card;
        window.Loaded += (_, _) => window.MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height * .82);
        window.ShowDialog();
        return result;
    }

    private static MessageBoxResult[] GetButtons(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
        MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
        MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
        _ => [MessageBoxResult.OK]
    };

    private static string ButtonLabel(MessageBoxResult result) => result switch
    {
        MessageBoxResult.Yes => "Да",
        MessageBoxResult.No => "Нет",
        MessageBoxResult.Cancel => "Отмена",
        _ => "Понятно"
    };

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static ControlTemplate ButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#25363B"), "ButtonBorder"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.OpacityProperty, .78, "ButtonBorder"));
        template.Triggers.Add(pressed);
        return template;
    }
}
