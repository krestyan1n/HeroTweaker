public class DriveSelectModel
{
    public string RootPath { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TotalSize { get; set; } = string.Empty;
    public string AvailableFreeSpace { get; set; } = string.Empty;
    public double UsedPercentage { get; set; }

    // Коэффициент от 0.0 до 1.0 для железного отображения бара
    public double UsedRatio => Math.Clamp(UsedPercentage / 100.0, 0.005, 1.0);

    public DriveSelectModel() { }

    public DriveSelectModel(string rootPath, string volumeLabel, string displayName, string totalSize, string availableFreeSpace, double usedPercentage)
    {
        RootPath = rootPath;
        VolumeLabel = volumeLabel;
        DisplayName = displayName;
        TotalSize = totalSize;
        AvailableFreeSpace = availableFreeSpace;
        UsedPercentage = usedPercentage;
    }
}