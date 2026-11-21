using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;

namespace CogniCustos.Ui.Localization;

/// <summary>
/// Runtime language switching.
///
/// The text itself lives in <c>Resources/Lang.&lt;culture&gt;.axaml</c> as a
/// ResourceDictionary, so adding a language is "copy one file, translate the values".
/// Two consumers are served from that single file:
///   * XAML uses <c>{DynamicResource Some.Key}</c>;
///   * view-models use <see cref="Get(string)"/> (a plain dictionary snapshot).
/// Switching replaces the language dictionary inside Application.Resources, which
/// makes DynamicResource re-resolve without recreating any window. Windows with
/// view-model text subscribe to <see cref="LanguageChanged"/> (see LocalizedWindow).
///
/// A key missing from the current language falls back to English, so a half-finished
/// translation never leaves a blank control; the test suite still fails on gaps.
/// </summary>
public static class Localizer
{
    public const string English = "en";
    public const string Chinese = "zh";
    public const string DefaultLanguage = English;

    private const string WindowTitleKey = "Window.Main.Title";

    private static readonly string[] KnownLanguages = [English, Chinese];
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Fallbacks = new(StringComparer.Ordinal);

    private static Application? _app;
    private static int _languageIndex = -1;
    private static string _current = DefaultLanguage;

    public static string Current => _current;

    public static IReadOnlyList<string> Languages => KnownLanguages;

    public static event EventHandler? LanguageChanged;

    /// <summary>Called once from App.Initialize; records where the language dictionary sits.</summary>
    public static void Attach(Application app)
    {
        _app = app;

        var merged = app.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is ResourceInclude include &&
                include.Source is { } source &&
                source.OriginalString.Contains("/Resources/Lang.", StringComparison.OrdinalIgnoreCase))
            {
                _languageIndex = i;
                break;
            }
        }

        // English is the fallback for every other language.
        LoadInto(DefaultLanguage, Fallbacks);
        Load(_current);
    }

    public static void SetLanguage(string culture)
    {
        if (culture == _current) return;
        Load(culture);
        _current = culture;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string NextLanguage() => _current == Chinese ? English : Chinese;

    /// <summary>
    /// Localised text by key. Missing in this language → English; missing everywhere →
    /// "!key!" so it is visible and the tests can catch it.
    /// </summary>
    public static string Get(string key)
    {
        if (Values.TryGetValue(key, out var value)) return value;
        if (Fallbacks.TryGetValue(key, out var fallback))
        {
            Debug.WriteLine($"[i18n] '{key}' is missing in '{_current}', falling back to '{DefaultLanguage}'");
            return fallback;
        }

        Debug.WriteLine($"[i18n] '{key}' is missing everywhere");
        return $"!{key}!";
    }

    public static bool HasKey(string key) => Values.ContainsKey(key);

    /// <summary>Short code shown on the language picker, e.g. "EN" / "中".</summary>
    public static string Code(string culture) => Get(culture == Chinese ? "Lang.Code.Zh" : "Lang.Code.En");

    /// <summary>Full name shown in the language menu, e.g. "English" / "中文".</summary>
    public static string DisplayName(string culture) => Get(culture == Chinese ? "Lang.Chinese" : "Lang.English");

    public static string WindowTitle => Get(WindowTitleKey);

    private static void Load(string culture)
    {
        LoadInto(culture, Values);
        SwapApplicationDictionary(culture);
    }

    private static void LoadInto(string culture, Dictionary<string, string> target)
    {
        var dictionary = LoadDictionary(culture);

        target.Clear();
        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is string text) target[name] = text;
        }
    }

    private static ResourceDictionary LoadDictionary(string culture)
    {
        var uri = new Uri($"avares://CogniCustos.Ui/Resources/Lang.{culture}.axaml");
        return (ResourceDictionary)AvaloniaXamlLoader.Load(uri);
    }

    private static void SwapApplicationDictionary(string culture)
    {
        if (_app is null) return;

        var dictionary = LoadDictionary(culture);
        var merged = _app.Resources.MergedDictionaries;

        if (_languageIndex >= 0 && _languageIndex < merged.Count)
        {
            merged[_languageIndex] = dictionary;
            return;
        }

        merged.Add(dictionary);
        _languageIndex = merged.Count - 1;
    }
}
