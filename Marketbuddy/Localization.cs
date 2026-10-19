using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Marketbuddy;

// Minimal self-contained localization helper mirroring ECommons.LanguageHelpers:
// same ini format (English==translation, one entry per line, literal \n escapes),
// the same .Loc() string extension name and the same "??" placeholder symbol.
// This plugin has no ECommons dependency, so we ship this tiny equivalent instead
// of pulling in the full library just for loc.
internal static class Localization
{
    private const string ParameterSymbol = "??";

    private static readonly Dictionary<string, string> Translations = [];

    public static void Init(string? directory)
    {
        Translations.Clear();
        if (directory == null)
            return;
        var path = Path.Combine(directory, "LanguageChineseTraditional.ini");
        try
        {
            if (!File.Exists(path))
                return;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var idx = line.IndexOf("==", StringComparison.Ordinal);
                if (idx <= 0)
                    continue;
                var key = line[..idx].Replace("\\n", "\n", StringComparison.Ordinal);
                var value = line[(idx + 2)..].TrimEnd('\r').Replace("\\n", "\n", StringComparison.Ordinal);
                Translations[key] = value;
            }
        }
        catch
        {
            // fall back to English keys if the ini can't be read
        }
    }

    public static string Loc(this string s)
        => Translations.TryGetValue(s, out var t) && !string.IsNullOrEmpty(t) ? t : s;

    public static string Loc(this string s, params object?[] values)
    {
        var result = s.Loc();
        foreach (var value in values)
        {
            var idx = result.IndexOf(ParameterSymbol, StringComparison.Ordinal);
            if (idx < 0)
                break;
            result = string.Concat(result.AsSpan(0, idx), value?.ToString(),
                result.AsSpan(idx + ParameterSymbol.Length));
        }

        return result;
    }
}
