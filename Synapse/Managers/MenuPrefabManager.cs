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
    private string _filePath = string.Empty;

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
        _listingManager.ListingFound -= OnListingFound;
        _networkManager.EliminatedUpdated -= Refresh;
    }

    internal void Reset(bool clearPrefab)
    {
        _didLoadSucceed = null;

        // ReSharper disable once InvertIf
        if (clearPrefab && _prefab != null)
        {
            Object.Destroy(_prefab);
            _prefab = null;
        }
    }

    internal async Task Download()
    {
        if (_prefab != null)
        {
            Invoke(true);
            return;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                DownloadProgress = 0.99f;
                await LoadBundle();
                DownloadProgress = 1;
                Invoke(true);
                return;
            }

            string? url = _bundleInfo?.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                _log.Error("No bundle listed");
                Invoke(true);
                return;
            }

            _log.Debug($"Downloading lobby bundle from [{url}]");
            using UnityWebRequest www = UnityWebRequest.Get(url);
            await www.SendAndVerify(n => DownloadProgress = n * 0.98f, _cancellationTokenManager.Reset());
            byte[] data = www.downloadHandler.data;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllBytes(_filePath, data);
            });
            DownloadProgress = 0.99f;
            await LoadBundle();
            DownloadProgress = 1;
            Invoke(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.Error($"Exception while loading lobby bundle\n{e}");
            Invoke(false);
        }

        return;

        void Invoke(bool success)
        {
            _didLoadSucceed = success;
            LoadedBacking?.Invoke(success);
            LoadedBacking = null;
        }
    }

    internal void Hide()
    {
        _active = false;
        Refresh();
    }

    internal void Show()
    {
        _active = true;
        Refresh();
    }

    private async Task LoadBundle()
    {
        if (_bundleInfo == null)
        {
            throw new InvalidOperationException("No bundle info found.");
        }

        AssetBundle? bundle = await MediaExtensions.LoadFromFileAsync(_filePath, _bundleInfo.Hash);
        if (bundle == null)
        {
            FileInfo file = new(_filePath);
            if (file.Exists)
            {
                file.Delete();
            }

            throw new InvalidOperationException("Failed to load bundle.");
        }

        string[] prefabNames = bundle.GetAllAssetNames();
        if (prefabNames.Length > 1)
        {
            _log.Warn($"More than one asset found in assetbundle, using first [{prefabNames[0]}]");
        }

        GameObject obj = await bundle.LoadAssetAsyncTask<GameObject>(prefabNames[0]);
        _prefab = _instantiator.InstantiatePrefab(obj);
        _instantiator.InstantiateComponent<LobbyPrefabAudioController>(_prefab);
        Animator = _prefab.GetComponent<Animator>();
        bundle.Unload(false);
        if (Animator == null)
        {
            _log.Error("No animator on prefab");
        }

        _prefab.SetActive(_active);
    }

    private void Refresh()
    {
        if (_lastActive == _active)
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

        _cameraDepthTextureManager.DepthTextureMode = (DepthTextureMode)_lobbyInfo!.DepthTextureMode;

        Reset(true);

        string listingTitle = listing == null
            ? "undefined"
            : new string(
                listing
                    .Title.Select(
                        j =>
                        {
                            if (char.IsLetter(j) || char.IsNumber(j))
                            {
                                return j;
                            }

                            return '_';
                        })
                    .ToArray());
        _filePath = Path.Combine(_folder, listingTitle);
    }
}
