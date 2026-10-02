using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.ViewControllers;
using HarmonyLib;
using HMUI;
using IPA.Utilities.Async;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Controllers;
using Synapse.HarmonyPatches;
using Synapse.Managers;
using Synapse.Networking.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace Synapse.Views;

[ViewDefinition("Synapse.Resources.LobbyChat.bsml")]
internal class EventLobbyChatViewController : BSMLAutomaticViewController
{
    [UIComponent("chat")]
    private readonly VerticalLayoutGroup _chatObject = null!;

    [UIComponent("modal")]
    private readonly ModalView _modal = null!;

    [UIComponent("player-count")]
    private readonly TextMeshProUGUI _playerCount = null!;

    [UIComponent("division-setting")]
    private readonly DropDownListSetting _divisionSetting = null!;

    [UIObject("replay-intro-button")]
    private readonly GameObject _replayIntroObject = null!;

    [UIObject("replay-outro-button")]
    private readonly GameObject _replayOutroObject = null!;

    [UIComponent("scrollview")]
    private readonly ScrollView _scrollView = null!;

    [UIComponent("textbox")]
    private readonly VerticalLayoutGroup _textObject = null!;

    [UIObject("toend")]
    private readonly GameObject _toEndObject = null!;

    [UIComponent("priority-bg")]
    private readonly Backgroundable _priorityBg = null!;

    [UIComponent("priority-bg")]
    private readonly VerticalLayoutGroup _priorityVertical = null!;

    private readonly List<ChatMessage> _messageQueue = [];
    private readonly LinkedList<(ChatMessage ChatMessage, TextMeshProUGUI TextMesh)> _messages = [];

    private readonly Stack<PriorityMessage> _disabledPriorityMessages = [];
    private readonly List<PriorityMessage> _priorityMessages = [];

    private SiraLog _log = null!;
    private Config _config = null!;
    private MessageManager _messageManager = null!;
    private NetworkManager _networkManager = null!;
    private ListingManager _listingManager = null!;
    private IInstantiator _instantiator = null!;
    private EventLeaderboardViewController _leaderboardViewController = null!;
    private InputFieldView _input = null!;
    private KeyboardOpener _keyboardOpener = null!;
    private OkRelay _okRelay = null!;

    private string _playerCountText = string.Empty;
    private readonly HashSet<string> _preparationBans = [];
    private Task<PreparedBatch>? _preparationTask;
    private MessageRequest? _preparationRequest;
    private int _messageRevision;
    private int _ownerThreadId;
    private bool _viewActive;

    internal event Action? IntroStarted;

    internal event Action? OutroStarted;

    [UsedImplicitly]
    [UIValue("join-chat")]
    private bool JoinChat
    {
        get => _config.JoinChat ?? false;
        set
        {
            _config.JoinChat = value;
            _ = _networkManager.Send(ServerOpcode.SetChatter, value);
        }
    }

    [UsedImplicitly]
    [UIValue("mute-music")]
    private bool DisableLobbyAudio
    {
        get => _config.DisableLobbyAudio;
        set => _config.DisableLobbyAudio = value;
    }

    [UsedImplicitly]
    [UIValue("profanity-filter")]
    private bool ProfanityFilter
    {
        get => _config.ProfanityFilter;
        set => _config.ProfanityFilter = value;
    }

    [UsedImplicitly]
    [UIValue("join-leave-messages")]
    private bool ShowJoinLeaveMessages
    {
        get => _config.ShowJoinLeaveMessages;
        set => _config.ShowJoinLeaveMessages = value;
    }

    [UsedImplicitly]
    [UIValue("division")]
    private int Division
    {
        get => _config.LastEvent.Division ?? 0;
        set
        {
            _config.LastEvent.Division = value;
            _leaderboardViewController.InvalidateAllScores();
            _ = _networkManager.Send(ServerOpcode.SetDivision, value);
        }
    }

    [UsedImplicitly]
    [UIValue("division-choices")]
    private List<object> DivisionChoices { get; set; } = [0];

#if !V1_29_1
    protected override void OnDestroy()
#else
    public override void OnDestroy()
#endif
    {
        RetirePreparation();
        _messageManager.MessageReceived -= OnMessageReceived;
        _networkManager.UserBanned -= OnUserBanned;
        _networkManager.StageUpdated -= OnStageUpdated;
        _networkManager.PlayerCountUpdated -= OnPlayerCountUpdated;
        if (_okRelay != null)
        {
            _okRelay.OkPressed -= OnOkPressed;
        }

        base.OnDestroy();
    }

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);

        rectTransform.sizeDelta = new Vector2(-40, 0);

        if (firstActivation)
        {
            InputFieldView original =
                Resources.FindObjectsOfTypeAll<InputFieldView>().First(n => n.name == "SearchInputField");
            _input = Instantiate(original, _chatObject.transform);
            _input.name = "EventChatInputField";
            RectTransform rect = (RectTransform)_input.transform;
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 1);
            rect.offsetMin = new Vector2(20, 0);
            rect.offsetMax = new Vector2(-20, -70);
            _input._keyboardPositionOffset = new Vector3(0, 60, 0);
            _input._textLengthLimit = 200;
            _input._textView.richText = false;
            _keyboardOpener = _instantiator.InstantiateComponent<KeyboardOpener>(_input.gameObject);
            RectTransform bg = (RectTransform)rect.Find("BG");
            bg.offsetMin = new Vector2(0, -4);
            bg.offsetMax = new Vector2(0, 4);
            Transform placeholderText = rect.Find("PlaceholderText");

            // its in Polyglot and im too lazy to add its reference
            // ReSharper disable once Unity.UnresolvedComponentOrScriptableObject
            Destroy(placeholderText.GetComponent("LocalizedTextMeshProUGUI"));
            placeholderText.GetComponent<CurvedTextMeshPro>().text = "Chat";
            ((RectTransform)placeholderText).offsetMin = new Vector2(4, 0);
            ((RectTransform)rect.Find("Text")).offsetMin = new Vector2(4, -4);
            Destroy(rect.Find("Icon").gameObject);

            _scrollView.gameObject.AddComponent<ScrollViewScrollToEnd>().Construct(_toEndObject);
            _toEndObject.SetActive(false);

            _okRelay = _input.gameObject.AddComponent<OkRelay>();
            _okRelay.OkPressed += OnOkPressed;

            _input.gameObject.AddComponent<LayoutElement>().minHeight = 10;

#if PRE_V1_40_8
            ImageView priorityBg = (ImageView)_priorityBg.background;
#else
            ImageView priorityBg = (ImageView)_priorityBg.Background;
#endif
            priorityBg._skew = 0;
            priorityBg._gradientDirection = ImageView.GradientDirection.Vertical;
            priorityBg.gradient = true;
            priorityBg.color = Color.white;
            priorityBg.color0 = new Color(0.13f, 0.01f, 0.09f);
            priorityBg.color1 = new Color(0.09f, 0.01f, 0.13f);
            ((RectTransform)_priorityVertical.transform).pivot = new Vector2(0.5f, 1);
            _priorityVertical.gameObject.SetActive(false);
        }

        // ReSharper disable once InvertIf
        if (addedToHierarchy)
        {
            _viewActive = true;
            _messageManager.MessageReceived += OnMessageReceived;
            _messageManager.RefreshMotd();
            _networkManager.UserBanned += OnUserBanned;
            OnStageUpdated(_networkManager.Status.Stage);
            _networkManager.StageUpdated += OnStageUpdated;

            Listing? listing = _listingManager.Listing;
            GameObject parent = _divisionSetting.transform.parent.gameObject;
            if (listing != null)
            {
                parent.SetActive(listing.Divisions.Count > 0);
                DivisionChoices = Enumerable.Range(0, Math.Max(listing.Divisions.Count, 1)).Cast<object>().ToList();
            }
            else
            {
                parent.SetActive(false);
                DivisionChoices = [0];
            }

#if !PRE_V1_40_8
            _divisionSetting.Values = DivisionChoices;
#else
            _divisionSetting.values = DivisionChoices;
#endif
            _divisionSetting.UpdateChoices();
            _divisionSetting.Value = Division;
        }
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);

        // ReSharper disable once InvertIf
        if (removedFromHierarchy)
        {
            RetirePreparation();
            _priorityVertical.gameObject.SetActive(false);
            _priorityMessages.Clear();
            _disabledPriorityMessages.Clear();
            foreach (Transform obj in _priorityVertical.transform)
            {
                Destroy(obj.gameObject);
            }

            _messageQueue.Clear();
            _messages.Clear();
            foreach (Transform obj in _textObject.transform)
            {
                Destroy(obj.gameObject);
            }

            _messageManager.MessageReceived -= OnMessageReceived;
            _networkManager.UserBanned -= OnUserBanned;
            _networkManager.StageUpdated -= OnStageUpdated;
        }

        _keyboardOpener.Close();
    }

    [UsedImplicitly]
    [Inject]
    private void Construct(
        SiraLog log,
        Config config,
        MessageManager messageManager,
        NetworkManager networkManager,
        ListingManager listingManager,
        IInstantiator instantiator,
        EventLeaderboardViewController leaderboardViewController)
    {
        _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        _log = log;
        _config = config;
        _messageManager = messageManager;
        _networkManager = networkManager;
        _listingManager = listingManager;
        _instantiator = instantiator;
        _leaderboardViewController = leaderboardViewController;
        networkManager.PlayerCountUpdated += OnPlayerCountUpdated;
    }

    private void OnMessageReceived(ChatMessage message)
    {
        if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
        {
            int revision = Volatile.Read(ref _messageRevision);
            UnityMainThreadTaskScheduler.Factory.StartNew(
                () =>
                {
                    if (this && _viewActive && revision == _messageRevision)
                    {
                        _messageQueue.Add(message);
                    }
                });
            return;
        }

        if (_viewActive)
        {
            _messageQueue.Add(message);
        }
    }

    private void OnOkPressed()
    {
        string text = _input.text;
        _input.ClearInput();
        _messageManager.SendMessage(text);
    }

    [UsedImplicitly]
    [UIAction("division-format")]
    private string DivisionFormat(int value)
    {
        Listing? listing = _listingManager.Listing;
        return listing is { Divisions.Count: > 0 } ? listing.Divisions[value].Name : "N/A";
    }

    [UsedImplicitly]
    [UIAction("replay-intro")]
    private void OnReplayIntroClick()
    {
        IntroStarted?.Invoke();
    }

    [UsedImplicitly]
    [UIAction("replay-outro")]
    private void OnReplayOutroClick()
    {
        OutroStarted?.Invoke();
    }

    private void OnPlayerCountUpdated(int chatters, int online)
    {
        _playerCountText = $"\ud83d\udcac {chatters} / \ud83d\udc64 {online}";
    }

    private void OnStageUpdated(IStageStatus stageStatus)
    {
        int revision = Volatile.Read(ref _messageRevision);
        UnityMainThreadTaskScheduler.Factory.StartNew(
            () =>
            {
                if (!this || !_viewActive || revision != _messageRevision ||
                    !ReferenceEquals(stageStatus, _networkManager.Status.Stage))
                {
                    return;
                }

                _replayIntroObject.SetActive(stageStatus is not IntroStatus);
                _replayOutroObject.SetActive(stageStatus is FinishStatus);
            });
    }

    [UsedImplicitly]
    [UIAction("toend-click")]
    private void OnToEndClick()
    {
        _scrollView.ScrollToEnd(true);
    }

    private void OnUserBanned(string id)
    {
        if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
        {
            int revision = Volatile.Read(ref _messageRevision);
            UnityMainThreadTaskScheduler.Factory.StartNew(
                () =>
                {
                    if (this && _viewActive && revision == _messageRevision)
                    {
                        OnUserBanned(id);
                    }
                });
            return;
        }

        if (!_viewActive)
        {
            return;
        }

        if (_preparationTask != null)
        {
            _preparationBans.Add(id);
        }

        _messageQueue.RemoveAll(n => n.Id == id);

        bool scrollToEnd = AtEnd();
        float heightLost = 0;
        _messages.Where(n => n.ChatMessage.Id == id).Do(
            n =>
            {
                TextMeshProUGUI textMesh = n.TextMesh;
                float height = textMesh.rectTransform.rect.height;
                textMesh.text = "<deleted>";
                heightLost += textMesh.rectTransform.rect.height - height;
            });
        ScrollLostHeight(scrollToEnd, heightLost);
    }

    [UsedImplicitly]
    [UIAction("show-modal")]
    private void ShowModal()
    {
        _modal.Show(true);
    }

    private bool AtEnd()
    {
        float end = _scrollView.contentSize - _scrollView.scrollPageSize;
        return end < 0 || Mathf.Abs(end - _scrollView._destinationPos) < 0.01f;
    }

    private void ScrollLostHeight(bool scrollToEnd, float heightLost)
    {
        Canvas.ForceUpdateCanvases();
        RectTransform contentTransform = _scrollView._contentRectTransform;
        _scrollView.SetContentSize(contentTransform.rect.height);
        float heightDiff;
        if (scrollToEnd)
        {
            heightDiff = _scrollView.contentSize - _scrollView.scrollPageSize - _scrollView._destinationPos;
        }
        else if (heightLost > 0)
        {
            heightDiff = -heightLost;
        }
        else
        {
            return;
        }

        _scrollView._destinationPos = Mathf.Max(_scrollView._destinationPos + heightDiff, 0);
        _scrollView.RefreshButtons();
        float newY = Mathf.Max(contentTransform.anchoredPosition.y + heightDiff, 0);
        contentTransform.anchoredPosition = new Vector2(0, newY);
        _scrollView.UpdateVerticalScrollIndicator(Mathf.Abs(newY));
    }

    private void Update()
    {
        if (_playerCountText != _playerCount.text)
        {
            _playerCount.text = _playerCountText;
        }

        if (_priorityMessages.Count > 0)
        {
            float dTime = Time.deltaTime;
            for (int i = _priorityMessages.Count - 1; i >= 0; i--)
            {
                PriorityMessage priorityMessage = _priorityMessages[i];
                priorityMessage.Time -= dTime;
                if (priorityMessage.Time > 0)
                {
                    continue;
                }

                priorityMessage.Text.gameObject.SetActive(false);
                _disabledPriorityMessages.Push(priorityMessage);
                _priorityMessages.Remove(priorityMessage);
            }

            if (_priorityMessages.Count == 0)
            {
                _priorityVertical.gameObject.SetActive(false);
            }
        }

        if (_preparationTask == null)
        {
            StartPreparation();
            return;
        }

        if (!_preparationTask.IsCompleted)
        {
            return;
        }

        Task<PreparedBatch> task = _preparationTask;
        MessageRequest request = _preparationRequest!;
        try
        {
            PreparedBatch batch = task.GetAwaiter().GetResult();
            if (request.Retired || !_viewActive || request.Revision != _messageRevision)
            {
                return;
            }

            if (request.Filter != _config.ProfanityFilter)
            {
                _messageQueue.InsertRange(0, request.Messages.Where(n => !_preparationBans.Contains(n.Id)));
                return;
            }

            bool scrollToEnd = AtEnd();
            float heightLost = 0;
            foreach (PreparedMessage prepared in batch.Messages)
            {
                if (!this || !_viewActive || request.Revision != _messageRevision)
                {
                    return;
                }

                ChatMessage message = prepared.Message;
                if (_preparationBans.Contains(message.Id))
                {
                    continue;
                }

                string content = prepared.Content;
                Color color = message.Type is MessageType.WhisperFrom or MessageType.WhisperTo
                    ? Color.magenta
                    : Color.white;

                if (message.Type == MessageType.PrioritySystem)
                {
                    _priorityVertical.gameObject.SetActive(true);

                    PriorityMessage priorityMessage;
                    if (_priorityMessages.Count >= 5)
                    {
                        priorityMessage = _priorityMessages.First();
                        TextMeshProUGUI text = priorityMessage.Text;
                        text.color = color;
                        text.text = content;
                        text.transform.SetAsLastSibling();
                    }
                    else if (_disabledPriorityMessages.Count == 0)
                    {
                        TextMeshProUGUI prio =
                            Synapse.Extras.UICompatibility.CreateText(
                                (RectTransform)_priorityBg.transform,
                                content,
                                Vector2.zero);
                        Synapse.Extras.UICompatibility.SetWrapping(prio, true);
                        prio.richText = true;
                        prio.color = color;
                        prio.alignment = TextAlignmentOptions.Left;
                        prio.fontSize = 4;
                        priorityMessage = new PriorityMessage(prio);
                        _priorityMessages.Add(priorityMessage);
                    }
                    else
                    {
                        priorityMessage = _disabledPriorityMessages.Pop();
                        TextMeshProUGUI text = priorityMessage.Text;
                        text.gameObject.SetActive(true);
                        text.color = color;
                        text.text = content;
                        text.transform.SetAsLastSibling();
                        _priorityMessages.Add(priorityMessage);
                    }

                    priorityMessage.Time = 20;
                }

                if (_messages.Count > 100)
                {
                    LinkedListNode<(ChatMessage, TextMeshProUGUI)> first = _messages.First;
                    _messages.RemoveFirst();
                    TextMeshProUGUI text = first.Value.Item2;
                    float height = text.rectTransform.rect.height;
                    text.color = color;
                    text.text = content;
                    text.transform.SetAsLastSibling();
                    first.Value = (message, text);
                    _messages.AddLast(first);
                    heightLost += height;
                }
                else
                {
                    TextMeshProUGUI text =
                        Synapse.Extras.UICompatibility.CreateText(
                            (RectTransform)_textObject.transform,
                            content,
                            Vector2.zero);
                    Synapse.Extras.UICompatibility.SetWrapping(text, true);
                    text.richText = true;
                    text.color = color;
                    text.alignment = TextAlignmentOptions.Left;
                    text.fontSize = 4;
                    _messages.AddLast((message, text));
                }

                ScrollLostHeight(scrollToEnd, heightLost);
            }

            if (batch.Error != null)
            {
                _log.Error($"Exception while processing message\n{batch.Error}");
            }
        }
        catch (Exception e)
        {
            _log.Error($"Exception while processing message\n{e}");
        }
        finally
        {
            _preparationBans.Clear();
            _preparationTask = null;
            _preparationRequest = null;
        }
    }

    private void StartPreparation()
    {
        if (!_viewActive || _messageQueue.Count == 0)
        {
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        bool knownCulture = culture.GetType() == typeof(CultureInfo);
        MessageRequest request = new(
            _messageQueue.ToArray(),
            _config.ProfanityFilter,
            knownCulture ? CultureInfo.ReadOnly((CultureInfo)culture.Clone()) : culture,
            _messageRevision);
        Task<PreparedBatch> task = knownCulture ? Task.Factory.StartNew(
            MessagePreparation.Process,
            request,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default) : Task.FromResult(MessagePreparation.Process(request));
        _messageQueue.Clear();
        _preparationRequest = request;
        _preparationTask = task;
    }

    private void RetirePreparation()
    {
        _viewActive = false;
        Interlocked.Increment(ref _messageRevision);
        if (_preparationRequest != null)
        {
            _preparationRequest.Retired = true;
        }

        // Keep the physical task until completion before starting another batch.
        _messageQueue.Clear();
        _preparationBans.Clear();
    }

    private sealed class MessageRequest(ChatMessage[] messages, bool filter, CultureInfo culture, int revision)
    {
        internal readonly ChatMessage[] Messages = messages;
        internal readonly bool Filter = filter;
        internal readonly CultureInfo Culture = culture;
        internal readonly int Revision = revision;
        internal volatile bool Retired;
    }

    private readonly struct PreparedMessage(ChatMessage message, string content)
    {
        internal ChatMessage Message { get; } = message;

        internal string Content { get; } = content;
    }

    private sealed class PreparedBatch(PreparedMessage[] messages, Exception? error)
    {
        internal PreparedMessage[] Messages { get; } = messages;

        internal Exception? Error { get; } = error;
    }

    private static class MessagePreparation
    {
        private static readonly Lazy<ProfanityFilter.ProfanityFilter> _filter = new(() => new ProfanityFilter.ProfanityFilter());

        internal static PreparedBatch Process(object? state)
        {
            MessageRequest request = (MessageRequest)state!;
            List<PreparedMessage> messages = new(request.Messages.Length);
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = request.Culture;
                foreach (ChatMessage message in request.Messages)
                {
                    if (request.Retired)
                    {
                        break;
                    }

                    string content;
                    string messageString = message.Message;
                    string usernameString = message.Username;
                    string? colorString = message.Color;
                    if (message.Type is not MessageType.System and not MessageType.PrioritySystem && request.Filter)
                    {
                        messageString = _filter.Value.CensorString(messageString);
                        usernameString = _filter.Value.CensorString(usernameString);
                    }

                    switch (message.Type)
                    {
                        case MessageType.PrioritySystem:
                        case MessageType.System:
                            content = Colorize(messageString, colorString);
                            break;
                        case MessageType.WhisperFrom:
                            content = $"[From {Colorize(NoParse(usernameString), colorString)}] {NoParse(messageString)}";
                            break;
                        case MessageType.WhisperTo:
                            content = $"[To {Colorize(NoParse(usernameString), colorString)}] {NoParse(messageString)}";
                            break;
                        case MessageType.Say:
                        default:
                            content = $"[{Colorize(NoParse(usernameString), colorString)}] {NoParse(messageString)}";
                            break;
                    }

                    messages.Add(new PreparedMessage(message, content));
                }

                return new PreparedBatch(messages.ToArray(), null);
            }
            catch (Exception error)
            {
                return new PreparedBatch(messages.ToArray(), error);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static string NoParse(string message)
        {
            StringBuilder stringBuilder = new(message.Length);
            foreach (char c in message)
            {
                if (c is '<' or '>')
                {
                    stringBuilder.Append($"<noparse>{c}</noparse>");
                }
                else
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString();
        }

        private static string Colorize(string message, string? color)
        {
            if (color == null)
            {
                return message;
            }

            return color[0] != '#' ? $"<color=\"{color}\">{message}</color>" : $"<color={color}>{message}</color>";
        }
    }

    private class PriorityMessage
    {
        internal PriorityMessage(TextMeshProUGUI text)
        {
            Text = text;
        }

        internal TextMeshProUGUI Text { get; }

        internal float Time { get; set; }
    }
}
