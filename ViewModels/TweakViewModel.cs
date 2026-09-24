using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Interfaces;
using HeroTweaker.Core.Models;
using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.ViewModels;

public class TweakViewModel : ObservableObject
{
    private readonly ITweak _tweak;
    private readonly Func<Task> _revertAction;
    private readonly Func<TransactionRecord?> _snapshotFunc;

    public ITweak Tweak => _tweak;
    public string Id => _tweak.Id;
    public string Name => _tweak.Name;
    public string Description => _tweak.Description;
    public string Category => _tweak.Category;
    public TweakMetadata Metadata => _tweak.Metadata;
    public bool IsFeatured { get; }
    public RiskLevel Risk { get; }

    public Action? OnPendingStateChanged { get; set; }

    private bool _currentState;
    public bool CurrentState
    {
        get => _currentState;
        private set
        {
            if (SetField(ref _currentState, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsApplied));
                OnPropertyChanged(nameof(HasPendingChange));
            }
        }
    }

    public bool IsActive => CurrentState;
    public bool IsApplied => CurrentState;

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

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public string RiskText => Risk switch
    {
        RiskLevel.Safe => "🟢 Безопасно",
        RiskLevel.Advanced => "🟡 Осторожно",
        RiskLevel.Experimental => "🔴 Рискованно",
        _ => "🟢 Безопасно"
    };

    public string RiskColorHex => Risk switch
    {
        RiskLevel.Safe => "#10B981",
        RiskLevel.Advanced => "#FBBF24",
        RiskLevel.Experimental => "#F87171",
        _ => "#10B981"
    };

    public string RebootText => (Risk == RiskLevel.Advanced || Risk == RiskLevel.Experimental)
        ? "⚠️ Требуется перезагрузка ПК для применения"
        : "⚡ Применяется мгновенно (без перезагрузки)";

    public TweakViewModel(ITweak tweak, Func<Task> revertAction, Func<TransactionRecord?> snapshotFunc, bool isFeatured = false, RiskLevel risk = RiskLevel.Safe)
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
        CurrentState = _tweak.IsApplied();
        TargetState = CurrentState;
    }

    public void ResetPending()
    {
        TargetState = CurrentState;
    }

    public async Task<TransactionRecord?> CommitChangeWithTransactionAsync()
    {
        if (!HasPendingChange) return null;

        var record = _snapshotFunc() ?? new TransactionRecord { TweakId = Id, TweakName = Name, Category = Category };
        record.IsApplied = TargetState;
        record.Details = $"Состояние изменено на {(TargetState ? "Включено" : "Отключено")}";

        try
        {
            if (TargetState)
            {
                // Применение твика (прописывание значений в реестр)
                await _tweak.ApplyAsync();

                // Автоматическая остановка службы в фоне, чтобы изменения вступили в силу сразу
                await Task.Run(() =>
                {
                    try
                    {
                        var type = _tweak.GetType();
                        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
                        var pathObj = type.GetProperty("KeyPath", flags)?.GetValue(_tweak) as string ??
                                      type.GetField("KeyPath", flags)?.GetValue(_tweak) as string;

                        if (pathObj != null && pathObj.StartsWith(@"SYSTEM\CurrentControlSet\Services\", StringComparison.OrdinalIgnoreCase))
                        {
                            string srv = pathObj.Substring(@"SYSTEM\CurrentControlSet\Services\".Length);
                            Process.Start(new ProcessStartInfo { FileName = "sc.exe", Arguments = $"stop \"{srv}\"", CreateNoWindow = true, UseShellExecute = false });
                        }
                    }
                    catch { }
                });
            }
            else
            {
                // Вызываем выделенную надежную лямбду отката
                await _revertAction();
            }
        }
        catch { }

        // Обновляем реальный статус прямо из реестра — тумблер больше не отпрыгнет
        CurrentState = _tweak.IsApplied();
        TargetState = CurrentState;

        return record;
    }
}