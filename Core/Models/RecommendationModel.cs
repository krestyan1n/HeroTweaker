using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public class RecommendationModel : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string TweakId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Why { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;
    public int ImpactScore { get; set; } = 5; // Вклад в Health Score
}