using System;
using System.Threading.Tasks;
using HeroTweaker.Core.Interfaces;
using HeroTweaker.Core.Models;
using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.ViewModels;

public class TweakViewModel : ObservableObject
{
    private readonly ITweak _tweak;
    private readonly Func<Task>? _revertAction;
    private readonly Func<TransactionRecord?>? _snapshotFunc;

    public string Id => _tweak.Id;
    public string Name => _tweak.Name;
    public string Description => _tweak.Description;
    public string Category => _tweak.Category;
    public bool IsFeatured { get; }
    public RiskLevel Risk { get; }

    // Двухуровневое подробное описание
    private string _userWhy = string.Empty;
    public string UserWhy
    {
        get => _userWhy;
        set => SetField(ref _userWhy, value);
    }

    private string _techDetails = string.Empty;
    public string TechDetails
    {
        get => _techDetails;
        set => SetField(ref _techDetails, value);
    }

    private string _rebootText = "Мгновенно";
    public string RebootText
    {
        get => _rebootText;
        set => SetField(ref _rebootText, value);
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    private bool _currentState;
    public bool CurrentState
    {
        get => _currentState;
        set
        {
            if (SetField(ref _currentState, value))
            {
                OnPropertyChanged(nameof(HasPendingChange));
            }
        }
    }

    private bool _targetState;
    public bool TargetState
    {
        get => _targetState;
        set
        {
            if (SetField(ref _targetState, value))
            {
                OnPropertyChanged(nameof(HasPendingChange));
                OnPendingStateChanged?.Invoke();
            }
        }
    }

    public bool HasPendingChange => CurrentState != TargetState;

    public Action? OnPendingStateChanged { get; set; }

    public string RiskText => Risk switch
    {
        RiskLevel.Safe => "Безопасно",
        RiskLevel.Advanced => "Продвинутый",
        RiskLevel.Experimental => "Опасно",
        _ => "Безопасно"
    };

    public string RiskColorHex => Risk switch
    {
        RiskLevel.Safe => "#10B981",
        RiskLevel.Advanced => "#FBBF24",
        RiskLevel.Experimental => "#F87171",
        _ => "#10B981"
    };

    public TweakViewModel(ITweak tweak, Func<Task>? revertAction = null, Func<TransactionRecord?>? snapshotFunc = null, bool isFeatured = false, RiskLevel risk = RiskLevel.Safe)
    {
        _tweak = tweak;
        _revertAction = revertAction;
        _snapshotFunc = snapshotFunc;
        IsFeatured = isFeatured;
        Risk = risk;

        RefreshState();
    }

    public void RefreshState()
    {
        try
        {
            CurrentState = _tweak.IsApplied();
            TargetState = CurrentState;
        }
        catch
        {
            CurrentState = false;
            TargetState = false;
        }
    }

    public void ResetPending()
    {
        TargetState = CurrentState;
    }

    public async Task<TransactionRecord?> CommitChangeWithTransactionAsync()
    {
        if (!HasPendingChange) return null;

        TransactionRecord? record = null;
        try
        {
            if (TargetState)
            {
                record = _snapshotFunc?.Invoke();
                if (record != null)
                {
                    record.IsApplied = true;
                    record.Details = $"Применён твик: {Name}";
                }
                await _tweak.ApplyAsync();
            }
            else
            {
                if (_revertAction != null)
                {
                    await _revertAction();
                }
            }

            CurrentState = TargetState;
        }
        catch (Exception ex)
        {
            ResetPending();
            throw new Exception($"Ошибка при применении '{Name}': {ex.Message}");
        }

        return record;
    }
}