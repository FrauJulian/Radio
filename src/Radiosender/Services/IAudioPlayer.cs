namespace Radiosender.Services;

/// <summary>The part of <see cref="MusicPlayer"/> the playback engine needs; lets tests run without audio.</summary>
public interface IAudioPlayer
{
    event EventHandler? TrackFinished;

    void Play(string filePath);

    void Stop();
}
