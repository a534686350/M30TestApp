using System;
using System.Collections.Generic;
using System.Linq;

namespace M30TestApp.Core.TaskScript;

/// <summary>
/// One parsed unit of a TestTaskPoint script. Syntax:
///     Module:Action[,arg1[,arg2...]]
/// Example: "TP:SetPressurePoint,1,TEST"
/// </summary>
public readonly record struct TaskCommand(string Module, string Action, IReadOnlyList<string> Args)
{
    public string Key => $"{Module}:{Action}";

    public override string ToString() =>
        Args.Count == 0 ? Key : $"{Key},{string.Join(",", Args)}";

    public static TaskCommand Parse(string token)
    {
        token = token.Trim();
        var commaParts = token.Split(',');
        var head = commaParts[0];
        var colon = head.IndexOf(':');
        if (colon < 0) throw new FormatException($"Invalid command (missing ':'): '{token}'");
        var module = head.Substring(0, colon).Trim();
        var action = head.Substring(colon + 1).Trim();
        var args = commaParts.Length > 1
            ? commaParts.Skip(1).Select(a => a.Trim()).ToArray()
            : Array.Empty<string>();
        return new TaskCommand(module, action, args);
    }
}

/// <summary>
/// A pipe-separated sequence of commands, e.g.
///   "Initial:Pressure|Initial:Board|DAQ:ClearData|TP:SetPressurePoint,1,TEST|Read:Usign"
/// </summary>
public sealed class TaskScript
{
    public IReadOnlyList<TaskCommand> Commands { get; }
    public string Source { get; }

    private TaskScript(string source, IReadOnlyList<TaskCommand> cmds)
    {
        Source = source; Commands = cmds;
    }

    public static TaskScript Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new TaskScript(source ?? "", Array.Empty<TaskCommand>());

        var tokens = source.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<TaskCommand>(tokens.Length);
        foreach (var t in tokens)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            list.Add(TaskCommand.Parse(t));
        }
        return new TaskScript(source, list);
    }
}
