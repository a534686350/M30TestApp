using System;
using System.IO;

namespace M30TestApp.Core.Common;

public enum LogLevel { Trace, Info, Warn, Error }

public sealed class LogEvent
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";

    public override string ToString() =>
        $"{Time:HH:mm:ss.fff} [{Level,-5}] {Source,-12} {Message}";
}

public static class AppLog
{
    public static event EventHandler<LogEvent>? Logged;

    private static readonly object _fileLock = new();
    private static string? _logFile;

    public static void Configure(string? file)
    {
        _logFile = file;
        if (file is not null)
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    }

    public static void Trace(string source, string msg) => Emit(LogLevel.Trace, source, msg);
    public static void Info (string source, string msg) => Emit(LogLevel.Info,  source, msg);
    public static void Warn (string source, string msg) => Emit(LogLevel.Warn,  source, msg);
    public static void Error(string source, string msg) => Emit(LogLevel.Error, source, msg);

    private static void Emit(LogLevel lvl, string source, string msg)
    {
        var e = new LogEvent { Level = lvl, Source = source, Message = msg };
        Logged?.Invoke(null, e);

        if (_logFile is null) return;
        try
        {
            lock (_fileLock)
                File.AppendAllText(_logFile, e + Environment.NewLine);
        }
        catch { /* swallow */ }
    }
}
