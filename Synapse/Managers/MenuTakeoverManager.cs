using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Controllers;
using Synapse.Extras;
using Synapse.Networking.Models;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using Zenject;
using Object = UnityEngine.Object;

namespace Synapse.Managers;

internal class MenuTakeoverManager : IDisposable, ITickable
{
    private static readonly string _takeoverFolder =
        (Path.GetDirectoryName(Application.streamingAssetsPath) ?? throw new InvalidOperationException()) +
        $"{Path.DirectorySeparatorChar}Synapse{Path.DirectorySeparatorChar}TakeoverBundles";

    private readonly SiraLog _log;
    private readonly Config _config;
    private readonly IInstantiator _instantiator;
    private readonly ListingManager _listingManager;
    private readonly GlobalParticleManager.ParticleHold _particleHold;
    private readonly CancellationTokenManager _cancellationTokenManager;

    private readonly GameObject[] _menuLogo;

    private BundleInfo? _bundleInfo;
    private uint? _lastHash;
    private GameObject? _prefab;
    private bool _enabled;
    private bool _disposed;
    private long _revision;
    private Task _pipeline = Task.CompletedTask;

    private DateTime _startTime;
    private TextMeshPro? _countdownText;
    private bool _disableDust;
    private bool _disableLogo;

    [UsedImplicitly]
    private MenuTakeoverManager(
        SiraLog log,
        Config config,
        IInstantiator instantiator,
        ListingManager listingManager,
        MenuEnvironmentManager menuEnvironmentManager,
        GlobalParticleManager.ParticleHold particleHold,
        CancellationTokenManager cancellationTokenManager)
    {
        _log = log;
        _config = config;
        _instantiator = instantiator;
        _listingManager = listingManager;
        _particleHold = particleHold;
        _cancellationTokenManager = cancellationTokenManager;

        GameObject? menu = menuEnvironmentManager._data
            .FirstOrDefault(n => n.menuEnvironmentType == MenuEnvironmentManager.MenuEnvironmentType.Default)
            ?.wrapper;
        if (menu != null)
        {
            List<GameObject> logoObjects = [];
            Transform menuTransform = menu.transform;
            logoObjects.AddRange(
                from Transform childTransform in menuTransform
                where childTransform.name.Contains("Logo") || childTransform.name.StartsWith("GlowLines")
                select childTransform.gameObject);

            _menuLogo = logoObjects.ToArray();
        }
        else
        {
            _menuLogo = [];
        }

        config.Updated += OnConfigUpdated;
        listingManager.ListingFound += OnListingFound;
    }

    internal bool Enabled
    {
        ////get => _enabled;
        set
        {
            if (_disposed || _enabled == value)
            {
                return;
            }

            _enabled = value;
            Refresh();
        }
    }

    public void Tick()
    {
        if (_disposed || _countdownText == null)
        {
            return;
        }

        TimeSpan span = _startTime.ToTimeSpan();
        if (span.Ticks < 0)
        {
            span = TimeSpan.Zero;
        }

        string timeText = $"{span.Days:D2}:{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
        _countdownText.text = timeText;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _config.Updated -= OnConfigUpdated;
        _listingManager.ListingFound -= OnListingFound;
        Reset();
    }

    private void Refresh()
    {
        long revision = _revision;
        GameObject? prefab = _prefab;
        bool doEnable = _enabled && !_config.DisableMenuTakeover;
        if (_disposed || prefab == null)
        {
            return;
        }

        prefab.SetActive(doEnable);
        if (!IsCurrent(revision) || prefab != _prefab || doEnable != (_enabled && !_config.DisableMenuTakeover))
        {
            return;
        }

        _particleHold.DustDisabled = doEnable && _disableDust;

        foreach (GameObject gameObject in _menuLogo)
        {
            if (!IsCurrent(revision) || prefab != _prefab || doEnable != (_enabled && !_config.DisableMenuTakeover))
            {
                return;
            }

            if (gameObject != null)
            {
                gameObject.SetActive(!(doEnable && _disableLogo));
            }
        }
    }

    private void OnConfigUpdated()
    {
        Refresh();
    }

    private bool IsCurrent(long revision)
    {
        return !_disposed && revision == _revision;
    }

    private long Reset()
    {
        long revision = ++_revision;
        GameObject? prefab = _prefab;
        _prefab = null;
        _countdownText = null;
        try
        {
            _cancellationTokenManager.Cancel();
        }
        finally
        {
            try
            {
                if (prefab != null)
                {
                    Object.Destroy(prefab);
                }
            }
            finally
            {
                if (revision == _revision)
                {
                    _particleHold.DustDisabled = false;
                    foreach (GameObject gameObject in _menuLogo)
                    {
                        if (revision != _revision)
                        {
                            break;
                        }

                        if (gameObject != null)
                        {
                            gameObject.SetActive(true);
                        }
                    }
                }
            }
        }

        return revision;
    }

    private async Task Download(
        Task predecessor,
        long revision,
        string title,
        string? url,
        uint hash,
        string? countdownPath)
    {
        try
        {
            await predecessor;
            if (!IsCurrent(revision))
            {
                return;
            }

            string filePath = await MenuBundleFileWorker.PreparePath(_takeoverFolder, title);
            if (!IsCurrent(revision))
            {
                return;
            }

            using IDisposable lease = await MenuBundleFileWorker.Acquire(filePath);
            if (!IsCurrent(revision))
            {
                return;
            }

            bool exists = await MenuBundleFileWorker.Exists(_takeoverFolder);
            if (!IsCurrent(revision))
            {
                return;
            }

            if (exists)
            {
                await LoadBundle(filePath, hash, countdownPath, revision);
                return;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                _log.Error("No bundle listed");
                return;
            }

            _log.Debug($"Downloading menu takeover bundle from [{url}]");
            if (!IsCurrent(revision))
            {
                return;
            }

            using UnityWebRequest www = UnityWebRequest.Get(url);
            if (!IsCurrent(revision))
            {
                return;
            }

            CancellationToken token = _cancellationTokenManager.Reset();
            if (!IsCurrent(revision))
            {
                return;
            }

            await www.SendAndVerify(null, token);
            if (!IsCurrent(revision))
            {
                return;
            }

            byte[] data = www.downloadHandler.data;
            await MenuBundleFileWorker.Write(_takeoverFolder, filePath, data);
            if (!IsCurrent(revision))
            {
                return;
            }

            await LoadBundle(filePath, hash, countdownPath, revision);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (IsCurrent(revision))
            {
                _log.Error($"Exception while loading menu takeover bundle\n{e}");
            }
        }
    }

    private async Task CompleteDownload(Task operation, TaskCompletionSource<bool> completion, long revision)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (IsCurrent(revision))
            {
                try
                {
                    _log.Error($"Exception while loading menu takeover bundle\n{e}");
                }
                catch
                {
                }
            }
        }
        finally
        {
            completion.TrySetResult(true);
        }
    }

    private async Task LoadBundle(string filePath, uint hash, string? countdownPath, long revision)
    {
        AssetBundle? bundle = null;
        GameObject? stagedPrefab = null;
        try
        {
            bundle = await MediaExtensions.LoadFromFileAsync(filePath, hash);
            if (!IsCurrent(revision))
            {
                return;
            }

            if (bundle == null)
            {
                await MenuBundleFileWorker.Delete(filePath);
                if (!IsCurrent(revision))
                {
                    return;
                }

                throw new InvalidOperationException("Failed to load bundle.");
            }

            string[] prefabNames = bundle.GetAllAssetNames();
            if (prefabNames.Length > 1)
            {
                _log.Warn($"More than one asset found in assetbundle, using first [{prefabNames[0]}]");
            }

            if (!IsCurrent(revision))
            {
                return;
            }

            GameObject obj = await bundle.LoadAssetAsyncTask<GameObject>(prefabNames[0]);
            if (!IsCurrent(revision))
            {
                return;
            }

            stagedPrefab = _instantiator.InstantiatePrefab(obj);
            if (!IsCurrent(revision))
            {
                return;
            }

            _instantiator.InstantiateComponent<TakeoverPrefabAudioController>(stagedPrefab);
            if (!IsCurrent(revision))
            {
                return;
            }

            TextMeshPro? countdown = null;
            if (!string.IsNullOrEmpty(countdownPath))
            {
                countdown = stagedPrefab.transform.Find(countdownPath).GetComponent<TextMeshPro>();
            }

            bundle.Unload(false);
            bundle = null;
            if (!IsCurrent(revision))
            {
                return;
            }

            stagedPrefab.SetActive(false);
            if (!IsCurrent(revision))
            {
                return;
            }

            _prefab = stagedPrefab;
            stagedPrefab = null;
            _countdownText = countdown;
            Refresh();
        }
        finally
        {
            try
            {
                if (stagedPrefab != null)
                {
                    Object.Destroy(stagedPrefab);
                }
            }
            finally
            {
                if (bundle != null)
                {
                    bundle.Unload(true);
                }
            }
        }
    }

    private void OnListingFound(Listing? listing)
    {
        if (_disposed)
        {
            return;
        }

        _bundleInfo = listing?.Takeover.Bundles.FirstOrDefault(b => b.GameVersion.MatchesGameVersion());
        if (_bundleInfo == null)
        {
            _log.Debug("No valid takeover bundle for current game version");
            Reset();
            return;
        }

        _startTime = listing?.Time ?? DateTime.MinValue;
        _disableDust = listing?.Takeover.DisableDust ?? false;
        _disableLogo = listing?.Takeover.DisableLogo ?? false;

        if (_bundleInfo.Hash == _lastHash)
        {
            return;
        }

        _lastHash = _bundleInfo.Hash;

        string title = listing == null ? "undefined" : listing.Title;
        string? url = _bundleInfo.Url;
        uint hash = _bundleInfo.Hash;
        string? countdownPath = listing?.Takeover.CountdownTMP;
        long revision = Reset();
        if (!IsCurrent(revision))
        {
            return;
        }

        if (title == null)
        {
            throw new ArgumentNullException("source");
        }

        Task predecessor = _pipeline;
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pipeline = completion.Task;
        Task operation = Download(predecessor, revision, title, url, hash, countdownPath);
        _ = CompleteDownload(operation, completion, revision);
    }
}
