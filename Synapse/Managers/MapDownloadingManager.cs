using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities.Async;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Extras;
using Synapse.Models;
using Synapse.Networking.Models;
using UnityEngine;
using Zenject;

namespace Synapse.Managers;

internal sealed class MapDownloadingManager : IDisposable, ITickable
{
    private static readonly string _mapFolder =
        (Path.GetDirectoryName(Application.streamingAssetsPath) ?? throw new InvalidOperationException()) +
        $"{Path.DirectorySeparatorChar}Synapse{Path.DirectorySeparatorChar}Levels";

    private readonly SiraLog _log;
    private readonly CustomLevelLoader _customLevelLoader;
    private readonly NetworkManager _networkManager;
#if !PRE_V1_37_1
    private readonly BeatmapLevelsModel _beatmapLevelsModel;
#endif
    private readonly CancellationTokenManager _cancellationTokenManager;
    private readonly SongCoreLoader? _songCoreLoader;
    private readonly DirectoryInfo _tmp;

    private string _lastSent = string.Empty;
    private string? _error;
    private float _downloadProgress;
    private float _lastProgress;

    private DownloadedMap? _beatmapLevel;

    [UsedImplicitly]
    [Inject]
    private MapDownloadingManager(
        SiraLog log,
        CustomLevelLoader customLevelLoader,
        NetworkManager networkManager,
#if !PRE_V1_37_1
        BeatmapLevelsModel beatmapLevelsModel,
#endif
        Config config,
        CancellationTokenManager cancellationTokenManager,
        [InjectOptional] SongCoreLoader? songCoreLoader)
    {
        _log = log;
        _customLevelLoader = customLevelLoader;
        _networkManager = networkManager;
#if !PRE_V1_37_1
        _beatmapLevelsModel = beatmapLevelsModel;
#endif
        _cancellationTokenManager = cancellationTokenManager;
        _songCoreLoader = songCoreLoader;
        networkManager.MapUpdated += OnMapUpdated;
        networkManager.Closed += OnClosed;

        string? oldTemp = config.Temp;
        if (!string.IsNullOrEmpty(oldTemp))
        {
            DirectoryInfo oldTempDirectory = new(Path.Combine(Path.GetTempPath(), oldTemp));
            if (oldTempDirectory.Exists)
            {
                oldTempDirectory.Delete(true);
            }
        }

        string guid = Guid.NewGuid().ToString();
        config.Temp = guid;
        _tmp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), guid));
        _tmp.Create();

        DirectoryInfo directory = new(_mapFolder);
        directory.Create();
        FileInfo jokeFile = new(Path.Combine(directory.FullName, "README.txt"));
        if (!jokeFile.Exists)
        {
            File.WriteAllText(jokeFile.FullName, "you wouldn't happen to be trying to steal maps, would you?");
        }
    }

    public event Action<string>? ProgressUpdated;

    public event Action<DownloadedMap>? MapDownloaded
    {
        add
        {
            if (_beatmapLevel.HasValue)
            {
                value?.Invoke(_beatmapLevel.Value);
            }

            MapDownloadedBacking += value;
        }

        remove => MapDownloadedBacking -= value;
    }

    public event Action<DownloadedMap>? MapDownloadedOnce
    {
        add
        {
            if (_beatmapLevel.HasValue)
            {
                value?.Invoke(_beatmapLevel.Value);
                return;
            }

            MapDownloadedOnceBacking += value;
        }

        remove => MapDownloadedOnceBacking -= value;
    }

    private event Action<DownloadedMap>? MapDownloadedOnceBacking;

    private event Action<DownloadedMap>? MapDownloadedBacking;

    public void Dispose()
    {
        _networkManager.MapUpdated -= OnMapUpdated;
        _networkManager.Closed -= OnClosed;
        DeleteTemp();
    }

    public void Tick()
    {
        string text;
        if (_error != null)
        {
            text = _error;
        }
        else
        {
            _lastProgress = Mathf.Lerp(_lastProgress, _downloadProgress, 20 * Time.deltaTime);
            text = $"{_lastProgress:0%}";
        }

        if (text == _lastSent)
        {
            return;
        }

        _lastSent = text;
        ProgressUpdated?.Invoke(text);
    }

    internal static void PurgeCache()
    {
        new DirectoryInfo(_mapFolder).Purge();
    }

    internal void Cancel()
    {
        _cancellationTokenManager.Cancel();
    }

    private void DeleteTemp()
    {
        if (Directory.Exists(_tmp.FullName))
        {
            _tmp.Delete(true);
        }
    }

    private void OnMapUpdated(int index, Map map)
    {
        MapDownloadedOnceBacking = null;
        _beatmapLevel = null;

        string mapName = Path.GetInvalidFileNameChars().Aggregate(map.Name, (current, c) => current.Replace(c, '_'));
        CancellationToken token = _cancellationTokenManager.Reset();
        UnityMainThreadTaskScheduler.Factory.StartNew(
            async () =>
            {
                int i = 1;
                while (true)
                {
                    try
                    {
                        _tmp.Purge();
                        await Download(index, map, mapName, token);
                        return;
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        await Task.Delay((++i) * 1000, token);
                    }
                }
            },
            token);
    }

    private void OnClosed()
    {
        DeleteTemp();
        _cancellationTokenManager.Cancel();
    }

    private async Task Download(int index, Map map, string mapName, CancellationToken token)
    {
        _error = null;
        _lastProgress = 0;
        _downloadProgress = 0;
        string unzipPath = _tmp.FullName;
        string path = Path.Combine(_mapFolder, mapName);
        FileInfo cacheAes = new(path + ".aes");
        FileInfo cacheZip = new(path + ".zip");
        try
        {
            Download download =
                map.Downloads.FirstOrDefault(n => n.GameVersion.MatchesGameVersion()) ??
                throw new InvalidOperationException($"No download found for game version [{Plugin.GameVersion}].");
            string url = download.Url;

            if (!cacheAes.Exists && !cacheZip.Exists)
            {
                _log.Debug($"Attempting to download [{map.Name}] from [{url}]");
                using MemoryStream stream = await MediaExtensions.DownloadHash(
                    url,
                    download.Hash,
                    n => _downloadProgress = n * 0.95f,
                    token);
                bool decrypt = !string.IsNullOrEmpty(download.Key);
                using FileStream fs = new(decrypt ? cacheAes.FullName : cacheZip.FullName, FileMode.CreateNew);
                await stream.CopyToAsync(fs);
                cacheZip.Refresh();
                cacheAes.Refresh();
            }

            if (cacheAes.Exists)
            {
                if (download.Key == null)
                {
                    throw new InvalidOperationException($"[{map.Name}] was encrypted but no key provided.");
                }

                _log.Debug($"Decrypting [{map.Name}]");
                using FileStream stream = new(cacheAes.FullName, FileMode.Open);
                using CryptoStream cryptoStream = await MediaExtensions.Decrypt(stream, download.Key);
                await MediaExtensions.Unzip(cryptoStream, _tmp.FullName, n => _downloadProgress = 0.95f + (n * 0.02f));
            }
            else if (cacheZip.Exists)
            {
                using FileStream stream = new(cacheZip.FullName, FileMode.Open);
                await MediaExtensions.Unzip(stream, _tmp.FullName, n => _downloadProgress = 0.95f + (n * 0.02f));
            }
            else
            {
                throw new InvalidOperationException($"Could not find [{map.Name}] in cache.");
            }

            _downloadProgress = 0.98f;
#if !PRE_V1_37_1
            BeatmapLevel beatmapLevel;
            if (_songCoreLoader != null)
            {
                beatmapLevel = _songCoreLoader.Load(unzipPath);
            }
            else
            {
                CustomLevelFolderInfo? customLevelFolderInfo =
                    await FileSystemCustomLevelProvider.LoadCustomLevelFolderInfoAsync(unzipPath, token) ??
                    throw new InvalidOperationException("Failed to get CustomLevelFolderInfo.");

                (BeatmapLevel, CustomLevelLoader.LoadedSaveData) tuple =
                    await _customLevelLoader.LoadBeatmapLevelAsync(customLevelFolderInfo.Value, token) ??
                    throw new InvalidOperationException("Failed to get BeatmapLevel.");

                beatmapLevel = tuple.Item1;
                _customLevelLoader._loadedBeatmapSaveData[beatmapLevel.levelID] = tuple.Item2;
            }

            // just throw that shit into the first pack we find, who cares
            // it just needs a pack for some reason
            BeatmapLevelsRepository allLoadedRepository = _beatmapLevelsModel._allLoadedBeatmapLevelsRepository ??
                                                 throw new InvalidOperationException(
                                                     "No repository found.");
            BeatmapLevelsRepository allExistingRepository = _beatmapLevelsModel._allExistingBeatmapLevelsRepository ??
                                                            throw new InvalidOperationException(
                                                                "No repository found.");
            BeatmapLevelPack beatmapLevelPack = allLoadedRepository.beatmapLevelPacks.First();
            allLoadedRepository._idToBeatmapLevel[beatmapLevel.levelID] = beatmapLevel;
            allLoadedRepository._beatmapLevelIdToBeatmapLevelPackId[beatmapLevel.levelID] = beatmapLevelPack.packID;
            allExistingRepository._idToBeatmapLevel[beatmapLevel.levelID] = beatmapLevel;
            allExistingRepository._beatmapLevelIdToBeatmapLevelPackId[beatmapLevel.levelID] = beatmapLevelPack.packID;

            List<BeatmapKey> beatmapKeys = map.Keys.Select(
                n =>
                {
                    if (!Enum.TryParse(n.Characteristic, true, out BeatmapCharacteristic characteristic))
                    {
                        characteristic = n.Characteristic.ToLowerInvariant() switch
                        {
                            "360degree" => BeatmapCharacteristic.Degree360,
                            "90degree" => BeatmapCharacteristic.Degree90,
                            _ => throw new InvalidOperationException(
                                $"Failed to find characteristic: [{n.Characteristic}].")
                        };
                    }

                    return new BeatmapKey(beatmapLevel.levelID, characteristic, (BeatmapDifficulty)n.Difficulty);
                }).ToList();
#else
            CustomPreviewBeatmapLevel beatmapLevel;
            if (_songCoreLoader != null)
            {
                beatmapLevel = _songCoreLoader.Load(unzipPath);
            }
            else
            {
                StandardLevelInfoSaveData infoSaveData =
                    await _customLevelLoader.LoadCustomLevelInfoSaveDataAsync(unzipPath, token);
                beatmapLevel = await _customLevelLoader.LoadCustomPreviewBeatmapLevelAsync(
                    unzipPath,
                    infoSaveData,
                    token);
            }

            _downloadProgress = 0.99f;
            CustomBeatmapLevel customBeatmapLevel =
                await _customLevelLoader.LoadCustomBeatmapLevelAsync(beatmapLevel, token);
            List<IDifficultyBeatmap> difficultyBeatmaps = map.Keys.Select(
                n =>
                {
                    IDifficultyBeatmapSet? set =
                        customBeatmapLevel.beatmapLevelData.GetDifficultyBeatmapSet(n.Characteristic);
                    if (set == null)
                    {
                        throw new InvalidOperationException($"Failed to find characteristic: [{n.Characteristic}].");
                    }

                    return set.difficultyBeatmaps.FirstOrDefault(m => (int)m.difficulty == n.Difficulty) ??
                           throw new InvalidOperationException($"Failed to find difficulty: [{n.Difficulty}].");
                }).ToList();
#endif

            _log.Debug($"Successfully downloaded [{map.Name}] as [{beatmapLevel.levelID}]");
            _downloadProgress = 1;

            DownloadedMap downloadedMap = new(
                index,
                map,
#if !PRE_V1_37_1
                beatmapKeys,
                beatmapLevel);
#else
                difficultyBeatmaps,
                beatmapLevel);
#endif
            _beatmapLevel = downloadedMap;
            MapDownloadedBacking?.Invoke(downloadedMap);
            MapDownloadedOnceBacking?.Invoke(downloadedMap);
            MapDownloadedOnceBacking = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.Error($"Error downloading map [{map.Name}]\n{e}");
            _error = "ERROR!";
            File.Delete(cacheAes.FullName);
            File.Delete(cacheZip.FullName);
            _tmp.Purge();
            throw;
        }
    }
}
