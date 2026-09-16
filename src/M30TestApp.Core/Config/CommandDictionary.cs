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

    // ─── 型号（节）增删：供「设置 → 指令 / 压力指令」页维护 Command.ini ───────────

    /// <summary>
    /// 节内用于标识设备类型的键名。它不是设备指令，只服务于型号列表的分组；
    /// 用 `__` 前缀避免与真实指令键冲突。
    /// </summary>
    public const string KindKey = "__Kind";

    /// <summary>该型号节是否已存在。</summary>
    public bool HasModel(string model) =>
        !string.IsNullOrWhiteSpace(model) && _ini.Sections.Contains(model, StringComparer.OrdinalIgnoreCase);

    /// <summary>读取型号的类型标识；老节没有该键时返回空串。</summary>
    public string KindOf(string model) => _ini.Get(model, KindKey, "");

    /// <summary>该型号节里实际出现的键名（不含类型标识）。</summary>
    public IReadOnlyCollection<string> KeysOf(string model) =>
        _ini.Keys(model).Where(k => !k.Equals(KindKey, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>新增型号节：写入类型标识与指令模板。同名节会被覆盖。</summary>
    public void AddModel(string model, string kind, IEnumerable<KeyValuePair<string, string>> commands)
    {
        if (!string.IsNullOrWhiteSpace(kind)) _ini.Set(model, KindKey, kind);
        foreach (var (key, value) in commands) _ini.Set(model, key, value);
    }

    /// <summary>写入 / 更新单条指令。</summary>
    public void SetCommand(string model, string command, string value) => _ini.Set(model, command, value);

    /// <summary>删除整个型号节。不存在返回 false。</summary>
    public bool RemoveModel(string model) => _ini.RemoveSection(model);

    /// <summary>把当前内存中的命令表写回 ini 文件。</summary>
    public void Save(string path) => _ini.Save(path);

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
