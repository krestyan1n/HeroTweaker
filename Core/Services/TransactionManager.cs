using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Models.Transactions;

namespace HeroTweaker.Core.Services;

public static class TransactionManager
{
    private static readonly string AuditDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeroEngineering", "HeroTweaker", "Audit");

    private static readonly string AuditFile = Path.Combine(AuditDir, "history.json");

    private static readonly List<TransactionRecord> _inMemoryHistory = new();
    private static readonly object _lock = new();

    static TransactionManager()
    {
        LoadAuditHistory();
    }

    public static StateSnapshot CreateRegistrySnapshot(RegistryHive hive, string subKeyPath, string valueName)
    {
        var snapshot = new StateSnapshot
        {
            Type = SnapshotType.Registry,
            Hive = hive.ToString(),
            KeyPath = subKeyPath,
            ValueName = valueName,
            ExistedBefore = false
        };

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKeyPath);
            if (key != null)
            {
                var val = key.GetValue(valueName);
                if (val != null)
                {
                    snapshot.ExistedBefore = true;
                    snapshot.OriginalValue = val;
                    snapshot.ValueKind = key.GetValueKind(valueName).ToString();
                }
            }
        }
        catch { }

        return snapshot;
    }

    public static StateSnapshot CreateServiceSnapshot(string serviceName)
    {
        var snapshot = new StateSnapshot
        {
            Type = SnapshotType.Service,
            ServiceName = serviceName,
            OriginalStartMode = 2 // 2 = Automatic, 3 = Manual, 4 = Disabled
        };

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key?.GetValue("Start") is int mode)
            {
                snapshot.OriginalStartMode = mode;
            }
        }
        catch { }

        return snapshot;
    }

    public static async Task CommitTransactionAsync(TransactionRecord record)
    {
        lock (_lock)
        {
            _inMemoryHistory.Insert(0, record);
            if (_inMemoryHistory.Count > 200)
                _inMemoryHistory.RemoveAt(_inMemoryHistory.Count - 1);
        }

        await SaveAuditHistoryAsync();
    }

    public static async Task<(bool Success, string Message)> RollbackTransactionAsync(Guid transactionId)
    {
        TransactionRecord? record;
        lock (_lock)
        {
            record = _inMemoryHistory.FirstOrDefault(t => t.Id == transactionId);
        }

        if (record == null)
            return (false, "Транзакция не найдена в журнале аудита.");

        if (record.IsRolledBack)
            return (false, "Эта операция уже была отменена ранее.");

        return await Task.Run(() =>
        {
            try
            {
                foreach (var snapshot in record.Snapshots)
                {
                    if (snapshot.Type == SnapshotType.Registry)
                    {
                        RollbackRegistry(snapshot);
                    }
                    else if (snapshot.Type == SnapshotType.Service)
                    {
                        RollbackService(snapshot);
                    }
                }

                record.IsRolledBack = true;
                _ = SaveAuditHistoryAsync();
                return (true, $"Откат операции «{record.TweakName}» успешно выполнен.");
            }
            catch (Exception ex)
            {
                return (false, $"Ошибка отката: {ex.Message}");
            }
        });
    }

    private static void RollbackRegistry(StateSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.Hive) || string.IsNullOrEmpty(snapshot.KeyPath)) return;

        var hive = Enum.Parse<RegistryHive>(snapshot.Hive);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

        if (!snapshot.ExistedBefore)
        {
            using var key = baseKey.OpenSubKey(snapshot.KeyPath, true);
            if (key != null && !string.IsNullOrEmpty(snapshot.ValueName))
            {
                try { key.DeleteValue(snapshot.ValueName, false); } catch { }
            }
        }
        else
        {
            using var key = baseKey.CreateSubKey(snapshot.KeyPath);
            if (key != null && !string.IsNullOrEmpty(snapshot.ValueName) && snapshot.OriginalValue != null)
            {
                var kind = RegistryValueKind.DWord;
                if (!string.IsNullOrEmpty(snapshot.ValueKind))
                    Enum.TryParse(snapshot.ValueKind, out kind);

                key.SetValue(snapshot.ValueName, snapshot.OriginalValue, kind);
            }
        }
    }

    private static void RollbackService(StateSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.ServiceName)) return;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{snapshot.ServiceName}", true);
            key?.SetValue("Start", snapshot.OriginalStartMode, RegistryValueKind.DWord);

            if (snapshot.OriginalStartMode != 4)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"start {snapshot.ServiceName}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(1000);
            }
        }
        catch { }
    }

    public static IReadOnlyList<TransactionRecord> GetHistory()
    {
        lock (_lock)
        {
            return _inMemoryHistory.ToList();
        }
    }

    private static void LoadAuditHistory()
    {
        try
        {
            if (File.Exists(AuditFile))
            {
                var json = File.ReadAllText(AuditFile);
                var list = JsonSerializer.Deserialize<List<TransactionRecord>>(json);
                if (list != null)
                {
                    lock (_lock)
                    {
                        _inMemoryHistory.Clear();
                        _inMemoryHistory.AddRange(list);
                    }
                }
            }
        }
        catch { }
    }

    private static async Task SaveAuditHistoryAsync()
    {
        try
        {
            Directory.CreateDirectory(AuditDir);
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_inMemoryHistory, new JsonSerializerOptions { WriteIndented = true });
            }
            await File.WriteAllTextAsync(AuditFile, json);
        }
        catch { }
    }
}