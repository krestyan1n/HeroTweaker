using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Models.Transactions;

namespace HeroTweaker.Core.Services;

public static class TransactionManager
{
    private static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HeroTweaker");

    private static readonly string HistoryFile = Path.Combine(StorageDir, "transactions.json");
    private static readonly List<TransactionRecord> _history = new();
    private static readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    static TransactionManager()
    {
        LoadHistory();
    }

    public static IReadOnlyList<TransactionRecord> GetHistory()
    {
        lock (_lock)
        {
            return _history.OrderByDescending(t => t.Timestamp).ToList();
        }
    }

    public static RegistrySnapshot? CreateRegistrySnapshot(RegistryHive hive, string subKeyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKeyPath);

            if (key == null)
            {
                return new RegistrySnapshot
                {
                    Hive = hive,
                    SubKeyPath = subKeyPath,
                    ValueName = valueName,
                    KeyExisted = false
                };
            }

            var kind = key.GetValueKind(valueName);
            var val = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

            return new RegistrySnapshot
            {
                Hive = hive,
                SubKeyPath = subKeyPath,
                ValueName = valueName,
                OriginalValue = val,
                ValueKind = kind,
                KeyExisted = true
            };
        }
        catch
        {
            return new RegistrySnapshot
            {
                Hive = hive,
                SubKeyPath = subKeyPath,
                ValueName = valueName,
                KeyExisted = false
            };
        }
    }

    public static ServiceSnapshot? CreateServiceSnapshot(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key == null) return null;

            int startMode = Convert.ToInt32(key.GetValue("Start", 3));
            ServiceControllerStatus status = ServiceControllerStatus.Stopped;

            try
            {
                using var sc = new ServiceController(serviceName);
                status = sc.Status;
            }
            catch { }

            return new ServiceSnapshot
            {
                ServiceName = serviceName,
                OriginalStartType = (ServiceStartMode)startMode,
                OriginalStatus = status
            };
        }
        catch
        {
            return null;
        }
    }

    public static async Task CommitTransactionAsync(TransactionRecord record)
    {
        if (record == null) return;

        lock (_lock)
        {
            _history.Insert(0, record);
            if (_history.Count > 100)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        await SaveHistoryAsync();
    }

    public static async Task<(bool Success, string Message)> RollbackTransactionAsync(Guid transactionId)
    {
        TransactionRecord? record;
        lock (_lock)
        {
            record = _history.FirstOrDefault(t => t.Id == transactionId);
        }

        if (record == null)
            return (false, "Транзакция не найдена в истории.");

        if (record.TweakId == "disk_clean" || record.Snapshots.Count == 0)
            return (false, "Данная операция очистки не подлежит откату.");

        try
        {
            foreach (var snapshot in record.Snapshots)
            {
                if (snapshot is RegistrySnapshot regSnap)
                {
                    RestoreRegistrySnapshot(regSnap);
                }
                else if (snapshot is ServiceSnapshot srvSnap)
                {
                    RestoreServiceSnapshot(srvSnap);
                }
            }

            lock (_lock)
            {
                _history.Remove(record);
            }

            await SaveHistoryAsync();
            return (true, $"Откат твика «{record.TweakName}» успешно выполнен.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка отката: {ex.Message}");
        }
    }

    private static void RestoreRegistrySnapshot(RegistrySnapshot snap)
    {
        using var baseKey = RegistryKey.OpenBaseKey(snap.Hive, RegistryView.Registry64);

        if (!snap.KeyExisted)
        {
            try
            {
                using var key = baseKey.OpenSubKey(snap.SubKeyPath, writable: true);
                key?.DeleteValue(snap.ValueName, throwOnMissingValue: false);
            }
            catch { }
            return;
        }

        using var subKey = baseKey.CreateSubKey(snap.SubKeyPath, writable: true);
        if (subKey == null) return;

        if (snap.OriginalValue == null)
        {
            subKey.DeleteValue(snap.ValueName, throwOnMissingValue: false);
            return;
        }

        object valueToSet = snap.OriginalValue;

        if (snap.OriginalValue is JsonElement jsonElement)
        {
            valueToSet = snap.ValueKind switch
            {
                RegistryValueKind.DWord => jsonElement.ValueKind == JsonValueKind.Number ? jsonElement.GetInt32() : Convert.ToInt32(jsonElement.GetString()),
                RegistryValueKind.QWord => jsonElement.ValueKind == JsonValueKind.Number ? jsonElement.GetInt64() : Convert.ToInt64(jsonElement.GetString()),
                RegistryValueKind.String or RegistryValueKind.ExpandString => jsonElement.GetString() ?? string.Empty,
                _ => jsonElement.ToString()
            };
        }
        else
        {
            valueToSet = snap.ValueKind switch
            {
                RegistryValueKind.DWord => Convert.ToInt32(snap.OriginalValue),
                RegistryValueKind.QWord => Convert.ToInt64(snap.OriginalValue),
                RegistryValueKind.String or RegistryValueKind.ExpandString => snap.OriginalValue.ToString() ?? string.Empty,
                _ => snap.OriginalValue
            };
        }

        subKey.SetValue(snap.ValueName, valueToSet, snap.ValueKind);
    }

    private static void RestoreServiceSnapshot(ServiceSnapshot snap)
    {
        string path = $@"SYSTEM\CurrentControlSet\Services\{snap.ServiceName}";
        using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
        if (key != null)
        {
            int modeVal = (int)snap.OriginalStartType;
            key.SetValue("Start", modeVal, RegistryValueKind.DWord);
        }

        try
        {
            using var sc = new ServiceController(snap.ServiceName);
            if (snap.OriginalStatus == ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
            }
            else if (snap.OriginalStatus == ServiceControllerStatus.Stopped && sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
            }
        }
        catch { }
    }

    private static void LoadHistory()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(HistoryFile))
                {
                    string json = File.ReadAllText(HistoryFile);
                    var list = JsonSerializer.Deserialize<List<TransactionRecord>>(json, JsonOptions);
                    if (list != null)
                    {
                        _history.Clear();
                        _history.AddRange(list);
                    }
                }
            }
            catch { }
        }
    }

    private static async Task SaveHistoryAsync()
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_history, JsonOptions);
            }
            await File.WriteAllTextAsync(HistoryFile, json);
        }
        catch { }
    }
}