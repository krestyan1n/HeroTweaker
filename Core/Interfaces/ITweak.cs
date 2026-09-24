using System.Threading.Tasks;
using HeroTweaker.Core.Models.Transactions;

namespace HeroTweaker.Core.Interfaces;

public interface ITweak
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Category { get; }

    // Дефолтная реализация метаданных (устраняет CS0535 для всех существующих классов)
    TweakMetadata Metadata => new()
    {
        Id = Id,
        Name = Name,
        Category = Category,
        WhatItDoes = Description,
        Impact = Name,
        Risk = RiskLevel.Safe,
        Reboot = RebootRequirement.None
    };

    bool IsApplied();
    Task ApplyAsync();

    // Дефолтная реализация снимка: вызывает ApplyAsync и возвращает null, если снимок не переопределен
    async Task<StateSnapshot?> ApplyWithSnapshotAsync()
    {
        await ApplyAsync();
        return null;
    }

    Task RollbackAsync();
}