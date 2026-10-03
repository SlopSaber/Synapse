using System;
using HMUI;
using IPA.Utilities;
using JetBrains.Annotations;
using Synapse.Controllers;
using Synapse.Extras;
using Synapse.Networking.Models;
using Synapse.Views;
using TMPro;
using Tweening;
using UnityEngine;
using UnityEngine.UI;
using Zenject;
using Object = UnityEngine.Object;

namespace Synapse.Managers;

internal class PromoManager : IInitializable, ITickable, IDisposable
{
    private static readonly Sprite _promoBack =
        MediaExtensions.GetEmbeddedResourceSprite("Synapse.Resources.promo_back.png");

    private static readonly Sprite _promoPlaceholder =
        MediaExtensions.GetEmbeddedResourceSprite("Synapse.Resources.promo_placeholder.png");

    private static readonly FieldAccessor<SelectableStateController, TimeTweeningManager>.Accessor
        _tweeningAccessor =
            FieldAccessor<SelectableStateController, TimeTweeningManager>.GetAccessor("_tweeningManager");

    private readonly IInstantiator _instantiator;
    private readonly EventModsViewController _eventModsViewController;

    private readonly ListingManager _listingManager;

    private readonly MainMenuViewController _mainMenuViewController;
    private readonly TimeTweeningManager _tweeningManager;
    private ImageView _bannerTextBg = null!;

    private Button? _button;
    private ImageView _imageView = null!;

    private Listing? _listing;
    private GameObject _originalBanner = null!;
    private GameObject _promoBanner = null!;

    private ImageViewRainbowController _rainbow = null!;
    private TextMeshProUGUI _textMesh = null!;
    private CanvasGroupTransitionSO _transition = null!;

    private bool _finished;

    [UsedImplicitly]
    private PromoManager(
        MainMenuViewController mainMenuViewController,
        ListingManager listingManager,
        TimeTweeningManager tweeningManager,
        IInstantiator instantiator,
        EventModsViewController eventModsViewController)
    {
        _instantiator = instantiator;
        _eventModsViewController = eventModsViewController;
        _mainMenuViewController = mainMenuViewController;
        _listingManager = listingManager;
        _tweeningManager = tweeningManager;
        listingManager.ListingFound += OnListingFound;
        listingManager.BannerImageCreated += OnBannerImageCreated;
    }

    internal Button Button => _button ??= CreateButton();

    internal bool Active { get; private set; }

    public void Dispose()
    {
        _listingManager.ListingFound -= OnListingFound;
        _listingManager.BannerImageCreated -= OnBannerImageCreated;
    }

    public void Initialize()
    {
        RefreshListing(_listing);
    }

    public void Tick()
    {
        if (_finished || _listing == null)
        {
            return;
        }

        TimeSpan span = _listing.Time.ToTimeSpan();

        if (span.Ticks < 0)
        {
            Active = true;
            _finished = true;
            _rainbow.enabled = true;
            _transition._normalAlpha = 0.8f;
            _transition._highlightedAlpha = 1;
            Button.enabled = true;
            NoTransitionButtonSelectableStateController controller =
                _promoBanner.GetComponent<NoTransitionButtonSelectableStateController>();
            controller.ResolveSelectionState(controller._component.selectionState, false);
            _textMesh.text = $"{_listing.Title}\n<size=120%>LIVE NOW</size>";
        }
        else if (span.TotalHours < 1 && _eventModsViewController.MissingMods is { Count: > 0 })
        {
            Active = true;
            _transition._normalAlpha = 0.8f;
            _transition._highlightedAlpha = 1;
            Button.enabled = true;
            NoTransitionButtonSelectableStateController controller =
                _promoBanner.GetComponent<NoTransitionButtonSelectableStateController>();
            controller.ResolveSelectionState(controller._component.selectionState, false);
            _textMesh.text = $"{_listing.Title}\nREADY TO DOWNLOAD";
        }
        else
        {
            string time = span.Days switch
            {
                > 1 => $"{span.Days} days",
                > 0 => $"{span.Days} day",
                _ => $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"
            };

            _textMesh.text = $"{_listing.Title}\n<size=120%>{time}</size>";
        }
    }

    private Button CreateButton()
    {
        Button originalButton = _mainMenuViewController._musicPackPromoButton;
        GameObject original = originalButton.gameObject;
        Button newButton = Object.Instantiate(originalButton, original.transform.parent);
        GameObject newObject = newButton.gameObject;
        _promoBanner = newObject;
        _originalBanner = original;

        MusicPackPromoBanner musicPackPromoBanner = newObject.GetComponent<MusicPackPromoBanner>();
#if !PRE_V1_37_1
        Object.Destroy(musicPackPromoBanner._loadingIndicator);
        musicPackPromoBanner._promoBannerGo.SetActive(true);
        musicPackPromoBanner._backgroundImage.gameObject.SetActive(true);
#endif
        Object.Destroy(musicPackPromoBanner);

        SelectableStateController stateController =
            newObject.GetComponent<NoTransitionButtonSelectableStateController>();
        _tweeningAccessor(ref stateController) = _tweeningManager;

        newObject.transform.SetSiblingIndex(original.transform.GetSiblingIndex());

        _textMesh = newObject.GetComponentInChildren<TextMeshProUGUI>();
        _textMesh.text = string.Empty;

        _imageView = newObject.transform.Find("Banner").GetComponent<ImageView>();
        _imageView.sprite = _promoPlaceholder;
        GameObject originalImage = _imageView.gameObject;
        GameObject newImage = Object.Instantiate(originalImage, newObject.transform).gameObject;
        originalImage.transform.localScale = new Vector3(0.96f, 0.99f, 1);
        newImage.transform.SetSiblingIndex(0);
        newImage.GetComponent<ImageView>().sprite = _promoBack;
        _rainbow = _instantiator.InstantiateComponent<ImageViewRainbowController>(newImage);
        ////newButton.transform.localScale = new Vector3(1.00f, 1.0f, 1);

        // its on PromoText in 1.29 and on a child object in 1.34
        _bannerTextBg = newObject.transform.Find("PromoText").GetComponentInChildren<ImageView>(true);

        CanvasGroupStateTransition stateTransition = newObject.GetComponent<CanvasGroupStateTransition>();
        _transition = Object.Instantiate(stateTransition._transition);
        stateTransition._transition = _transition;

        newObject.name = "SynapsePromoBanner";

        _promoBanner = newObject;
        return newButton;
    }

    private void OnBannerImageCreated(Sprite? sprite)
    {
        _imageView.sprite = sprite != null ? sprite : _promoPlaceholder;
    }

    private void OnListingFound(Listing? listing)
    {
        RefreshListing(listing);
    }

    private void RefreshListing(Listing? listing)
    {
        _button ??= CreateButton();
        _listing = listing;
        if (listing == null)
        {
            _promoBanner.SetActive(false);
        }
        else
        {
            _finished = false;
            Active = false;
            _rainbow.enabled = false;
            _transition._normalAlpha = 0.6f;
            _transition._highlightedAlpha = 0.6f;
            Button.enabled = false;
            _originalBanner.SetActive(false);
            _promoBanner.SetActive(true);
            if (ColorUtility.TryParseHtmlString(listing.BannerColor, out Color color))
            {
                _bannerTextBg.color = color;
            }
        }
    }
}
