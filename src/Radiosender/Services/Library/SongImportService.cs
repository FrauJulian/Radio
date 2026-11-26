using System.IO;
using NAudio.Wave;
using Radiosender.Models;

namespace Radiosender.Services.Library;

public class SongImportService(SongRepository songs, string musicDirectory)
{
    public static string DefaultMusicDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radiosender", "Music");

    private static IReadOnlyList<string> SupportedExtensions { get; } = [".mp3", ".wav"];

    /// <exception cref="SongImportException">The file is not a readable MP3 or WAV file with audio.</exception>
    public Song Import(string sourcePath)
    {
        if (!SupportedExtensions.Contains(Path.GetExtension(sourcePath).ToLowerInvariant()))
            throw new SongImportException("Nur MP3- und WAV-Dateien werden unterstützt.");

        Directory.CreateDirectory(musicDirectory);
        var targetPath = GetUniqueTargetPath(Path.GetFileName(sourcePath));
        try
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SongImportException($"Die Datei konnte nicht gelesen werden: {e.Message}", e);
        }

        try
        {
            var duration = ReadDuration(targetPath);
            var (title, artist) = ReadTags(targetPath);
            var song = new Song
            {
                Title = title ?? Path.GetFileNameWithoutExtension(sourcePath),
                Artist = artist ?? "",
                Duration = duration,
                FilePath = targetPath,
                Source = SongSource.Upload,
            };
            songs.Add(song);
            return song;
        }
        catch
        {
            File.Delete(targetPath);
            throw;
        }
    }

    private string GetUniqueTargetPath(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var path = Path.Combine(musicDirectory, fileName);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(musicDirectory, $"{name} ({i}){extension}");
        return path;
    }

    private static TimeSpan ReadDuration(string path)
    {
        TimeSpan duration;
        try
        {
            using var reader = new AudioFileReader(path);
            duration = reader.TotalTime;
        }
        catch (Exception e)
        {
            // NAudio/Media Foundation throw many unrelated types on corrupt files.
            throw new SongImportException("Die Datei ist keine lesbare MP3- oder WAV-Datei.", e);
        }

        if (duration <= TimeSpan.Zero)
            throw new SongImportException("Die Datei enthält kein Audio.");
        return duration;
    }

    private static (string? Title, string? Artist) ReadTags(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return (NullIfBlank(file.Tag.Title), NullIfBlank(file.Tag.FirstPerformer));
        }
        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException)
        {
            return (null, null); // tags are optional; duration reading decides if the file is valid
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
