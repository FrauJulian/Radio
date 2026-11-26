using Radiosender.Models;
using Radiosender.Services.Library;
using Radiosender.Services.Localization;

namespace Radiosender.Controllers;

public class UploadController(SongImportService importService)
{
    public static string FileDialogFilter => Loc.T("Import_FileFilter");

    // Never throws: batch continues with next file.
    public UploadResult Upload(string filePath)
    {
        try
        {
            var song = importService.Import(filePath);
            return new UploadResult(true, Loc.F("Import_FileAdded", song.Title, $"{(int)song.Duration.TotalMinutes}:{song.Duration.Seconds:00}"), song);
        }
        catch (SongImportException e)
        {
            return new UploadResult(false, e.Message);
        }
        catch (Exception e)
        {
            return new UploadResult(false, Loc.F("Import_SaveFailed", e.Message));
        }
    }
}
