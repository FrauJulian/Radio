namespace Radiosender.Services.Streaming;

/// <summary>Message is shown to the user as is.</summary>
public class LiveStreamException(string message, Exception? inner = null) : Exception(message, inner);
