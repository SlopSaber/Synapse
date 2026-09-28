using System;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json;
using SiraUtil.Logging;
using Synapse.Extras;
using Synapse.Networking.Models;
using UnityEngine;
using UnityEngine.Networking;
using Zenject;

namespace Synapse.Managers;

internal class ListingManager : IInitializable
{
    private readonly CancellationTokenManager _cancellationTokenManager;
    private readonly Config _config;
    private readonly SiraLog _log;
    private Sprite? _bannerImage;
    private string? _bannerUrl;
    private string? _lastListing;

    private Task? _initializeTask;
    private DateTime _nextAttemptUtc;
    private string? _lastAttemptUrl;

    [UsedImplicitly]
    private ListingManager(SiraLog log, Config config, CancellationTokenManager cancellationTokenManager)
    {
        _log = log;
        _config = config;
        _cancellationTokenManager = cancellationTokenManager;
    }

    public event Action<Sprite?>? BannerImageCreated
    {
        add
        {
            if (_bannerImage != null)
            {
                value?.Invoke(_bannerImage);
            }

            BannerImageCreatedBacking += value;
        }

        remove => BannerImageCreatedBacking -= value;
    }

    public event Action<Listing?>? ListingFound
    {
        add
        {
            if (Listing != null)
            {
                value?.Invoke(Listing);
            }

            ListingFoundBacking += value;
        }

        remove => ListingFoundBacking -= value;
    }

    private event Action<Sprite?>? BannerImageCreatedBacking;

    private event Action<Listing?>? ListingFoundBacking;

    public Listing? Listing { get; private set; }

    public void Initialize()
    {
        if ((!_initializeTask?.IsCompleted ?? false) ||
            Listing != null)
        {
            return;
        }

        string url = Plugin.ListingOverride ?? _config.Url;
        if (url == _lastAttemptUrl && DateTime.UtcNow < _nextAttemptUtc)
        {
            return;
        }

        _lastAttemptUrl = url;
        _initializeTask = InitializeAsync(url, _cancellationTokenManager.Reset());
    }

    public void Clear()
    {
        _nextAttemptUtc = DateTime.MinValue;
        ClearListing();
    }

    private void ClearListing()
    {
        Listing = null;
        _bannerUrl = null;
        _bannerImage = null;
        _lastListing = null;
        ListingFoundBacking?.Invoke(null);
        BannerImageCreatedBacking?.Invoke(null);
    }

    private async Task GetBannerImage(Listing listing, CancellationToken token)
    {
        if (_bannerUrl != listing.BannerImage)
        {
            _bannerUrl = listing.BannerImage;

            try
            {
                _log.Debug($"Fetching banner image from [{listing.BannerImage}]");
                Sprite bannerImage = await MediaExtensions.RequestSprite(listing.BannerImage, token);
                _bannerImage = bannerImage;
                BannerImageCreatedBacking?.Invoke(bannerImage);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _log.Error($"Exception while fetching promo banner image\n{e}");
                BannerImageCreatedBacking?.Invoke(null);
            }
        }
    }

    private async Task InitializeAsync(string url, CancellationToken token)
    {
        try
        {
            _log.Debug($"Checking [{url}] for active listing");
            using UnityWebRequest www = UnityWebRequest.Get(url);
            www.timeout = 20;
            try
            {
                await www.SendAndVerify(token);
            }
            catch (InvalidOperationException) when (www.responseCode == 404 || www.responseCode == 410)
            {
                _nextAttemptUtc = DateTime.UtcNow.AddMinutes(5);
                ClearListing();
                _log.Info($"Listing endpoint [{url}] is unavailable (HTTP {www.responseCode}); checking again after five minutes.");
                return;
            }

            string json = www.downloadHandler.text;
            Listing? listing = JsonConvert.DeserializeObject<Listing>(json, JsonSettings.Settings);
            if (listing == null)
            {
                throw new JsonSerializationException("Listing response contained no event data.");
            }

            if (listing.Guid == _lastListing)
            {
                return;
            }

            _lastListing = listing.Guid;
            _log.Debug($"Found active listing for [{listing.Title}]");

            Listing = listing;
            ListingFoundBacking?.Invoke(Listing);
            if (_config.LastEvent.Title != listing.Title)
            {
                MapDownloadingManager.PurgeCache();
                _config.LastEvent = new EventInfo
                {
                    Title = listing.Title
                };
            }

            _ = GetBannerImage(listing, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Initialize owns this task; cancellation must not leave an unobserved fault.
        }
        catch (Exception e)
        {
            _log.Warn($"Exception while loading listing\n{e}");
            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(30);
            ClearListing();
        }
    }
}
