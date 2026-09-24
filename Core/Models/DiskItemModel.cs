using System;
using System.Collections.Generic;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public class DiskItemModel : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FormattedSize => FormatBytes(SizeBytes);
    public double Percentage { get; set; }
    public bool IsFolder { get; set; }
    public string ColorHex { get; set; } = "#6366F1";

    // Параметры геометрии диаграммы
    public double StartAngle { get; set; }
    public double SweepAngle { get; set; }
    public int RingLevel { get; set; } = 1; // 1 = внутреннее кольцо, 2 = внешнее кольцо

    public DiskItemModel? Parent { get; set; }
    public List<DiskItemModel> Children { get; set; } = new();

    // Состояние подсветки для двухсторонней синхронизации
    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set => SetField(ref _isHovered, value);
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
            if (counter >= suffixes.Length - 1) break;
        }
        return $"{number:F1} {suffixes[counter]}";
    }
}