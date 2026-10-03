using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IPA.Utilities.Async;
using SiraUtil.Logging;
using Synapse.Extras;
using Synapse.Networking.Models;
using UnityEngine;
using UnityEngine.Audio;
using Zenject;
using Object = UnityEngine.Object;

namespace Synapse.Managers;

internal class CountdownManager : ITickable, IInitializable, IDisposable
{
    private const int ClipCount = 5;

    private static readonly string _folder = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(Application.streamingAssetsPath) ?? throw new InvalidOperationException(),
        "Synapse",
        "Countdown"));

    private readonly AudioClipAsyncLoader _audioClipAsyncLoader;
    private readonly Task _audioLoad;
    private readonly AudioClip[] _audioClips = new AudioClip[ClipCount];
    private readonly string?[] _audioPaths = new string?[ClipCount];
    private readonly SiraLog _log;
    private readonly NetworkManager _networkManager;
    private readonly RainbowString _rainbowString;
    private readonly SongPreviewPlayer _songPreviewPlayer;
    private readonly TimeSyncManager _timeSyncManager;
    private GameObject? _audioObject;
    private AudioSource _countAudioSource = null!;
    private AudioSource _gongAudioSource = null!;

    private bool _disposed;
    private bool _gongPlayed;

    private int _lastPlayed;
    private string _lastSent = string.Empty;
    private bool _levelStarted;

    private float _startTime;

    private CountdownManager(
        SiraLog log,
        NetworkManager networkManager,
        TimeSyncManager timeSyncManager,
        AudioClipAsyncLoader audioClipAsyncLoader,
        SongPreviewPlayer songPreviewPlayer,
        RainbowString rainbowString)
    {
        _log = log;
        _networkManager = networkManager;
        _timeSyncManager = timeSyncManager;
        _songPreviewPlayer = songPreviewPlayer;
        _rainbowString = rainbowString;
        _audioClipAsyncLoader = audioClipAsyncLoader;
        networkManager.StartTimeUpdated += OnStartTimeUpdated;
        networkManager.Closed += OnClosed;

        _audioLoad = UnityMainThreadTaskScheduler.Factory.StartNew(LoadAudio).Unwrap();
    }

    internal event Action<string>? CountdownUpdated;

    internal event Action? LevelStarted;

    private float StartTime
    {
        get => _startTime;
        set
        {
            _gongPlayed = false;
            _lastPlayed = -1;
            _startTime = value;
            _levelStarted = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _networkManager.StartTimeUpdated -= OnStartTimeUpdated;
        _networkManager.Closed -= OnClosed;
        if (_countAudioSource)
        {
            _countAudioSource.Stop();
            _countAudioSource.clip = null;
        }

        if (_gongAudioSource)
        {
            _gongAudioSource.Stop();
            _gongAudioSource.clip = null;
        }

        if (_audioObject)
        {
            Object.Destroy(_audioObject);
            _audioObject = null;
        }

        for (int i = 0; i < ClipCount; i++)
        {
            string? path = _audioPaths[i];
            if (path != null)
            {
                _audioPaths[i] = null;
                _audioClips[i] = null!;
                _audioClipAsyncLoader.Unload(path);
            }
        }
    }

    public void Initialize()
    {
        if (_disposed)
        {
            return;
        }

        AudioMixerGroup outputAudioMixerGroup = _songPreviewPlayer._audioSourcePrefab.outputAudioMixerGroup;
        GameObject gameObject = new("CountdownAudio");
        _audioObject = gameObject;
        Object.DontDestroyOnLoad(gameObject);
        AudioSource gong = gameObject.AddComponent<AudioSource>();
        gong.outputAudioMixerGroup = outputAudioMixerGroup;
        gong.volume = 0.4f;
        gong.playOnAwake = false;
        gong.bypassEffects = true;
        gong.clip = Resources.FindObjectsOfTypeAll<CountdownController>().First()._audioSource.clip;
        _gongAudioSource = gong;
        AudioSource count = gameObject.AddComponent<AudioSource>();
        count.outputAudioMixerGroup = outputAudioMixerGroup;
        count.volume = 0.4f;
        count.playOnAwake = false;
        count.bypassEffects = true;
        _countAudioSource = count;
    }

    public void Tick()
    {
        if (_disposed || _networkManager.Status.Stage is not PlayStatus playStatus)
        {
            return;
        }

        if (_levelStarted)
        {
            Send($"<size=160%>{_rainbowString}");
            return;
        }

        TimeSpan diff = (StartTime - _timeSyncManager.SyncTime).ToTimeSpan();
        TimeSpan alteredDiff = diff + TimeSpan.FromSeconds(1);
        if ((int)alteredDiff.TotalHours > 0)
        {
            Send("Soon™");
        }
        else if ((int)alteredDiff.TotalMinutes > 0)
        {
            Send($"{alteredDiff.Minutes}:{alteredDiff.Seconds:D2}");
        }
        else if ((int)alteredDiff.TotalSeconds > 10)
        {
            Send($"{alteredDiff.Seconds}");
        }
        else if (diff.TotalSeconds > 0)
        {
            if (!_gongPlayed)
            {
                if (_gongAudioSource)
                {
                    _gongAudioSource.Play();
                }

                _gongPlayed = true;
            }

            int count = alteredDiff.Seconds - 1;
            if (count >= 0 && count < _audioClips.Length && _lastPlayed != count)
            {
                _lastPlayed = count;
                if (_countAudioSource)
                {
                    _countAudioSource.clip = _audioClips[count];
                    if (_audioClips[count])
                    {
                        _countAudioSource.Play();
                    }
                }
            }

            _rainbowString.SetString(alteredDiff.Seconds.ToString());
            Send($"<size=160%>{_rainbowString}");
        }
        else
        {
            _levelStarted = true;
            if (playStatus.PlayerScore == null)
            {
                NotifyLevelStarted();
            }

            if (_disposed)
            {
                return;
            }

            _rainbowString.SetString("Now");
            Send($"<size=160%>{_rainbowString}");
        }
    }

    internal void ManualStart()
    {
        if (_disposed)
        {
            return;
        }

        _levelStarted = true;
        NotifyLevelStarted();
    }

    internal void Refresh()
    {
        _lastSent = string.Empty;
    }

    private async Task LoadAudio()
    {
        try
        {
            const string prefix = "Synapse.Resources.Countdown.";
            Assembly assembly = typeof(CountdownManager).Assembly;
            for (int i = 0; i < ClipCount && !_disposed; i++)
            {
                string fileName = $"{i + 1}.ogg";
                string audio = $"{prefix}{fileName}";
                string path = await CountdownFileWorker.Prepare(assembly, audio, _folder, fileName);
                if (_disposed)
                {
                    return;
                }

                AudioClip clip = await _audioClipAsyncLoader.Load(path);
                if (_disposed)
                {
                    _audioClipAsyncLoader.Unload(path);
                    return;
                }

                _audioPaths[i] = path;
                _audioClips[i] = clip;
            }
        }
        catch (Exception e)
        {
            if (!_disposed)
            {
                _log.Error($"Exception while loading countdown audio\n{e}");
            }
        }
    }

    private void OnClosed()
    {
        StartTime = float.MaxValue;
    }

    private void OnStartTimeUpdated(float startTime)
    {
        StartTime = startTime;
    }

    private void Send(string text)
    {
        if (_disposed || text == _lastSent)
        {
            return;
        }

        _lastSent = text;
        Action<string>? updated = CountdownUpdated;
        if (updated == null)
        {
            return;
        }

        foreach (Action<string> handler in updated.GetInvocationList())
        {
            if (_disposed)
            {
                return;
            }

            handler(text);
        }
    }

    private void NotifyLevelStarted()
    {
        Action? started = LevelStarted;
        if (_disposed || started == null)
        {
            return;
        }

        foreach (Action handler in started.GetInvocationList())
        {
            if (_disposed)
            {
                return;
            }

            handler();
        }
    }
}
