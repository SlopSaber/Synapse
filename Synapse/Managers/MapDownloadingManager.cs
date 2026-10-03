using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly ListingManager _listingManager;
#if !PRE_V1_37_1
    private readonly BeatmapLevelsModel _beatmapLevelsModel;
#endif
    private readonly CancellationTokenManager _cancellationTokenManager;
    private readonly SongCoreLoader? _songCoreLoader;
    private readonly DirectoryInfo _tmp;
    private readonly Task<MapFileWorker.Result> _initialization;
    private static string? _sessionTempName;
    private Task? _pipeline;
    private MapContext? _current;
    private bool _disposed;
    private bool _initializationObserved;

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
        ListingManager listingManager,
        [InjectOptional] SongCoreLoader? songCoreLoader)
    {
        _log = log;
        _customLevelLoader = customLevelLoader;
        _networkManager = networkManager;
        _listingManager = listingManager;
#if !PRE_V1_37_1
        _beatmapLevelsModel = beatmapLevelsModel;
#endif
        _cancellationTokenManager = cancellationTokenManager;
        _songCoreLoader = songCoreLoader;
        networkManager.MapUpdated += OnMapUpdated;
        networkManager.Closed += OnClosed;

        string? oldRoot = null;
        if (_sessionTempName == null)
        {
            if (Guid.TryParse(config.Temp, out _) && config.Temp != null)
            {
                oldRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), config.Temp));
            }

            _sessionTempName = Guid.NewGuid().ToString();
        }

        // Published levels retain file-backed media for the process lifetime.
        config.Temp = _sessionTempName;
        _tmp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), _sessionTempName));
        _initialization = MapFileWorker.Initialize(oldRoot, _tmp.FullName, Path.GetFullPath(_mapFolder));
    }

    public event Action<string>? ProgressUpdated;

    public event Action<DownloadedMap>? MapDownloaded
    {
        add
        {
            if (_disposed)
            {
                return;
            }

            MapDownloadedBacking += value;
            MapContext? context = _current;
            if (_beatmapLevel.HasValue && context is { Committed: true } && IsPublicationCurrent(context))
            {
                Publish(value, _beatmapLevel.Value, context);
            }
        }

        remove => MapDownloadedBacking -= value;
    }

    public event Action<DownloadedMap>? MapDownloadedOnce
    {
        add
        {
            if (_disposed)
            {
                return;
            }

            MapContext? context = _current;
            if (_beatmapLevel.HasValue && context is { Committed: true } && IsPublicationCurrent(context))
            {
                Publish(value, _beatmapLevel.Value, context);
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
        _disposed = true;
        _networkManager.MapUpdated -= OnMapUpdated;
        _networkManager.Closed -= OnClosed;
        _cancellationTokenManager.Cancel();
        _current = null;
        _beatmapLevel = null;
        MapDownloadedBacking = null;
        MapDownloadedOnceBacking = null;
    }

    public void Tick()
    {
        if (!_initializationObserved && _initialization.IsCompleted)
        {
            _initializationObserved = true;
            try
            {
                LogWarnings(_initialization.GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                _log.Error($"Error preparing map directories\n{exception}");
                _error = "ERROR!";
            }
        }

        if (_current != null && IsCurrent(_current))
        {
            _downloadProgress = _current.Progress.Value;
        }

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
        _ = ObservePurge(MapFileWorker.Purge(Path.GetFullPath(_mapFolder)));
    }

    internal void Cancel()
    {
        _cancellationTokenManager.Cancel();
        if (_current is { Committed: false })
        {
            _current = null;
        }
    }

    private static async Task ObservePurge(Task<MapFileWorker.Result> work)
    {
        try
        {
            MapFileWorker.Result result = await work;
            foreach ((string path, Exception error) in result.Warnings)
            {
                Plugin.Log.Error($"Exception while purging directory: [{path}]\n{error}");
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Error($"Exception while purging directory: [{_mapFolder}]\n{exception}");
        }
    }

    private void OnMapUpdated(int index, Map map)
    {
        if (_disposed)
        {
            return;
        }

        MapDownloadedOnceBacking = null;
        _beatmapLevel = null;
        CancellationToken token = _cancellationTokenManager.Reset();
        MapContext context = new(index, map, _listingManager.Listing, token);
        _current = context;
        Task previous = _pipeline ?? Task.CompletedTask;
        _pipeline = context.Completion.Task;
        _ = UnityMainThreadTaskScheduler.Factory.StartNew(() => RunPipeline(context, previous), CancellationToken.None).Unwrap();
    }

    private void OnClosed()
    {
        _cancellationTokenManager.Cancel();
        _current = null;
        _beatmapLevel = null;
        MapDownloadedOnceBacking = null;
    }

    private bool IsCurrent(MapContext context) => !_disposed && ReferenceEquals(_current, context) &&
        ReferenceEquals(_listingManager.Listing, context.Listing) && !context.Token.IsCancellationRequested;

    private bool IsPublicationCurrent(MapContext context) => !_disposed && ReferenceEquals(_current, context) &&
        ReferenceEquals(_listingManager.Listing, context.Listing);

    private void Publish(Action<DownloadedMap>? handlers, DownloadedMap map, MapContext context)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Action<DownloadedMap> handler in handlers.GetInvocationList())
        {
            if (!IsPublicationCurrent(context))
            {
                return;
            }

            try
            {
                handler(map);
            }
            catch (Exception exception)
            {
                try
                {
                    _log.Error($"Error handling downloaded map [{map.Map.Name}]\n{exception}");
                }
                catch
                {
                }
            }
        }
    }

    private void RequireCurrent(MapContext context)
    {
        if (!IsCurrent(context))
        {
            throw new OperationCanceledException(context.Token);
        }
    }

    private void LogWarnings(MapFileWorker.Result result)
    {
        foreach ((string path, Exception error) in result.Warnings)
        {
            _log.Error($"Exception while purging directory: [{path}]\n{error}");
        }
    }

    private async Task RunPipeline(MapContext context, Task previous)
    {
        try
        {
            await previous;
            await _initialization;
            RequireCurrent(context);
            int retry = 1;
            while (IsCurrent(context))
            {
                try
                {
                    await Download(context);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    if (context.Committed)
                    {
                        return;
                    }

                    RequireCurrent(context);
                    await Task.Delay((++retry) * 1000, context.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrent(context))
            {
                _log.Error($"Error preparing map [{context.Map.Name}]\n{exception}");
                _error = "ERROR!";
            }
        }
        finally
        {
            try
            {
                await CleanupAttempt(context);
            }
            finally
            {
                context.Completion.TrySetResult(true);
            }
        }
    }

    private async Task CleanupAttempt(MapContext context)
    {
        if (context.AttemptPath == null || context.NativeExposed)
        {
            return;
        }

        try
        {
            await MapFileWorker.Delete(context.AttemptPath);
            context.AttemptPath = null;
        }
        catch (Exception exception)
        {
            _log.Error($"Error cleaning map directory [{context.AttemptPath}]\n{exception}");
        }
    }

    private async Task Download(MapContext context)
    {
        RequireCurrent(context);
        await CleanupAttempt(context);
        RequireCurrent(context);
        int index = context.Index;
        Map map = context.Map;
        string name = map.Name;
        CancellationToken token = context.Token;
        _error = null;
        _lastProgress = 0;
        _downloadProgress = 0;
        context.Progress.Set(0);
        string unzipPath = Path.GetFullPath(Path.Combine(_tmp.FullName, Guid.NewGuid().ToString()));
        context.AttemptPath = unzipPath;
        context.NativeExposed = false;
        try
        {
            Download download =
                map.Downloads.FirstOrDefault(n => n.GameVersion.MatchesGameVersion()) ??
                throw new InvalidOperationException($"No download found for game version [{Plugin.GameVersion}].");
            string url = download.Url;
            string hash = download.Hash;
            string? key = download.Key;
            MapFileWorker.Result files = await MapFileWorker.Extract(_mapFolder, name, unzipPath, key, null, context.Progress, token);
            RequireCurrent(context);
            if (!files.Found)
            {
                _log.Debug($"Attempting to download [{map.Name}] from [{url}]");
                RequireCurrent(context);
                MemoryStream stream = await MediaExtensions.DownloadHash(
                    url,
                    hash,
                    value =>
                    {
                        if (IsCurrent(context))
                        {
                            context.Progress.Set(value * 0.95f);
                        }
                    },
                    token);
                try
                {
                    RequireCurrent(context);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                files = await MapFileWorker.Extract(_mapFolder, name, unzipPath, key, stream, context.Progress, token);
                RequireCurrent(context);
                if (!files.Found)
                {
                    throw new InvalidOperationException($"Could not find [{map.Name}] in cache.");
                }
            }

            _downloadProgress = 0.98f;
            context.Progress.Set(0.98f);
#if !PRE_V1_37_1
            BeatmapLevel beatmapLevel;
            if (_songCoreLoader != null)
            {
                context.NativeExposed = true;
                beatmapLevel = _songCoreLoader.Load(unzipPath);
            }
            else
            {
                CustomLevelFolderInfo? customLevelFolderInfo =
                    await FileSystemCustomLevelProvider.LoadCustomLevelFolderInfoAsync(unzipPath, token) ??
                    throw new InvalidOperationException("Failed to get CustomLevelFolderInfo.");
                RequireCurrent(context);

                (BeatmapLevel, CustomLevelLoader.LoadedSaveData) tuple =
                    await _customLevelLoader.LoadBeatmapLevelAsync(customLevelFolderInfo.Value, token) ??
                    throw new InvalidOperationException("Failed to get BeatmapLevel.");
                context.NativeExposed = true;
                RequireCurrent(context);

                beatmapLevel = tuple.Item1;
                _customLevelLoader._loadedBeatmapSaveData[beatmapLevel.levelID] = tuple.Item2;
            }

            RequireCurrent(context);

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
                context.NativeExposed = true;
                beatmapLevel = _songCoreLoader.Load(unzipPath);
            }
            else
            {
                StandardLevelInfoSaveData infoSaveData =
                    await _customLevelLoader.LoadCustomLevelInfoSaveDataAsync(unzipPath, token);
                RequireCurrent(context);
                beatmapLevel = await _customLevelLoader.LoadCustomPreviewBeatmapLevelAsync(
                    unzipPath,
                    infoSaveData,
                    token);
                context.NativeExposed = true;
                RequireCurrent(context);
            }

            _downloadProgress = 0.99f;
            CustomBeatmapLevel customBeatmapLevel =
                await _customLevelLoader.LoadCustomBeatmapLevelAsync(beatmapLevel, token);
            RequireCurrent(context);
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

            RequireCurrent(context);
            _log.Debug($"Successfully downloaded [{map.Name}] as [{beatmapLevel.levelID}]");
            RequireCurrent(context);
            _downloadProgress = 1;
            context.Progress.Set(1);

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
            context.Committed = true;
            Action<DownloadedMap>? once = MapDownloadedOnceBacking;
            MapDownloadedOnceBacking = null;
            Publish(MapDownloadedBacking, downloadedMap, context);
            Publish(once, downloadedMap, context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            RequireCurrent(context);
            _log.Error($"Error downloading map [{map.Name}]\n{e}");
            RequireCurrent(context);
            _error = "ERROR!";
            if (!context.Committed)
            {
                await MapFileWorker.ClearMapCache(_mapFolder, name, token);
            }

            throw;
        }
    }

    private sealed class MapContext
    {
        internal MapContext(int index, Map map, object? listing, CancellationToken token)
        {
            Index = index;
            Map = map;
            Listing = listing;
            Token = token;
        }

        internal int Index { get; }

        internal Map Map { get; }

        internal object? Listing { get; }

        internal CancellationToken Token { get; }

        internal MapFileWorker.Progress Progress { get; } = new();

        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string? AttemptPath { get; set; }

        internal bool NativeExposed { get; set; }

        internal bool Committed { get; set; }
    }
}
