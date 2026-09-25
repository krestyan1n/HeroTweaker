using System;
using HeroTweaker.ViewModels.Base;

namespace HeroTweaker.Core.Models;

public class DiskItemModel : ObservableObject
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private string _fullPath = string.Empty;
    public string FullPath
    {
        get => _fullPath;
        set => SetField(ref _fullPath, value);
    }

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetField(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(FormattedSize));
            }
        }
    }

    private bool _isFolder;
    public bool IsFolder
    {
        get => _isFolder;
        set => SetField(ref _isFolder, value);
    }

    private double _percentage;
    public double Percentage
    {
        get => _percentage;
        set => SetField(ref _percentage, value);
    }

    private string _colorHex = "#38BDF8";
    public string ColorHex
    {
        get => _colorHex;
        set => SetField(ref _colorHex, value);
    }

    // Состояние наведения мыши для подсветки в DiskDonutChart
    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set => SetField(ref _isHovered, value);
    }

    // Уровень кольца диаграммы (1 - внутреннее, 2 - внешнее)
    private int _ringLevel = 1;
    public int RingLevel
    {
        get => _ringLevel;
        set
        {
            if (SetField(ref _ringLevel, value))
            {
                OnPropertyChanged(nameof(Level));
            }
        }
    }

    // Псевдоним для совместимости со сканером и кэшем
    public int Level
    {
        get => RingLevel;
        set => RingLevel = value;
    }

    private double _startAngle;
    public double StartAngle
    {
        get => _startAngle;
        set => SetField(ref _startAngle, value);
    }

    private double _sweepAngle;
    public double SweepAngle
    {
        get => _sweepAngle;
        set => SetField(ref _sweepAngle, value);
    }

    public string FormattedSize => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 Б";
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.##} {units[i]}";
    }
}