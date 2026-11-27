using System.Globalization;
using System.Text.Json;

namespace VMCrate.Core;

public sealed record Locale(string Id, string NativeName, string CultureTag, bool RightToLeft = false);

public sealed class TextCatalog
{
    public static IReadOnlyList<Locale> Locales { get; } =
    [
        new("en", "English", "en-US"),
        new("zh-Hans", "简体中文", "zh-CN"),
        new("hi", "हिन्दी", "hi-IN"),
        new("es", "Español", "es-ES"),
        new("ar", "العربية", "ar-SA", true),
        new("fr", "Français", "fr-FR"),
        new("bn", "বাংলা", "bn-BD"),
        new("pt", "Português", "pt-BR"),
        new("id", "Bahasa Indonesia", "id-ID"),
        new("ur", "اردو", "ur-PK", true),
        new("ru", "Русский", "ru-RU"),
        new("de", "Deutsch", "de-DE"),
        new("ja", "日本語", "ja-JP"),
        new("pcm", "Naijá", "en-NG"),
        new("arz", "مصري", "ar-EG", true)
    ];

    private readonly Dictionary<string, string> english = Load("en");
    private Dictionary<string, string> strings;

    public TextCatalog(string? language)
    {
        Current = Resolve(language);
        strings = Current.Id == "en" ? english : Load(Current.Id);
    }

    public Locale Current { get; private set; }

    public string this[string key] => strings.GetValueOrDefault(key) ?? english.GetValueOrDefault(key) ?? key;

    public void Change(string id)
    {
        var locale = Resolve(id);
        strings = locale.Id == "en" ? english : Load(locale.Id);
        Current = locale;
    }

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.GetCultureInfo(Current.CultureTag), this[key], args);

    public static Locale Resolve(string? id)
    {
        if (string.Equals(id, "ar-EG", StringComparison.OrdinalIgnoreCase))
            return Locales.Single(locale => locale.Id == "arz");

        var exact = Locales.FirstOrDefault(locale => string.Equals(id, locale.Id, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var family = id?.Split('-')[0];
        if (string.Equals(family, "zh", StringComparison.OrdinalIgnoreCase))
            family = "zh-Hans";
        return Locales.FirstOrDefault(locale => string.Equals(family, locale.Id, StringComparison.OrdinalIgnoreCase))
            ?? Locales[0];
    }

    private static Dictionary<string, string> Load(string id)
    {
        using var input = typeof(TextCatalog).Assembly.GetManifestResourceStream($"VMCrate.Core.Locales.{id}.json")
            ?? throw new InvalidOperationException($"Missing locale {id}.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(input, JsonDefaults.Options)
            ?? throw new InvalidOperationException($"Empty locale {id}.");
    }
}
