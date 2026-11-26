using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Radiosender.Models;

namespace Radiosender.Services.Providers;

// Search returns items (often albums); each item's file list is fetched, every MP3 is one result.
public partial class InternetArchiveProvider(HttpClient http) : IMusicProvider
{
    private const string SearchUrl = "https://archive.org/advancedsearch.php";
    private const string MetadataUrl = "https://archive.org/metadata/";

    // One extra request per item: keep small.
    private const int Items = 8;
    private const int TracksPerItem = 5;

    public string Name => "Internet Archive";

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        // Strip Lucene operators: user input must not change the filter.
        var words = string.Join(' ', LuceneSyntax().Replace(query, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (words.Length == 0)
            return [];
        var filter = $"({words}) AND mediatype:audio AND licenseurl:*creativecommons* AND format:(MP3)";
        var url = $"{SearchUrl}?q={Uri.EscapeDataString(filter)}&fl[]=identifier&fl[]=title&fl[]=creator&fl[]=licenseurl&rows={Items}&output=json";

        using var json = await ProviderParsing.GetJsonAsync(http, url, Name, cancellationToken);
        if (!json.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            throw new MusicProviderException($"{Name}-Suche fehlgeschlagen: unerwartete Antwort.");

        var perItem = await Task.WhenAll(docs.EnumerateArray().Select(doc => TracksOfAsync(doc, cancellationToken)));
        return [.. perItem.SelectMany(tracks => tracks)];
    }

    private async Task<List<MusicTrack>> TracksOfAsync(JsonElement item, CancellationToken cancellationToken)
    {
        List<MusicTrack> tracks = [];
        if (ProviderParsing.Text(item, "identifier") is not { } identifier)
            return tracks;

        JsonDocument metadata;
        try
        {
            metadata = await ProviderParsing.GetJsonAsync(http, MetadataUrl + Uri.EscapeDataString(identifier), Name, cancellationToken);
        }
        catch (MusicProviderException)
        {
            return tracks; // one unreachable item must not fail the whole search
        }

        using (metadata)
        {
            if (!metadata.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return tracks;
            var license = ProviderParsing.License(ProviderParsing.Text(item, "licenseurl")) ?? "";
            foreach (var file in files.EnumerateArray())
            {
                if (tracks.Count == TracksPerItem)
                    break;
                if (ProviderParsing.Text(file, "name") is not { } name
                    || ProviderParsing.Text(file, "format")?.Contains("MP3", StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                var title = ProviderParsing.Text(file, "title") ?? Path.GetFileNameWithoutExtension(name);
                var creator = ProviderParsing.Text(file, "creator") ?? ProviderParsing.Text(file, "artist") ?? ProviderParsing.Text(item, "creator");
                // Name may contain folders: escape each segment.
                var path = string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
                tracks.Add(new MusicTrack
                {
                    Id = $"archive:{identifier}/{name}",
                    Title = title,
                    Creator = creator,
                    Url = $"https://archive.org/download/{Uri.EscapeDataString(identifier)}/{path}",
                    FileType = "mp3",
                    License = license,
                    Attribution = $"\"{title}\" by {creator ?? "Unknown"} (https://archive.org/details/{identifier}) is licensed under {license}.",
                    Duration = ProviderParsing.Duration(ProviderParsing.Text(file, "length")),
                });
            }
        }
        return tracks;
    }

    [GeneratedRegex("""[^\p{L}\p{N}\s]|\b(AND|OR|NOT|TO)\b""")]
    private static partial Regex LuceneSyntax();
}
