using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Controllers;

/// <param name="Song">The saved song, or <c>null</c> when the upload failed.</param>
public record UploadResult(bool Success, string Message, Song? Song = null);

public class UploadController(SongImportService importService)
{
    public static string FileDialogFilter { get; } = "Audio files (*.mp3;*.wav)|*.mp3;*.wav";

    /// <summary>Imports one file; failures come back as a result, so a batch can go on with the next file.</summary>
    public UploadResult Upload(string filePath)
    {
        try
        {
            var song = importService.Import(filePath);
            return new UploadResult(true, $"\"{song.Title}\" ({(int)song.Duration.TotalMinutes}:{song.Duration.Seconds:00}) was added to the library.", song);
        }
        catch (SongImportException e)
        {
            return new UploadResult(false, e.Message);
        }
        catch (Exception e)
        {
            return new UploadResult(false, $"The song could not be saved: {e.Message}");
        }
    }
}
