using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

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

    private static readonly object _configureLock = new();
    private static readonly Channel<LogEvent> _fileQueue = Channel.CreateUnbounded<LogEvent>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private static string? _logFile;
    private static bool _writerStarted;
    private static readonly object _flushLock = new();
    private static int _pendingFileEvents;
    private static TaskCompletionSource<bool> _drained =
        CompletedDrain();

    public static void Configure(string? file)
    {
        lock (_configureLock)
        {
            _logFile = file;
            if (file is not null)
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            if (!_writerStarted)
            {
                _writerStarted = true;
                _ = Task.Run(ProcessFileQueueAsync);
            }
        }
    }

    public static void Trace(string source, string msg) => Emit(LogLevel.Trace, source, msg);
    public static void Info (string source, string msg) => Emit(LogLevel.Info,  source, msg);
    public static void Warn (string source, string msg) => Emit(LogLevel.Warn,  source, msg);
    public static void Error(string source, string msg) => Emit(LogLevel.Error, source, msg);

    private static void Emit(LogLevel lvl, string source, string msg)
    {
        var e = new LogEvent { Level = lvl, Source = source, Message = msg };
        Logged?.Invoke(null, e);

        if (_logFile is not null)
        {
            lock (_flushLock)
            {
                if (_pendingFileEvents == 0)
                    _drained = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingFileEvents++;
            }
            _fileQueue.Writer.TryWrite(e);
        }
    }

    /// <summary>
    /// Wait until all log events emitted before this call have been written.
    /// This is used before a run is reported as finished or failed so that
    /// transient pressure/device errors are not missing from the saved log.
    /// </summary>
    public static async Task FlushAsync(TimeSpan timeout)
    {
        Task wait;
        lock (_flushLock)
            wait = _pendingFileEvents == 0 ? Task.CompletedTask : _drained.Task;

        await wait.WaitAsync(timeout).ConfigureAwait(false);
    }

    private static async Task ProcessFileQueueAsync()
    {
        var batch = new List<string>(128);
        while (await _fileQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 128 && _fileQueue.Reader.TryRead(out var e))
                batch.Add(e.ToString());

            if (batch.Count == 0) continue;

            try
            {
                var file = _logFile;
                if (file is not null)
                    File.AppendAllLines(file, batch);
            }
            catch (Exception ex)
            {
                // Do not enqueue another error here: that would recurse forever.
                System.Diagnostics.Debug.WriteLine($"Log write failed: {ex}");
            }
            finally
            {
                lock (_flushLock)
                {
                    _pendingFileEvents = Math.Max(0, _pendingFileEvents - batch.Count);
                    if (_pendingFileEvents == 0)
                        _drained.TrySetResult(true);
                }
            }
        }
    }

    private static TaskCompletionSource<bool> CompletedDrain()
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult(true);
        return tcs;
    }
}
