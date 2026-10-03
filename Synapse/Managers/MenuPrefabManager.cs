using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Controllers;
using Synapse.Extras;
using Synapse.HarmonyPatches;
using Synapse.Networking.Models;
using UnityEngine;
using UnityEngine.Networking;
using Zenject;
using Object = UnityEngine.Object;

namespace Synapse.Managers;

internal class MenuPrefabManager : IDisposable
{
    private static readonly string _folder =
        (Path.GetDirectoryName(Application.streamingAssetsPath) ?? throw new InvalidOperationException()) +
        $"{Path.DirectorySeparatorChar}Synapse{Path.DirectorySeparatorChar}Bundles";

    private static readonly int _eliminated = Animator.StringToHash("eliminated");

    private readonly CancellationTokenManager _cancellationTokenManager;
    private readonly GlobalParticleManager.ParticleHold _particleHold;
    private readonly MenuMusicManager _menuMusicManager;
    private readonly IInstantiator _instantiator;
    private readonly ListingManager _listingManager;
    private readonly NetworkManager _networkManager;
    private readonly CameraDepthTextureManager _cameraDepthTextureManager;

    private readonly SiraLog _log;
    private readonly MenuEnvironmentManager _menuEnvironmentManager;

    private bool _active;
    private bool _lastActive;
    private uint _lastHash;
    private LobbyInfo? _lobbyInfo;
    private BundleInfo? _bundleInfo;

    private bool? _didLoadSucceed;
    private string? _pathTitle;
    private bool _disposed;
    private long _revision;
    private long _downloadRevision;
    private Task? _downloadTask;
    private Task _physicalTask = Task.CompletedTask;

    private GameObject? _prefab;

    [UsedImplicitly]
    private MenuPrefabManager(
        SiraLog log,
        IInstantiator instantiator,
        ListingManager listingManager,
        NetworkManager networkManager,
        CameraDepthTextureManager cameraDepthTextureManager,
        MenuEnvironmentManager menuEnvironmentManager,
        CancellationTokenManager cancellationTokenManager,
        GlobalParticleManager.ParticleHold particleHold,
        MenuMusicManager menuMusicManager)
    {
        _log = log;
        _instantiator = instantiator;
        _listingManager = listingManager;
        _networkManager = networkManager;
        _cameraDepthTextureManager = cameraDepthTextureManager;
        _menuEnvironmentManager = menuEnvironmentManager;
        _cancellationTokenManager = cancellationTokenManager;
        _particleHold = particleHold;
        _menuMusicManager = menuMusicManager;
        listingManager.ListingFound += OnListingFound;
        networkManager.EliminatedUpdated += Refresh;
    }

    internal event Action<bool>? Loaded
    {
        add
        {
            if (_disposed)
            {
                return;
            }

            if (_didLoadSucceed != null)
            {
                value?.Invoke(_didLoadSucceed.Value);
                return;
            }

            LoadedBacking += value;
        }

        remove => LoadedBacking -= value;
    }

    private event Action<bool>? LoadedBacking;

    internal Animator? Animator { get; private set; }

    internal float DownloadProgress { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LoadedBacking = null;
        _listingManager.ListingFound -= OnListingFound;
        _networkManager.EliminatedUpdated -= Refresh;
        Reset(true);
    }

    internal void Reset(bool clearPrefab)
    {
        _revision++;
        _didLoadSucceed = null;
        GameObject? previous = null;

        // ReSharper disable once InvertIf
        if (clearPrefab)
        {
            previous = _prefab;
            _prefab = null;
            Animator = null;
        }

        try
        {
            _cancellationTokenManager.Cancel();
        }
        finally
        {
            if (previous != null)
            {
                Object.Destroy(previous);
            }
        }
    }

    internal Task Download()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (_prefab != null)
        {
            try
            {
                Invoke(_revision, true);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                TaskCompletionSource<object?> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (exception is OperationCanceledException)
                {
                    failed.TrySetCanceled();
                }
                else
                {
                    failed.TrySetException(exception);
                }

                return failed.Task;
            }
        }

        if (_downloadRevision == _revision && _downloadTask is { IsCompleted: false })
        {
            return _downloadTask;
        }

        DownloadRequest request = new(_revision, _pathTitle, _bundleInfo?.Url, _bundleInfo?.Hash);
        Task previous = _physicalTask;
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<object?> physical = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _downloadRevision = _revision;
        _downloadTask = completion.Task;
        _physicalTask = physical.Task;
        _ = CompleteDownload(request, previous, completion, physical);
        return completion.Task;
    }

    internal void Show()
    {
        _active = true;
        Refresh();
    }

    private async Task CompleteDownload(
        DownloadRequest request,
        Task previous,
        TaskCompletionSource<object?> completion,
        TaskCompletionSource<object?> physical)
    {
        try
        {
            await Download(request, previous);
            completion.TrySetResult(null);
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            physical.TrySetResult(null);
        }
    }

    private async Task Download(DownloadRequest request, Task previous)
    {
        Action<bool>? publishingCallbacks = null;
        bool publicationStarted = false;
        try
        {
            await previous;
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            string path = request.Title == null ? string.Empty : await MenuBundleFileWorker.PreparePath(_folder, request.Title);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            using IDisposable lease = await MenuBundleFileWorker.Acquire(path);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            bool exists = await MenuBundleFileWorker.Exists(path);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            if (exists)
            {
                DownloadProgress = 0.99f;
                await LoadBundle(request, path);
                if (!IsCurrent(request.Revision))
                {
                    return;
                }

                DownloadProgress = 1;
                InvokeSuccess();
                return;
            }

            string? url = request.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                _log.Error("No bundle listed");
                InvokeSuccess();
                return;
            }

            _log.Debug($"Downloading lobby bundle from [{url}]");
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            using UnityWebRequest www = UnityWebRequest.Get(url);
            System.Threading.CancellationToken token = _cancellationTokenManager.Reset();
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            await www.SendAndVerify(
                n =>
                {
                    if (IsCurrent(request.Revision))
                    {
                        DownloadProgress = n * 0.98f;
                    }
                },
                token);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            byte[] data = www.downloadHandler.data;
            await MenuBundleFileWorker.Write(_folder, path, data);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            DownloadProgress = 0.99f;
            await LoadBundle(request, path);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            DownloadProgress = 1;
            InvokeSuccess();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            _log.Error($"Exception while loading lobby bundle\n{e}");
            if (publicationStarted)
            {
                Invoke(request.Revision, false, publishingCallbacks);
            }
            else
            {
                Invoke(request.Revision, false);
            }
        }

        return;

        void InvokeSuccess()
        {
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            publishingCallbacks = LoadedBacking;
            LoadedBacking = null;
            publicationStarted = true;
            Invoke(request.Revision, true, publishingCallbacks);
        }
    }

    internal void Hide()
    {
        _active = false;
        Refresh();
    }

    private async Task LoadBundle(DownloadRequest request, string path)
    {
        if (request.Hash == null)
        {
            throw new InvalidOperationException("No bundle info found.");
        }

        AssetBundle? bundle = await MediaExtensions.LoadFromFileAsync(path, request.Hash.Value);
        GameObject? prefab = null;
        try
        {
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            if (bundle == null)
            {
                await MenuBundleFileWorker.Delete(path);
                if (IsCurrent(request.Revision))
                {
                    throw new InvalidOperationException("Failed to load bundle.");
                }

                return;
            }

            string[] prefabNames = bundle.GetAllAssetNames();
            if (prefabNames.Length > 1)
            {
                _log.Warn($"More than one asset found in assetbundle, using first [{prefabNames[0]}]");
            }

            if (!IsCurrent(request.Revision))
            {
                return;
            }

            GameObject obj = await bundle.LoadAssetAsyncTask<GameObject>(prefabNames[0]);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            prefab = _instantiator.InstantiatePrefab(obj);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            _instantiator.InstantiateComponent<LobbyPrefabAudioController>(prefab);
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            Animator? animator = prefab.GetComponent<Animator>();
            bundle.Unload(false);
            bundle = null;
            if (!IsCurrent(request.Revision))
            {
                return;
            }

            if (animator == null)
            {
                _log.Error("No animator on prefab");
            }

            if (!IsCurrent(request.Revision))
            {
                return;
            }

            _prefab = prefab;
            prefab = null;
            Animator = animator;
            _prefab.SetActive(_active);
        }
        finally
        {
            if (prefab != null)
            {
                Object.Destroy(prefab);
            }

            if (bundle != null)
            {
                bundle.Unload(true);
            }
        }
    }

    private bool IsCurrent(long revision)
    {
        return !_disposed && revision == _revision;
    }

    private void Invoke(long revision, bool success)
    {
        if (!IsCurrent(revision))
        {
            return;
        }

        Action<bool>? callbacks = LoadedBacking;
        LoadedBacking = null;
        Invoke(revision, success, callbacks);
    }

    private void Invoke(long revision, bool success, Action<bool>? callbacks)
    {
        if (!IsCurrent(revision))
        {
            return;
        }

        _didLoadSucceed = success;
        if (callbacks == null)
        {
            return;
        }

        foreach (Action<bool> callback in callbacks.GetInvocationList())
        {
            if (!IsCurrent(revision))
            {
                return;
            }

            callback(success);
        }
    }

    private void Refresh()
    {
        if (_disposed || _lastActive == _active)
        {
            return;
        }

        _lastActive = _active;
        _cameraDepthTextureManager.Enabled = _active;
        _particleHold.DustDisabled = _active && (_lobbyInfo?.DisableDust ?? false);
        _particleHold.SmokeDisabled = _active && (_lobbyInfo?.DisableSmoke ?? false);
        _menuMusicManager.MenuMusicDisabled = _active;
        if (_active)
        {
            SetPrefabActive(false);
            SetPrefabActive(true);

            // None is actually used on 1.40 for some reason?
            _menuEnvironmentManager.ShowEnvironmentType((MenuEnvironmentManager.MenuEnvironmentType)99);
        }
        else
        {
            _menuEnvironmentManager.ShowEnvironmentType(MenuEnvironmentManager.MenuEnvironmentType.Default);
            SetPrefabActive(false);
        }

        if (Animator != null)
        {
            Animator.SetBool(_eliminated, _networkManager.Status.Stage is PlayStatus { Eliminated: true });
        }

        return;

        void SetPrefabActive(bool value)
        {
            if (_prefab != null)
            {
                _prefab.SetActive(value);
            }
        }
    }

    private void OnListingFound(Listing? listing)
    {
        if (_disposed)
        {
            return;
        }

        _lobbyInfo = listing?.Lobby;
        _bundleInfo = _lobbyInfo?.Bundles.FirstOrDefault(b => b.GameVersion.MatchesGameVersion());
        if (_bundleInfo == null)
        {
            Reset(true);
            return;
        }

        if (_bundleInfo.Hash == _lastHash)
        {
            return;
        }

        _lastHash = _bundleInfo.Hash;
        DepthTextureMode depthTextureMode = (DepthTextureMode)_lobbyInfo!.DepthTextureMode;
        _pathTitle = listing == null ? "undefined" : listing.Title;
        long revision = _revision + 1;
        Reset(true);
        if (!IsCurrent(revision))
        {
            return;
        }

        _cameraDepthTextureManager.DepthTextureMode = depthTextureMode;
        if (_pathTitle == null)
        {
            throw new ArgumentNullException("source");
        }
    }

    private sealed class DownloadRequest(long revision, string? title, string? url, uint? hash)
    {
        internal long Revision { get; } = revision;

        internal string? Title { get; } = title;

        internal string? Url { get; } = url;

        internal uint? Hash { get; } = hash;
    }
}
