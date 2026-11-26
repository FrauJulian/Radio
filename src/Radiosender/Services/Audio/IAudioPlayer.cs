namespace Radiosender.Services.Audio;

public interface IAudioPlayer
{
    event EventHandler? TrackFinished;

    void Play(string filePath);

    void Stop();
}
