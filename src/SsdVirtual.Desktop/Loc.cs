using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SsdVirtual.Core;

namespace SsdVirtual.Desktop;

/// <summary>
/// Localization: every UI string comes from Strings/&lt;code&gt;.json (embedded) or from a custom language file in
/// %AppData%\SsdVirtual\lang\&lt;name&gt;.json. XAML binds with {Binding [Key], Source={x:Static local:Loc.I}}.
/// Missing keys fall back to English, then to the key itself.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();
    private Dictionary<string, string> _strings = new();
    private readonly Dictionary<string, string> _english;
    public string Language { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    private Loc()
    {
        _english = LoadEmbedded("en") ?? new Dictionary<string, string>();
        _strings = _english;
    }

    public string this[string key] => _strings.TryGetValue(key, out var v) ? v : _english.TryGetValue(key, out var e) ? e : key;

    public static string T(string key) => I[key];
    public static string F(string key, params object[] args) => string.Format(I[key], args);

    public static string CustomLanguageFolder => Path.Combine(AppPaths.UserRoot, "lang");

    public static IReadOnlyList<(string Code, string Name)> Available()
    {
        var list = new List<(string, string)> { ("en", "English"), ("id", "Bahasa Indonesia") };
        try
        {
            if (Directory.Exists(CustomLanguageFolder))
                foreach (var f in Directory.EnumerateFiles(CustomLanguageFolder, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    list.Add(("custom:" + name, name + " (custom)"));
                }
        }
        catch { }
        return list;
    }

    public void SetLanguage(string code)
    {
        Dictionary<string, string>? strings = null;
        if (code.StartsWith("custom:", StringComparison.Ordinal))
        {
            var file = Path.Combine(CustomLanguageFolder, code.Substring(7) + ".json");
            try { strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)); }
            catch (Exception ex) { Log.Warn("Custom language could not be loaded: " + file, ex); }
        }
        else strings = LoadEmbedded(code);
        _strings = strings ?? _english;
        Language = strings == null ? "en" : code;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    /// <summary>Writes the English strings as a template for translators.</summary>
    public static string ExportTemplate()
    {
        Directory.CreateDirectory(CustomLanguageFolder);
        var path = Path.Combine(CustomLanguageFolder, "template.json");
        File.WriteAllText(path, JsonSerializer.Serialize(I._english.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value), new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static IReadOnlyCollection<string> Keys(string code) => (IReadOnlyCollection<string>?)LoadEmbedded(code)?.Keys ?? Array.Empty<string>();

    private static Dictionary<string, string>? LoadEmbedded(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Strings." + code + ".json", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s);
    }
}
