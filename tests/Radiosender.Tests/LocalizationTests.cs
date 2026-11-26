using System.Globalization;
using System.IO;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Radiosender.Models;
using Radiosender.Services.Localization;

namespace Radiosender.Tests;

// Static checks only: switching Loc here would race with tests that assert German messages.
public sealed partial class LocalizationTests
{
    private static readonly string AppFolder = FindAppFolder();
    private static readonly string StringsFolder = Path.Combine(AppFolder, "Services", "Localization");

    // From this source file, not the test output: tests may build to another folder.
    private static string FindAppFolder([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
        {
            var app = Path.Combine(dir.FullName, "src", "Radiosender");
            if (Directory.Exists(app))
                return app;
        }
        throw new DirectoryNotFoundException("src/Radiosender not found above the test sources.");
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(StringsFolder, file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    [Fact]
    public void GermanAndEnglish_HaveTheSameKeysAndPlaceholders()
    {
        var german = Read("Strings.resx");
        var english = Read("Strings.en.resx");

        Assert.Equal(german.Keys.Order(), english.Keys.Order());
        Assert.All(german, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(english[entry.Key]), entry.Key);
            Assert.Equal(Placeholders(entry.Value), Placeholders(english[entry.Key]));
        });
    }

    [Fact]
    public void EveryKeyUsedInCode_Exists()
    {
        var keys = Read("Strings.resx").Keys.ToHashSet();
        var used = Directory.EnumerateFiles(AppFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => UsedKey().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups["key"].Value)));
        var kinds = Enum.GetNames<ScheduleItemKind>().Select(k => (File: "ScheduleItem.cs", Key: $"Kind_{k}"));

        // "Key" is the placeholder in doc comments of Loc and TrExtension.
        var missing = used.Concat(kinds).Where(u => u.Key != "Key" && !keys.Contains(u.Key)).ToList();

        Assert.True(missing.Count == 0, $"Missing keys: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EnglishSatellite_IsFound()
    {
        var texts = new ResourceManager($"{typeof(Loc).Namespace}.Strings", typeof(Loc).Assembly);

        Assert.Equal("Language", texts.GetString("Settings_Language", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Sprache", texts.GetString("Settings_Language", CultureInfo.GetCultureInfo("de-DE")));
    }

    private static string[] Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{\d+[^}]*\}").Select(m => m.Value).Distinct().Order()];

    [GeneratedRegex("""(?:Loc\.[TF]\(\s*"|\{l:Tr\s+)(?<key>[A-Za-z0-9_]+)""")]
    private static partial Regex UsedKey();
}
