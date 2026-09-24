namespace HeroTweaker.Core.Models;

public record DriveSelectModel(
    string RootPath,
    string VolumeLabel,
    string DisplayName,
    string TotalSizeFormatted,
    string FreeSizeFormatted,
    double FreePercent
)
{
    public string FormattedDescription => $"💾 {DisplayName}  •  {FreeSizeFormatted} свободно из {TotalSizeFormatted}";
}