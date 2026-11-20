using System.Collections.Generic;
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
/// makes DynamicResource re-resolve without recreating any window.
/// </summary>
public static class Localizer
{
    public const string English = "en";
    public const string Chinese = "zh";
    public const string DefaultLanguage = English;

    private static readonly string[] KnownLanguages = [English, Chinese];
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);

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
    /// Localised text by key. A missing key returns "!key!" on purpose: it shows in
    /// the UI and the test suite asserts it never happens.
    /// </summary>
    public static string Get(string key) => Values.TryGetValue(key, out var value) ? value : $"!{key}!";

    public static bool HasKey(string key) => Values.ContainsKey(key);

    private static void Load(string culture)
    {
        var uri = new Uri($"avares://CogniCustos.Ui/Resources/Lang.{culture}.axaml");
        var dictionary = (ResourceDictionary)AvaloniaXamlLoader.Load(uri);

        Values.Clear();
        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is string text) Values[name] = text;
        }

        SwapApplicationDictionary(dictionary);
    }

    private static void SwapApplicationDictionary(ResourceDictionary dictionary)
    {
        if (_app is null) return;

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
