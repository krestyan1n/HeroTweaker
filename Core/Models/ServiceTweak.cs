using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using HeroTweaker.Core.Interfaces;
using HeroTweaker.Core.Models.Transactions;
using HeroTweaker.Core.Services;

namespace HeroTweaker.Core.Models;

public class ServiceTweak : ITweak
{
    private readonly string _id;
    private readonly string _name;
    private readonly string _description;
    private readonly string _serviceName;
    private readonly int _targetStartMode;
    private readonly int _defaultStartMode;
    private readonly RiskLevel _risk;

    public string Id => _id;
    public string Name => _name;
    public string Description => _description;
    public string Category => "Службы";

    public TweakMetadata Metadata => new()
    {
        Id = _id,
        Name = _name,
        Category = Category,
        WhatItDoes = $"Управляет режимом автозапуска системной службы {_serviceName}.",
        Impact = _description,
        Risk = _risk,
        Reboot = RebootRequirement.None
    };

    public ServiceTweak(
        string id,
        string name,
        string description,
        string serviceName,
        int targetStartMode = 4, // 4 = Disabled
        int defaultStartMode = 2, // 2 = Automatic, 3 = Manual
        RiskLevel risk = RiskLevel.Safe)
    {
        _id = id;
        _name = name;
        _description = description;
        _serviceName = serviceName;
        _targetStartMode = targetStartMode;
        _defaultStartMode = defaultStartMode;
        _risk = risk;
    }

    public bool IsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}");
            if (key?.GetValue("Start") is int currentMode)
                return currentMode == _targetStartMode;
            return false;
        }
        catch { return false; }
    }

    public Task ApplyAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}", true);
                key?.SetValue("Start", _targetStartMode, RegistryValueKind.DWord);

                if (_targetStartMode == 4)
                {
                    var psi = new ProcessStartInfo { FileName = "sc.exe", Arguments = $"stop {_serviceName}", CreateNoWindow = true, UseShellExecute = false };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(1000);
                }
            }
            catch { }
        });
    }

    public async Task<StateSnapshot?> ApplyWithSnapshotAsync()
    {
        var snapshot = TransactionManager.CreateServiceSnapshot(_serviceName);
        await ApplyAsync();
        return snapshot;
    }

    public Task RollbackAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{_serviceName}", true);
                key?.SetValue("Start", _defaultStartMode, RegistryValueKind.DWord);

                if (_defaultStartMode != 4)
                {
                    var psi = new ProcessStartInfo { FileName = "sc.exe", Arguments = $"start {_serviceName}", CreateNoWindow = true, UseShellExecute = false };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(1000);
                }
            }
            catch { }
        });
    }
}