using System;

namespace HeroTweaker.Core.Models;

public enum LogLevel
{
    Info,
    Command,
    Success,
    Warning,
    Error
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
    public LogLevel Level { get; set; }
    public string FormattedTime { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;

    public LogEntry(DateTime timestamp, string message, LogLevel level)
    {
        Timestamp = timestamp;
        Message = message;
        Level = level;
        FormattedTime = timestamp.ToString("HH:mm:ss");

        Prefix = level switch
        {
            LogLevel.Command => "> [EXEC]",
            LogLevel.Success => "+ [OK]",
            LogLevel.Warning => "! [WARN]",
            LogLevel.Error => "x [ERR]",
            _ => "i [INFO]"
        };

        ColorHex = level switch
        {
            LogLevel.Command => "#38BDF8", // Неоново-голубой
            LogLevel.Success => "#34D399", // Изумрудно-зеленый
            LogLevel.Warning => "#FBBF24", // Янтарно-желтый
            LogLevel.Error => "#F87171",   // Красный
            _ => "#94A3B8"                 // Серый
        };
    }
}