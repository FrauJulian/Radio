using Radiosender.Services;

namespace Radiosender.Controllers;

public record UploadResult(bool Success, string Message);

public class UploadController(SongImportService importService)
{
    public static string FileDialogFilter { get; } = "Audio files (*.mp3;*.wav)|*.mp3;*.wav";

    public UploadResult Upload(string filePath)
    {
        try
        {
            var song = importService.Import(filePath);
            return new UploadResult(true, $"\"{song.Title}\" ({(int)song.Duration.TotalMinutes}:{song.Duration.Seconds:00}) was added to the library.");
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
