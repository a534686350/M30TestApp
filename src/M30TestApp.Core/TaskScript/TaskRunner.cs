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

    /// <summary>Max consecutive errors before giving up (0 = never retry).</summary>
    public int MaxConsecutiveErrors { get; set; } = 3;

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
        Register(new RunLongTermStabilityTestAction());
        Register(new SaveTestDataAction());
        Register(new CalTestAction());
        return this;
    }

    public async Task RunAsync(TaskScript script, TaskContext ctx, CancellationToken ct)
    {
        var total = script.Commands.Count;
        var consecutiveErrors = 0;
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
                consecutiveErrors = 0; // reset on success
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                consecutiveErrors++;
                AppLog.Error("Runner", $"{cmd}: {ex.Message} (连续错误 {consecutiveErrors}/{MaxConsecutiveErrors})");
                Progress?.Invoke(this, new TaskProgress { Index = i, Total = total, Command = cmd, Phase = "Error", Error = ex.Message });

                if (MaxConsecutiveErrors > 0 && consecutiveErrors >= MaxConsecutiveErrors)
                {
                    AppLog.Error("Runner", $"连续 {consecutiveErrors} 次错误，停止测试");
                    throw;
                }

                AppLog.Warn("Runner", $"自动恢复，继续执行下一步…");
            }
        }
    }
}
