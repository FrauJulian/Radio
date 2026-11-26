namespace Radiosender.Services.Providers;

public class MusicProviderException(string message, Exception? inner = null) : Exception(message, inner);
