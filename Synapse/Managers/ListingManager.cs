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

internal class ListingManager : IInitializable, IDisposable
{
    private readonly CancellationTokenManager _cancellationTokenManager;
    private readonly Config _config;
    private readonly SiraLog _log;
    private Sprite? _bannerImage;
    private string? _bannerUrl;
    private string? _lastListing;

    private Task? _initializeTask;
    private Task? _bannerTask;
    private DateTime _nextAttemptUtc;
    private string? _lastAttemptUrl;
    private long _listingRevision;
    private bool _disposed;

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
            if (_disposed)
            {
                return;
            }

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
            if (_disposed)
            {
                return;
            }

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
        if (_disposed || (!_initializeTask?.IsCompleted ?? false) ||
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
        long revision = ++_listingRevision;
        _initializeTask = InitializeAsync(url, revision, _cancellationTokenManager.Reset());
    }

    public void Clear()
    {
        if (_disposed)
        {
            return;
        }

        _listingRevision++;
        _nextAttemptUtc = DateTime.MinValue;
        ClearListing();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listingRevision++;
        _cancellationTokenManager.Cancel();
        ListingFoundBacking = null;
        BannerImageCreatedBacking = null;
    }

    private void ClearListing()
    {
        long revision = _listingRevision;
        Listing = null;
        _bannerUrl = null;
        _bannerImage = null;
        _lastListing = null;
        ListingFoundBacking?.Invoke(null);
        if (!_disposed && revision == _listingRevision)
        {
            BannerImageCreatedBacking?.Invoke(null);
        }
    }

    private bool IsCurrentRequest(string url, long revision, CancellationToken token)
    {
        return !_disposed && !token.IsCancellationRequested && revision == _listingRevision &&
               string.Equals(url, Plugin.ListingOverride ?? _config.Url, StringComparison.Ordinal);
    }

    private async Task GetBannerImage(Listing listing, string url, long revision, CancellationToken token)
    {
        if (!IsCurrentRequest(url, revision, token) || !ReferenceEquals(listing, Listing))
        {
            return;
        }

        if (_bannerUrl != listing.BannerImage)
        {
            string bannerUrl = listing.BannerImage;
            _bannerUrl = bannerUrl;

            try
            {
                _log.Debug($"Fetching banner image from [{bannerUrl}]");
                Sprite bannerImage = await MediaExtensions.RequestSprite(bannerUrl, token);
                if (!IsCurrentRequest(url, revision, token) || !ReferenceEquals(listing, Listing) ||
                    !string.Equals(bannerUrl, _bannerUrl, StringComparison.Ordinal))
                {
                    return;
                }

                _bannerImage = bannerImage;
                BannerImageCreatedBacking?.Invoke(bannerImage);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (!IsCurrentRequest(url, revision, token) || !ReferenceEquals(listing, Listing) ||
                    !string.Equals(bannerUrl, _bannerUrl, StringComparison.Ordinal))
                {
                    return;
                }

                _log.Error($"Exception while fetching promo banner image\n{e}");
                BannerImageCreatedBacking?.Invoke(null);
            }
        }
    }

    private async Task InitializeAsync(string url, long revision, CancellationToken token)
    {
        if (!IsCurrentRequest(url, revision, token))
        {
            return;
        }

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
                if (!IsCurrentRequest(url, revision, token))
                {
                    return;
                }

                _nextAttemptUtc = DateTime.UtcNow.AddMinutes(5);
                ClearListing();
                _log.Info($"Listing endpoint [{url}] is unavailable (HTTP {www.responseCode}); checking again after five minutes.");
                return;
            }

            if (!IsCurrentRequest(url, revision, token))
            {
                return;
            }

            string json = www.downloadHandler.text;
            Listing? listing = await ListingPreparationWorker.ParseAsync(json);
            if (!IsCurrentRequest(url, revision, token))
            {
                return;
            }

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
            if (!IsCurrentRequest(url, revision, token) || !ReferenceEquals(listing, Listing))
            {
                return;
            }

            if (_config.LastEvent.Title != listing.Title)
            {
                MapDownloadingManager.PurgeCache();
                _config.LastEvent = new EventInfo
                {
                    Title = listing.Title
                };
            }

            if (IsCurrentRequest(url, revision, token) && ReferenceEquals(listing, Listing))
            {
                _bannerTask = GetBannerImage(listing, url, revision, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Initialize owns this task; cancellation must not leave an unobserved fault.
        }
        catch (Exception e)
        {
            if (!IsCurrentRequest(url, revision, token))
            {
                return;
            }

            _log.Warn($"Exception while loading listing\n{e}");
            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(30);
            ClearListing();
        }
    }
}
