using System.Windows;

namespace ContextFlow.Services;

/// <summary>
/// Swaps the UI string dictionary at runtime. XAML uses DynamicResource keys;
/// view-model/code strings use Get/Format so both stay in sync.
/// </summary>
public sealed class LocalizationService
{
    public const string English = "en";
    public const string Spanish = "es";

    public string Language { get; private set; } = English;

    public event EventHandler? LanguageChanged;

    public void Apply(string? language)
    {
        var normalized = string.Equals(language, Spanish, StringComparison.OrdinalIgnoreCase)
            ? Spanish
            : English;

        var application = Application.Current;
        if (application is null)
        {
            Language = normalized;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                normalized == Spanish ? "Themes/Strings.es.xaml" : "Themes/Strings.en.xaml",
                UriKind.Relative)
        };

        var existingIndex = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is null)
            {
                continue;
            }

            if (source.EndsWith("Themes/Strings.en.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Strings.es.xaml", StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            dictionaries[existingIndex] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        Language = normalized;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string Get(string key)
    {
        var value = Application.Current?.TryFindResource(key);
        return value as string ?? key;
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(Get(key), arguments);
}
