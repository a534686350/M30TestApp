using System;
using M30TestApp.Core.Config;
using M30TestApp.Core.Devices.Hw;
using M30TestApp.Core.Devices.Sim;

namespace M30TestApp.Core.Devices;

/// <summary>
/// Builds devices from a StationProfile. Each device kind has a SIM backend and
/// (eventually) HW backends keyed by the model name in Command.ini.
///
/// For now, HW backends fall back to SIM with a warning so the rest of the system
/// can run end-to-end. Real SCPI/serial backends will be wired in as we migrate the
/// original Func_*.cs drivers.
/// </summary>
public sealed class DeviceFactory
{
    private readonly CommandDictionary _commands;

    public DeviceFactory(CommandDictionary commands) => _commands = commands;

    public IPressureController CreatePressure(DeviceProfile p)
        => p.Backend == DeviceBackend.Hw
            ? new HwPressureController(p, _commands)
            : new SimPressureController(p.Model, p.Address, _commands);

    public IOven CreateOven(DeviceProfile p)
        => p.Backend == DeviceBackend.Hw
            ? new HwOven(p)
            : new SimOven(p.Model, p.Address, _commands);

    public IDmm CreateDmm(DeviceProfile p)
        => p.Backend == DeviceBackend.Hw
            ? new HwDmm(p)
            : new SimDmm(p.Model, p.Address, _commands);

    public IDac CreateDac(DeviceProfile p)
        => p.Backend == DeviceBackend.Hw
            ? new HwDac(p)
            : new SimDac(p.Model, p.Address, _commands);

    public IPowerSupply CreatePower(DeviceProfile p)
        => p.Backend == DeviceBackend.Hw
            ? new HwPowerSupply(p, _commands)
            : new SimPower(p.Model, p.Address, _commands);

    public IBoard CreateBoard(DeviceProfile p)
        => p.Backend == DeviceBackend.Hw
            ? new HwBoard(p)
            : new SimBoard(p.Model, p.Address, _commands);
}
