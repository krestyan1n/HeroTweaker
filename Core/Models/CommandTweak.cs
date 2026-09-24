using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HeroTweaker.Core.Interfaces;

namespace HeroTweaker.Core.Models;

public class CommandTweak : ITweak
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }

    private readonly Func<bool> _isAppliedCheck;
    private readonly string _applyFileName;
    private readonly string _applyArguments;
    private readonly string _rollbackFileName;
    private readonly string _rollbackArguments;

    public CommandTweak(
        string id,
        string name,
        string description,
        string category,
        Func<bool> isAppliedCheck,
        string applyFileName,
        string applyArguments,
        string rollbackFileName,
        string rollbackArguments)
    {
        Id = id;
        Name = name;
        Description = description;
        Category = category;
        _isAppliedCheck = isAppliedCheck ?? (() => false);
        _applyFileName = applyFileName;
        _applyArguments = applyArguments;
        _rollbackFileName = rollbackFileName;
        _rollbackArguments = rollbackArguments;
    }

    public bool IsApplied()
    {
        try
        {
            return _isAppliedCheck();
        }
        catch
        {
            return false;
        }
    }

    public Task ApplyAsync() => Task.Run(() => ExecuteProcess(_applyFileName, _applyArguments));

    public Task RollbackAsync() => Task.Run(() => ExecuteProcess(_rollbackFileName, _rollbackArguments));

    private static void ExecuteProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Ошибка выполнения команды {fileName} {arguments}: {ex.Message}", ex);
        }
    }
}