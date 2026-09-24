using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public class CleanupCategoryModel : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "🧹";
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetField(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(FormattedSize));
            }
        }
    }

    public string FormattedSize => DiskItemModel.FormatBytes(SizeBytes);

    private int _filesCount;
    public int FilesCount
    {
        get => _filesCount;
        set => SetField(ref _filesCount, value);
    }
}