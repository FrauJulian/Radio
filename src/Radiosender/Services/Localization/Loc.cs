using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Radiosender.Services.Localization;

// UI texts from Strings.resx (German, default) and Strings.en.resx.
// XAML binds to the indexer ({l:Tr Key}), so a language switch updates open views.
public sealed class Loc : INotifyPropertyChanged
{
    public const string German = "de";
    public const string English = "en";

    private static readonly ResourceManager Texts = new($"{typeof(Loc).Namespace}.Strings", typeof(Loc).Assembly);

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    public static CultureInfo Culture { get; private set; } = CultureFor(German);

    public static string Language => Culture.TwoLetterISOLanguageName;

    // Views refresh texts they set in code.
    public static event EventHandler? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => T(key);

    // Missing key shows as #Key instead of crashing.
    public static string T(string key) => Texts.GetString(key, Culture) ?? $"#{key}";

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    public static bool IsSupported(string? language) => language is German or English;

    public static void SetLanguage(string language)
    {
        var culture = CultureFor(IsSupported(language) ? language : German);
        if (culture.Name == Culture.Name)
            return;
        Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static CultureInfo CultureFor(string language) => CultureInfo.GetCultureInfo(language == English ? "en-US" : "de-DE");
}
