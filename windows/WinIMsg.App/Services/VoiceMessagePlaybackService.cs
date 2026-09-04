using Windows.Media.Core;
using Windows.Media.Playback;

namespace WinIMsg.App.Services;

public sealed record VoicePlaybackState(string? Key, bool IsPlaying, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// One app-wide audio player for voice-message chips: starting one voice
/// message stops the previous one, and no XAML element ever owns a
/// MediaPlayer (which avoids the MediaPlayerElement teardown fail-fast
/// entirely for audio). State events fire on media-thread callbacks;
/// subscribers marshal to their dispatcher.
/// </summary>
public sealed class VoiceMessagePlaybackService
{
    public static VoiceMessagePlaybackService Instance { get; } = new();

    private readonly object _sync = new();
    private MediaPlayer? _player;
    private string? _currentKey;

    public event EventHandler<VoicePlaybackState>? StateChanged;

    public string? CurrentKey
    {
        get
        {
            lock (_sync)
            {
                return _currentKey;
            }
        }
    }

    public VoicePlaybackState CurrentState()
    {
        lock (_sync)
        {
            if (_player is null || _currentKey is null)
            {
                return new VoicePlaybackState(null, false, TimeSpan.Zero, TimeSpan.Zero);
            }

            var session = _player.PlaybackSession;
            return new VoicePlaybackState(
                _currentKey,
                session.PlaybackState == MediaPlaybackState.Playing,
                session.Position,
                session.NaturalDuration);
        }
    }

    public void TogglePlayback(string key, string filePath)
    {
        string? stoppedKey = null;
        lock (_sync)
        {
            var player = EnsurePlayer();
            if (string.Equals(_currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    player.Pause();
                }
                else
                {
                    player.Play();
                }

                return;
            }

            stoppedKey = _currentKey;
            _currentKey = key;
            player.Source = MediaSource.CreateFromUri(new Uri(filePath));
            player.Play();
        }

        if (stoppedKey is not null)
        {
            StateChanged?.Invoke(this, new VoicePlaybackState(stoppedKey, false, TimeSpan.Zero, TimeSpan.Zero));
        }
    }

    public void Stop()
    {
        string? stoppedKey;
        lock (_sync)
        {
            stoppedKey = _currentKey;
            _currentKey = null;
            if (_player is not null)
            {
                _player.Pause();
                _player.Source = null;
            }
        }

        if (stoppedKey is not null)
        {
            StateChanged?.Invoke(this, new VoicePlaybackState(stoppedKey, false, TimeSpan.Zero, TimeSpan.Zero));
        }
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_player is not null)
        {
            return _player;
        }

        var player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Speech
        };
        player.MediaEnded += (_, _) =>
        {
            string? endedKey;
            TimeSpan duration;
            lock (_sync)
            {
                endedKey = _currentKey;
                _currentKey = null;
                duration = player.PlaybackSession.NaturalDuration;
                player.Source = null;
            }

            if (endedKey is not null)
            {
                StateChanged?.Invoke(this, new VoicePlaybackState(endedKey, false, TimeSpan.Zero, duration));
            }
        };
        player.MediaFailed += (_, _) =>
        {
            string? failedKey;
            lock (_sync)
            {
                failedKey = _currentKey;
                _currentKey = null;
                player.Source = null;
            }

            if (failedKey is not null)
            {
                StateChanged?.Invoke(this, new VoicePlaybackState(failedKey, false, TimeSpan.Zero, TimeSpan.Zero));
            }
        };
        player.PlaybackSession.PositionChanged += (session, _) => RaiseSessionState(session);
        player.PlaybackSession.PlaybackStateChanged += (session, _) => RaiseSessionState(session);

        _player = player;
        return player;
    }

    private void RaiseSessionState(MediaPlaybackSession session)
    {
        string? key;
        lock (_sync)
        {
            key = _currentKey;
        }

        if (key is null)
        {
            return;
        }

        StateChanged?.Invoke(this, new VoicePlaybackState(
            key,
            session.PlaybackState == MediaPlaybackState.Playing,
            session.Position,
            session.NaturalDuration));
    }
}
