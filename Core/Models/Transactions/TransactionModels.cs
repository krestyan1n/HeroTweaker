using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HeroTweaker.Core.Models.Transactions;

public enum RiskLevel
{
    Safe,          // 🟢 Без рисков, полностью обратимо в 1 клик
    Advanced,      // 🟡 Требует понимания, может затронуть связанные функции
    Experimental   // 🔴 Глубокое вмешательство в ядро/таймеры/виртуализацию
}

public enum RebootRequirement
{
    None,            // Мгновенный эффект без перезапусков
    ExplorerRestart, // Требуется перезапуск проводника (explorer.exe)
    SystemReboot     // Требуется полная перезагрузка Windows
}

public enum SnapshotType
{
    Registry,
    Service,
    FileSystem
}

/// <summary>
/// Точный слепок состояния системы до изменения для гарантированного отката.
/// </summary>
// Снимки сохраняются в журнале, поэтому полиморфизм должен быть явно
// описан для System.Text.Json.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RegistrySnapshot), "registry")]
[JsonDerivedType(typeof(ServiceSnapshot), "service")]
public abstract class StateSnapshot
{
    public abstract SnapshotType Type { get; }
}

public sealed class RegistrySnapshot : StateSnapshot
{
    public override SnapshotType Type => SnapshotType.Registry;
    public RegistryHive Hive { get; set; }
    public string SubKeyPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public object? OriginalValue { get; set; }
    public RegistryValueKind ValueKind { get; set; }
    public bool KeyExisted { get; set; }
}

public sealed class ServiceSnapshot : StateSnapshot
{
    public override SnapshotType Type => SnapshotType.Service;
    public string ServiceName { get; set; } = string.Empty;
    public ServiceStartMode OriginalStartType { get; set; }
    public ServiceControllerStatus OriginalStatus { get; set; }
}

/// <summary>
/// Метаданные твика согласно концепции Why? + Risk + Impact
/// </summary>
public class TweakMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;
    public RebootRequirement Reboot { get; set; } = RebootRequirement.None;

    public string WhatItDoes { get; set; } = string.Empty; // Что конкретно меняется
    public string Impact { get; set; } = string.Empty;     // На что влияет
    public string? PotentialConflict { get; set; }         // Возможный конфликт (WSL, Hyper-V, Anticheat)
    public bool RollbackSupported { get; set; } = true;
}

/// <summary>
/// Запись в журнале аудита о выполненной транзакции
/// </summary>
public class TransactionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string TweakId { get; set; } = string.Empty;
    public string TweakName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsApplied { get; set; } // true = применен, false = откачен
    public bool IsRolledBack { get; set; }
    public List<StateSnapshot> Snapshots { get; set; } = new();
    public string Details { get; set; } = string.Empty;
}
