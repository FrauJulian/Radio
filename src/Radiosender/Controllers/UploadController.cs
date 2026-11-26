using Radiosender.Models;
using Radiosender.Services.Library;

namespace Radiosender.Controllers;

public class UploadController(SongImportService importService)
{
    public static string FileDialogFilter { get; } = "Audiodateien (*.mp3;*.wav)|*.mp3;*.wav";

    // Never throws: batch continues with next file.
    public UploadResult Upload(string filePath)
    {
        try
        {
            var song = importService.Import(filePath);
            return new UploadResult(true, $"\"{song.Title}\" ({(int)song.Duration.TotalMinutes}:{song.Duration.Seconds:00}) wurde zur Bibliothek hinzugefügt.", song);
        }
        catch (SongImportException e)
        {
            return new UploadResult(false, e.Message);
        }
        catch (Exception e)
        {
            return new UploadResult(false, $"Der Song konnte nicht gespeichert werden: {e.Message}");
        }
    }
}
