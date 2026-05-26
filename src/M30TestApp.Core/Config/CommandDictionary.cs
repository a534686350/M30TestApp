using System;
using System.Collections.Generic;
using System.Globalization;

namespace M30TestApp.Core.Config;

/// <summary>
/// Loads `Command.ini` into a per-device-model command map.
/// Each section corresponds to a hardware model (e.g. "FLUKE-7250", "Keysight-34970A").
/// Commands often contain a sentinel `9999` placeholder or `{0}` to substitute parameters.
/// </summary>
public sealed class CommandDictionary
{
    private readonly IniFile _ini;

    public CommandDictionary(IniFile ini) => _ini = ini;

    public static CommandDictionary Load(string path) => new(IniFile.Load(path));

    public IEnumerable<string> Models => _ini.Sections;

    public bool Has(string model, string command) => _ini.TryGet(model, command, out _);

    /// <summary>Get raw command template for the given device model.</summary>
    public string Raw(string model, string command, string fallback = "")
        => _ini.Get(model, command, fallback);

    /// <summary>Render a command: substitutes `9999` and `{0}`/`{1}`/... with args.</summary>
    public string Render(string model, string command, params object[] args)
    {
        var raw = Raw(model, command);
        if (string.IsNullOrEmpty(raw)) return raw;

        // Substitute LabVIEW-style 9999 sentinel with first arg.
        if (args.Length > 0 && raw.Contains("9999"))
            raw = raw.Replace("9999", Format(args[0]));

        // Substitute {0}, {1}, ... if present.
        if (raw.Contains("{0}") || raw.Contains("{1}"))
        {
            try { raw = string.Format(CultureInfo.InvariantCulture, raw, args); }
            catch { /* keep raw if format mismatch */ }
        }

        return raw;
    }

    private static string Format(object o)
    {
        if (o is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
        return o?.ToString() ?? "";
    }
}
