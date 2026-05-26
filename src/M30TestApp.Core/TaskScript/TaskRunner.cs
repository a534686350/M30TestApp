using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using M30TestApp.Core.Common;
using M30TestApp.Core.TaskScript.Actions;

namespace M30TestApp.Core.TaskScript;

public sealed class TaskProgress
{
    public int Index { get; init; }
    public int Total { get; init; }
    public TaskCommand Command { get; init; }
    public string Phase { get; init; } = "";   // "Start" | "Done" | "Error"
    public string? Error { get; init; }
}

/// <summary>
/// Executes a TaskScript over a TaskContext using a registry of IAction handlers.
/// Unknown commands are logged and skipped (configurable).
/// </summary>
public sealed class TaskRunner
{
    private readonly Dictionary<string, IAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public bool ThrowOnUnknown { get; set; } = false;

    public event EventHandler<TaskProgress>? Progress;

    public TaskRunner Register(IAction action)
    {
        _actions[action.Key] = action;
        return this;
    }

    public TaskRunner RegisterBuiltins()
    {
        Register(new InitialPressureAction());
        Register(new InitialOvenAction());
        Register(new InitialBoardAction());
        Register(new InitialDmmAction());
        Register(new InitialCommuTestAction());
        Register(new DaqClearDataAction());
        Register(new DaqTestTypeAction());
        Register(new DaqDownAction());
        Register(new TpSetPressurePointAction());
        Register(new TpSetTempPointAction());
        Register(new TpVentAction());
        Register(new TpReturnRoomTempAction());
        Register(new TpStopTempAction());
        Register(new ReadRAction());
        Register(new ReadUsourceAction());
        Register(new ReadIsourceAction());
        Register(new ReadUtAction());
        Register(new ReadUsignAction());
        Register(new ReadDmmSampleAction());
        Register(new RunPerformanceTestAction());
        Register(new SaveTestDataAction());
        Register(new CalTestAction());
        return this;
    }

    public async Task RunAsync(TaskScript script, TaskContext ctx, CancellationToken ct)
    {
        var total = script.Commands.Count;
        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cmd = script.Commands[i];
            Progress?.Invoke(this, new TaskProgress { Index = i, Total = total, Command = cmd, Phase = "Start" });

            if (!_actions.TryGetValue(cmd.Key, out var action))
            {
                var msg = $"Unknown action: {cmd.Key}";
                if (ThrowOnUnknown) throw new InvalidOperationException(msg);
                AppLog.Warn("Runner", msg);
                Progress?.Invoke(this, new TaskProgress { Index = i, Total = total, Command = cmd, Phase = "Done" });
                continue;
            }

            try
            {
                await action.ExecuteAsync(ctx, cmd, ct).ConfigureAwait(false);
                Progress?.Invoke(this, new TaskProgress { Index = i, Total = total, Command = cmd, Phase = "Done" });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Error("Runner", $"{cmd}: {ex.Message}");
                Progress?.Invoke(this, new TaskProgress { Index = i, Total = total, Command = cmd, Phase = "Error", Error = ex.Message });
                throw;
            }
        }
    }
}
