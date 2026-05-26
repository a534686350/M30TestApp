using System.Threading;
using System.Threading.Tasks;

namespace M30TestApp.Core.TaskScript.Actions;

public interface IAction
{
    /// <summary>Action key in "Module:Action" form, e.g. "TP:SetPressurePoint".</summary>
    string Key { get; }

    Task ExecuteAsync(TaskContext ctx, TaskCommand cmd, CancellationToken ct);
}
