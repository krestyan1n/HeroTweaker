using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Interfaces;
using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.Core.Services;

namespace HeroTweaker.Core.Models;

public class RegistryTweak : ITweak
{
    private readonly string _id;
    private readonly string _name;
    private readonly string _description;
    private readonly string _category;
    private readonly RegistryHive _hive;
    private readonly string _subKey;
    private readonly string _valueName;
    private readonly object _targetValue;
    private readonly object? _originalValue;
    private readonly bool _deleteKeyOnRollback;

    public string Id => _id;
    public string Name => _name;
    public string Description => _description;
    public string Category => _category;

    public TweakMetadata Metadata => new()
    {
        Id = _id,
        Name = _name,
        Category = _category,
        WhatItDoes = _description,
        Impact = _name,
        Risk = RiskLevel.Safe,
        Reboot = RebootRequirement.None
    };

    public RegistryTweak(
        string id,
        string name,
        string description,
        string category,
        RegistryHive hive,
        string subKey,
        string valueName,
        object targetValue,
        object? originalValue = null,
        bool deleteKeyOnRollback = false)
    {
        _id = id;
        _name = name;
        _description = description;
        _category = category;
        _hive = hive;
        _subKey = subKey;
        _valueName = valueName;
        _targetValue = targetValue;
        _originalValue = originalValue;
        _deleteKeyOnRollback = deleteKeyOnRollback;
    }

    public bool IsApplied()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(_subKey);
            if (key == null) return false;

            var val = key.GetValue(_valueName);
            if (val == null) return false;

            return val.ToString()?.Equals(_targetValue.ToString(), StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    public Task ApplyAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
                using var key = baseKey.CreateSubKey(_subKey);
                if (key == null) return;

                if (_targetValue is int intVal)
                    key.SetValue(_valueName, intVal, RegistryValueKind.DWord);
                else if (_targetValue is long longVal)
                    key.SetValue(_valueName, longVal, RegistryValueKind.QWord);
                else
                    key.SetValue(_valueName, _targetValue.ToString() ?? string.Empty, RegistryValueKind.String);
            }
            catch { }
        });
    }

    public async Task<StateSnapshot?> ApplyWithSnapshotAsync()
    {
        var snapshot = TransactionManager.CreateRegistrySnapshot(_hive, _subKey, _valueName);
        await ApplyAsync();
        return snapshot;
    }

    public Task RollbackAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
                if (_deleteKeyOnRollback)
                {
                    baseKey.DeleteSubKeyTree(_subKey, false);
                    return;
                }

                if (_originalValue == null)
                {
                    using var key = baseKey.OpenSubKey(_subKey, true);
                    key?.DeleteValue(_valueName, false);
                }
                else
                {
                    using var key = baseKey.CreateSubKey(_subKey);
                    if (key == null) return;

                    if (_originalValue is int intVal)
                        key.SetValue(_valueName, intVal, RegistryValueKind.DWord);
                    else if (_originalValue is long longVal)
                        key.SetValue(_valueName, longVal, RegistryValueKind.QWord);
                    else
                        key.SetValue(_valueName, _originalValue.ToString() ?? string.Empty, RegistryValueKind.String);
                }
            }
            catch { }
        });
    }
}