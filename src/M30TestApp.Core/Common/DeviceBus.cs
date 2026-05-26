using System;

namespace M30TestApp.Core.Common;

public enum BusDirection { Tx, Rx, Info }

public sealed record BusEvent(DateTime Time, string Device, BusDirection Direction, string Payload)
{
    public string Arrow => Direction switch
    {
        BusDirection.Tx => "▶",
        BusDirection.Rx => "◀",
        _ => "·"
    };

    public override string ToString() =>
        $"{Time:HH:mm:ss.fff} {Arrow} {Device,-18} {Payload}";
}

/// <summary>
/// Global TX / RX trace bus. Every device implementation (HW or SIM) reports the raw
/// command it would send and the response it would receive. The Manual view subscribes
/// to render a wire-level inspector. Lightweight: a multicast event with no buffering.
/// </summary>
public static class DeviceBus
{
    public static event EventHandler<BusEvent>? Traffic;

    public static void Tx(string device, string payload)
        => Traffic?.Invoke(null, new BusEvent(DateTime.Now, device, BusDirection.Tx, payload));

    public static void Rx(string device, string payload)
        => Traffic?.Invoke(null, new BusEvent(DateTime.Now, device, BusDirection.Rx, payload));

    public static void Info(string device, string payload)
        => Traffic?.Invoke(null, new BusEvent(DateTime.Now, device, BusDirection.Info, payload));
}
