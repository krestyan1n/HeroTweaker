using System.Windows.Media;

namespace HeroTweaker.Core.Models;

public class ThemeModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SolidColorBrush AccentBrush { get; set; } = new(Colors.DodgerBlue);
    public Color AccentColor { get; set; }
    public Color GlowColor { get; set; }
    public Color WindowBgColor { get; set; }
    public Color SidebarBgColor { get; set; }
    public Color CardBgColor { get; set; }
    public Color CardHoverColor { get; set; }
    public Color BorderColor { get; set; }
}