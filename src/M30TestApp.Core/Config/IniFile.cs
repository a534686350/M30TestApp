using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace M30TestApp.Core.Config;

/// <summary>
/// Lightweight INI reader compatible with LabVIEW-style ini and the M30/ASLab files.
/// - Supports [Section] headers (case-insensitive).
/// - Keys are case-insensitive; values can be quoted "..." with escape sequences
///   `\0A` (LF), `\0D` (CR), `\09` (TAB), `\\` (backslash), `\"` (quote).
/// - Lines starting with ';' or '#' are comments.
/// - Bare keys without sections go into the empty-string section.
/// </summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Sections => _sections.Keys;

    public IReadOnlyDictionary<string, string> this[string section] =>
        _sections.TryGetValue(section, out var s)
            ? s
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string section, string key, out string value)
    {
        value = "";
        if (!_sections.TryGetValue(section, out var s)) return false;
        return s.TryGetValue(key, out value!);
    }

    public string Get(string section, string key, string fallback = "") =>
        TryGet(section, key, out var v) ? v : fallback;

    public void Set(string section, string key, string value)
    {
        if (!_sections.TryGetValue(section, out var s))
        {
            s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = s;
        }
        s[key] = value;
    }

    public static IniFile Load(string path)
    {
        var ini = new IniFile();
        if (!File.Exists(path)) return ini;

        string current = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

            if (line[0] == '[' && line.EndsWith("]"))
            {
                current = line.Substring(1, line.Length - 2).Trim();
                if (!ini._sections.ContainsKey(current))
                    ini._sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line.Substring(0, idx).Trim();
            var val = line.Substring(idx + 1).Trim();
            val = Unquote(val);
            ini.Set(current, key, val);
        }
        return ini;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var sw = new StreamWriter(path);
        bool first = true;
        foreach (var (sec, kvs) in _sections.OrderBy(s => s.Key))
        {
            if (!first) sw.WriteLine();
            first = false;
            if (sec.Length > 0) sw.WriteLine($"[{sec}]");
            foreach (var (k, v) in kvs)
                sw.WriteLine($"{k} = \"{Quote(v)}\"");
        }
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);

        // Decode LabVIEW-style \0A, \0D, \09, \\, \"
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 2 < s.Length)
            {
                var hex = s.Substring(i + 1, 2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var code))
                {
                    sb.Append((char)code);
                    i += 2;
                    continue;
                }
            }
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                switch (s[i + 1])
                {
                    case '\\': sb.Append('\\'); i++; continue;
                    case '"':  sb.Append('"');  i++; continue;
                    case 'n':  sb.Append('\n'); i++; continue;
                    case 'r':  sb.Append('\r'); i++; continue;
                    case 't':  sb.Append('\t'); i++; continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\0A"); break;
                case '\r': sb.Append("\\0D"); break;
                case '\t': sb.Append("\\09"); break;
                default:   sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
