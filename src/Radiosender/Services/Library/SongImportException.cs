namespace Radiosender.Services.Library;

public class SongImportException(string message, Exception? innerException = null) : Exception(message, innerException);
