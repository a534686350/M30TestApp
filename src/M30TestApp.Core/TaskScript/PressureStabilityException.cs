using System;

namespace M30TestApp.Core.TaskScript;

/// <summary>
/// Raised when the pressure controller cannot keep the requested pressure
/// stable after the configured recovery attempts.
/// </summary>
public sealed class PressureStabilityException : InvalidOperationException
{
    public PressureStabilityException(string message)
        : base(message)
    {
    }

    public PressureStabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
