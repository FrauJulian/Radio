using System.Net.Http;
using NAudio.Wave;
using Radiosender.Services.Audio;
using Radiosender.Services.Settings;
using Radiosender.Services.Streaming;

namespace Radiosender.Controllers;

public class LiveController
{
    private readonly ScheduleController _schedule;
    private readonly SettingsStore _settings;
    private readonly ProgramOutput _output;
    private readonly HttpClient _http;
    private readonly StreamTitles _titles;
    private readonly Lock _lock = new();
    private IcecastStream? _stream;
    private BufferedWaveProvider? _feed;
    private IcecastTarget? _target;

    public LiveController(ScheduleController schedule, SettingsStore settings, ProgramOutput output, HttpClient http, Func<DateTime> now)
    {
        _schedule = schedule;
        _settings = settings;
        _output = output;
        _http = http;
        _titles = new StreamTitles(now);
        _schedule.ItemStarted += (_, _) => SendCurrentTitle();
    }

    public bool IsEnabled => _settings.Current.LautFmEnabled;

    public bool IsLive => _stream is not null;

    // Background thread.
    public event EventHandler<string>? Problem;

    /// <exception cref="LiveStreamException">laut.fm off, unreachable, or login refused.</exception>
    /// <exception cref="NAudio.MmException">No audio output device.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsLive)
            return;
        var settings = _settings.Current;
        if (!settings.LautFmEnabled)
            throw new LiveStreamException("laut.fm ist in den Einstellungen ausgeschaltet.");
        var target = IcecastTarget.From(settings);
        var feed = _output.OpenLiveFeed();
        IcecastStream stream;
        try
        {
            stream = await IcecastStream.ConnectAsync(target, feed, cancellationToken);
        }
        catch
        {
            _output.CloseLiveFeed(feed);
            throw;
        }
        stream.Failed += OnFailed;
        lock (_lock)
        {
            _stream = stream;
            _feed = feed;
            _target = target;
        }
        SendCurrentTitle();
    }

    public void Stop() => Stop(null);

    // only != null: stop only if still current stream.
    private void Stop(IcecastStream? only)
    {
        IcecastStream stream;
        BufferedWaveProvider feed;
        lock (_lock)
        {
            if (_stream is null || _feed is null || (only is not null && _stream != only))
                return;
            (stream, feed) = (_stream, _feed);
            (_stream, _feed, _target) = (null, null, null);
        }
        stream.Failed -= OnFailed;
        stream.Dispose();
        _output.CloseLiveFeed(feed);
    }

    private void OnFailed(object? sender, string message)
    {
        Stop(sender as IcecastStream);
        Problem?.Invoke(this, message);
    }

    private async void SendCurrentTitle()
    {
        IcecastTarget target;
        string title;
        lock (_lock)
        {
            if (_target is null || _schedule.CurrentItem is not { } item)
                return;
            target = _target;
            title = _titles.For(item);
        }
        try
        {
            await IcecastStream.UpdateTitleAsync(_http, target, title);
        }
        catch (LiveStreamException ex)
        {
            Problem?.Invoke(this, ex.Message);
        }
    }
}
