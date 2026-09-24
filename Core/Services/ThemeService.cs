using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using HeroTweaker.Core.Models;

namespace HeroTweaker.Core.Services;

public static class ThemeService
{
    private static readonly List<ThemeModel> Themes = new()
    {
        new()
        {
            Id = "SlateCyan",
            Name = "Slate Cyan",
            Description = "Сдержанный небесно-бирюзовый с глубоким сланцевым фоном",
            AccentColor = (Color)ColorConverter.ConvertFromString("#38BDF8"),
            GlowColor = (Color)ColorConverter.ConvertFromString("#0284C7"),
            WindowBgColor = (Color)ColorConverter.ConvertFromString("#0B0F19"),
            SidebarBgColor = (Color)ColorConverter.ConvertFromString("#070A10"),
            CardBgColor = (Color)ColorConverter.ConvertFromString("#111827"),
            CardHoverColor = (Color)ColorConverter.ConvertFromString("#1E293B"),
            BorderColor = (Color)ColorConverter.ConvertFromString("#1E293B")
        },
        new()
        {
            Id = "EmeraldSage",
            Name = "Emerald Sage",
            Description = "Мягкий пастельный изумруд, успокаивающий зрение",
            AccentColor = (Color)ColorConverter.ConvertFromString("#34D399"),
            GlowColor = (Color)ColorConverter.ConvertFromString("#059669"),
            WindowBgColor = (Color)ColorConverter.ConvertFromString("#0A110F"),
            SidebarBgColor = (Color)ColorConverter.ConvertFromString("#060B0A"),
            CardBgColor = (Color)ColorConverter.ConvertFromString("#0F1A17"),
            CardHoverColor = (Color)ColorConverter.ConvertFromString("#182A25"),
            BorderColor = (Color)ColorConverter.ConvertFromString("#1C332C")
        },
        new()
        {
            Id = "AmethystViolet",
            Name = "Amethyst",
            Description = "Глубокий лавандовый аметист с бархатным темным фоном",
            AccentColor = (Color)ColorConverter.ConvertFromString("#A78BFA"),
            GlowColor = (Color)ColorConverter.ConvertFromString("#7C3AED"),
            WindowBgColor = (Color)ColorConverter.ConvertFromString("#0E0D1A"),
            SidebarBgColor = (Color)ColorConverter.ConvertFromString("#080710"),
            CardBgColor = (Color)ColorConverter.ConvertFromString("#151326"),
            CardHoverColor = (Color)ColorConverter.ConvertFromString("#211E3B"),
            BorderColor = (Color)ColorConverter.ConvertFromString("#282447")
        },
        new()
        {
            Id = "SunsetAmber",
            Name = "Sunset Amber",
            Description = "Теплый медово-янтарный оттенок без резких бликов",
            AccentColor = (Color)ColorConverter.ConvertFromString("#FBBF24"),
            GlowColor = (Color)ColorConverter.ConvertFromString("#D97706"),
            WindowBgColor = (Color)ColorConverter.ConvertFromString("#120E0A"),
            SidebarBgColor = (Color)ColorConverter.ConvertFromString("#0B0806"),
            CardBgColor = (Color)ColorConverter.ConvertFromString("#1C1610"),
            CardHoverColor = (Color)ColorConverter.ConvertFromString("#2B2219"),
            BorderColor = (Color)ColorConverter.ConvertFromString("#33281E")
        },
        new()
        {
            Id = "NordicFrost",
            Name = "Nordic Frost",
            Description = "Прохладный нордический индиго в стиле палитры Nord",
            AccentColor = (Color)ColorConverter.ConvertFromString("#818CF8"),
            GlowColor = (Color)ColorConverter.ConvertFromString("#4F46E5"),
            WindowBgColor = (Color)ColorConverter.ConvertFromString("#0B0E17"),
            SidebarBgColor = (Color)ColorConverter.ConvertFromString("#07090F"),
            CardBgColor = (Color)ColorConverter.ConvertFromString("#121725"),
            CardHoverColor = (Color)ColorConverter.ConvertFromString("#1C2438"),
            BorderColor = (Color)ColorConverter.ConvertFromString("#222B42")
        },
        new()
        {
            Id = "CrimsonRose",
            Name = "Crimson Rose",
            Description = "Матовый пепельно-малиновый премиальный акцент",
            AccentColor = (Color)ColorConverter.ConvertFromString("#FB7185"),
            GlowColor = (Color)ColorConverter.ConvertFromString("#E11D48"),
            WindowBgColor = (Color)ColorConverter.ConvertFromString("#140A0E"),
            SidebarBgColor = (Color)ColorConverter.ConvertFromString("#0D0609"),
            CardBgColor = (Color)ColorConverter.ConvertFromString("#1F1117"),
            CardHoverColor = (Color)ColorConverter.ConvertFromString("#2E1922"),
            BorderColor = (Color)ColorConverter.ConvertFromString("#381F2A")
        },
        new()
        {
            Id = "Titanium",
            Name = "Titanium Minimal",
            Description = "Сдержанный графитовый монохром для любителей чистоты",
            AccentColor = (Color)ColorConverter.ConvertFromString("#94A3B8"),
            GlowColor = (Color)ColorConverter.ConvertFromString("#64748B"),
            WindowBgColor = (Color)ColorConverter.ConvertFromString("#0F1115"),
            SidebarBgColor = (Color)ColorConverter.ConvertFromString("#090A0D"),
            CardBgColor = (Color)ColorConverter.ConvertFromString("#171A20"),
            CardHoverColor = (Color)ColorConverter.ConvertFromString("#232731"),
            BorderColor = (Color)ColorConverter.ConvertFromString("#2B303C")
        }
    };

    public static List<ThemeModel> GetThemes()
    {
        foreach (var t in Themes) t.AccentBrush = new SolidColorBrush(t.AccentColor);
        return Themes;
    }

    public static void ApplyTheme(string themeId)
    {
        var theme = Themes.FirstOrDefault(t => t.Id.Equals(themeId, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

        if (Application.Current != null)
        {
            // 1. Обновляем глобальные ресурсы (App.xaml)
            ApplyToDictionary(Application.Current.Resources, theme);

            // 2. КРИТИЧЕСКИЙ ФИКС: Прошиваем цвета в ресурсы КАЖДОГО открытого окна.
            // XAML-свойства Window.Resources приоритетнее, поэтому мы должны переписать их напрямую.
            foreach (Window window in Application.Current.Windows)
            {
                ApplyToDictionary(window.Resources, theme);
            }
        }
    }

    private static void ApplyToDictionary(ResourceDictionary res, ThemeModel theme)
    {
        if (res == null) return;

        var accentBrush = new SolidColorBrush(theme.AccentColor);
        accentBrush.Freeze(); // Заморозка защищает от ошибок многопоточности UI-потока
        res["CyanAccentBrush"] = accentBrush;
        res["AccentBrush"] = accentBrush;

        var glowBrush = new SolidColorBrush(theme.GlowColor);
        glowBrush.Freeze();
        res["CyanGlowBrush"] = glowBrush;
        res["AccentGlowBrush"] = glowBrush;

        var windowBg = new SolidColorBrush(theme.WindowBgColor);
        windowBg.Freeze();
        res["WindowBgBrush"] = windowBg;

        var sidebarBg = new SolidColorBrush(theme.SidebarBgColor);
        sidebarBg.Freeze();
        res["SidebarBgBrush"] = sidebarBg;

        var cardBg = new SolidColorBrush(theme.CardBgColor);
        cardBg.Freeze();
        res["CardBgBrush"] = cardBg;
        res["SurfaceBgBrush"] = cardBg;

        var cardHover = new SolidColorBrush(theme.CardHoverColor);
        cardHover.Freeze();
        res["CardHoverBrush"] = cardHover;
        res["SurfaceHoverBrush"] = cardHover;

        var border = new SolidColorBrush(theme.BorderColor);
        border.Freeze();
        res["BorderBrush"] = border;

        var glass = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(50, theme.AccentColor.R, theme.AccentColor.G, theme.AccentColor.B), 0.0),
                new GradientStop(Color.FromArgb(20, theme.BorderColor.R, theme.BorderColor.G, theme.BorderColor.B), 0.5),
                new GradientStop(Color.FromArgb(35, theme.GlowColor.R, theme.GlowColor.G, theme.GlowColor.B), 1.0)
            }
        };
        glass.Freeze();
        res["GlassBorderBrush"] = glass;
    }
}