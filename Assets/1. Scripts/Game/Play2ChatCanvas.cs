using System.Collections.Generic;
using System.Linq;
using TMPro;
using Poker.Application;
using Poker.Foundation;
using Poker.Transport;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public sealed class Play2ChatCanvas : MonoBehaviour
{
    [SerializeField] private GameObject panel;
    [SerializeField] private UnityEngine.UI.ScrollRect scroll;
    [SerializeField] private TMPro.TMP_Text messages;
    [SerializeField] private TMPro.TMP_InputField input;
    [SerializeField] private UnityEngine.UI.Toggle dealer;
    [SerializeField] private UnityEngine.UI.Button send;

    internal const float DockWidth = 344f;
    private float RowSpacing => village ? 4f : 1f;
    private static readonly Color ChatText = Play2UGui.Hex("F0E6CB");
    private static readonly Color GuestText = Play2UGui.Hex("ADCFCA");

    private Play2TablePreview table;
    private HoldemRoomPacket packet;
    private RectTransform panelRect, scrollRect, scrollbarRail, marketHeader;
    private RectTransform chatUnderline;
    private VerticalLayoutGroup chatLayout;
    private bool village;
    private Graphic originalPanel;
    private RectTransform marketDock, marketPanel;
    private Image inputBackground;
    private Button chatTab;
    private readonly List<(RectTransform root, TMP_Text speaker, TMP_Text text, int seat)> rows = new();
    private UnityEngine.UI.Outline inputFocus;
    private GameObject closeHint;
    private long firstMessage = -1, lastMessage = -1;
    private bool composing, recordsOpen, focusRequested;
    private int requestedEditFrame = -1;
    private int releasedFocusFrame = -1;
    private InputAction openChat;
    private TMP_Text historyLabel, chatHeading, unreadLabel;
    private UnityEngine.UI.Button unreadButton;
    private long readThrough;
    private int shownUnread;
    private TMP_Text requestHint, sendLabel, dealerLabel;
    private UnityEngine.UI.Image dealerBackground;
    private Play2RoomSession roomSession;
    private string pendingId, pendingText;
    private bool canRequest;
    private bool deliveryUnconfirmed;
    private float dealerReceiptUntil;
    private string practiceGraceWindow;
    private float practiceGraceUntil;
    private float savedChatOffset;
    private int visibleRecords = -1;
    private readonly List<(int street, int accuser, int target, bool caught)> verdicts = new();
    internal TMPro.TMP_FontAsset Font => messages.font;
    internal bool IsTyping => panel.activeInHierarchy && (input.isFocused || requestedEditFrame == Time.frameCount);

    internal bool WaitingForSpectatorRequest()
    {
        if (!dealer.isOn || !canRequest || packet?.ownUtterances?.windowId == null) return false;
        var self = System.Array.Find(packet.game.seats, seat => seat.viewer);
        if (self == null || (self.status != (int)HoldemSeatStatus.Folded && self.status != (int)HoldemSeatStatus.Busted))
            return false;
        string window = packet.ownUtterances.windowId;
        if (practiceGraceWindow != window)
        {
            practiceGraceWindow = window;
            practiceGraceUntil = Time.unscaledTime + 5f;
        }
        return Time.unscaledTime < practiceGraceUntil;
    }

    internal void Initialize(Play2TablePreview owner)
    {
        table = owner;
        roomSession = Play2RoomSession.Current;
        roomSession.ChatReceived += ReceiveChat;
        BuildLayout();
        input.SetTextWithoutNotify(roomSession.ChatDraft);
        dealer.SetIsOnWithoutNotify(roomSession.ChatDraftToDealer);
        deliveryUnconfirmed = roomSession.ChatDeliveryUnconfirmed;
        roomSession.ClearChatDraft();
        input.richText = input.textComponent.richText = false;
        if (input.placeholder is TMP_Text hint) hint.text = "Enter로 대화하기";
        openChat = new InputAction("Open table chat", InputActionType.Button);
        openChat.AddBinding("<Keyboard>/enter");
        openChat.AddBinding("<Keyboard>/numpadEnter");
        openChat.performed += _ => focusRequested = panel.activeInHierarchy && input.interactable && !input.isFocused;
        openChat.Enable();
        send.onClick.AddListener(Send);
        input.onSelect.AddListener(_ =>
        {
            if (recordsOpen) ToggleHistory();
            requestedEditFrame = Time.frameCount;
        });
        input.onDeselect.AddListener(_ =>
        {
            releasedFocusFrame = Time.frameCount;
            requestedEditFrame = -1;
        });
        input.onSubmit.AddListener(_ =>
        {
            if (string.IsNullOrWhiteSpace(input.text)) focusRequested = true;
            else Send();
        });
        dealer.onValueChanged.AddListener(_ =>
        {
            RefreshComposer();
            // Move keyboard input to the draft before Enter can submit the selected toggle again.
            focusRequested = input.interactable;
        });
        input.onValueChanged.AddListener(_ =>
        {
            deliveryUnconfirmed = false;
            RefreshComposer();
        });
        input.restoreOriginalTextOnEscape = false;
        panel.SetActive(false);
    }

    private void BuildLayout()
    {
        messages.font = Play2HudArt.TableFont(messages.font);
        input.fontAsset = Font;
        panelRect = (RectTransform)panel.transform;
        panelRect.pivot = Vector2.zero;
        originalPanel = panel.GetComponent<Graphic>();
        originalPanel.color = Play2UGui.Hex("152420");
        marketDock = Play2UGui.Panel("Market Chat Dock", panelRect.parent, Vector2.zero, Vector2.zero,
            Play2UGui.Hex("253629"));
        marketDock.SetAsFirstSibling();
        marketDock.anchorMin = Vector2.zero;
        marketDock.anchorMax = Vector2.up;
        marketDock.offsetMin = Vector2.zero;
        marketDock.offsetMax = new Vector2(DockWidth, -Play2TableHud.VillageBarHeight);
        marketDock.gameObject.SetActive(false);
        marketPanel = Play2UGui.Fill("Market Chat Surface", panelRect);
        Play2HudArt.Apply(Play2UGui.Image(marketPanel, Color.white, true), "MarketPanel");
        var panelShadow = marketPanel.gameObject.AddComponent<Shadow>();
        panelShadow.effectColor = Play2UGui.Hex("28382271");
        panelShadow.effectDistance = new Vector2(0, -3);

        scrollRect = (RectTransform)scroll.transform;
        scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
        messages.enabled = false;
        chatLayout = scroll.content.gameObject.AddComponent<VerticalLayoutGroup>();
        chatLayout.childAlignment = TextAnchor.UpperLeft;
        chatLayout.childControlWidth = chatLayout.childControlHeight = true;
        chatLayout.childForceExpandWidth = true;
        chatLayout.childForceExpandHeight = false;
        chatLayout.spacing = RowSpacing;

        marketHeader = Play2HudArt.Plate("Market Chat Header", panelRect, Vector2.zero, Vector2.zero, "MarketMoss");
        marketHeader.SetAsFirstSibling();
        marketPanel.SetAsFirstSibling();
        marketHeader.anchorMin = Vector2.up;
        marketHeader.anchorMax = Vector2.one;
        marketHeader.offsetMin = new Vector2(0, -36);
        marketHeader.offsetMax = Vector2.zero;

        chatTab = Play2HudArt.Button("Chat Tab", panelRect, "", Vector2.zero, new Vector2(56, 31),
            "MarketButton", ChatText, Font, () => { if (recordsOpen) ToggleHistory(); });
        chatTab.targetGraphic.color = Color.clear;
        var tabRect = (RectTransform)chatTab.transform;
        tabRect.anchorMin = tabRect.anchorMax = tabRect.pivot = Vector2.up;
        tabRect.anchoredPosition = new Vector2(3, -3);
        var label = Play2UGui.Text("Chat Heading", panelRect, "채팅", new Vector2(21, -4), new Vector2(61, 30),
            16, ChatText, Font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = Vector2.up;
        label.rectTransform.anchoredPosition = new Vector2(21, -4);
        chatHeading = label;
        unreadButton = Play2HudArt.Button("New Chat Messages", panelRect, "", Vector2.zero, new Vector2(115, 27),
            "ActionRow", Play2UGui.Gold, Font, ShowLatestMessages, 15);
        var unreadRect = (RectTransform)unreadButton.transform;
        unreadRect.anchorMin = unreadRect.anchorMax = unreadRect.pivot = Vector2.up;
        unreadRect.anchoredPosition = new Vector2(9, -5);
        unreadButton.targetGraphic.color = Play2UGui.PineInset;
        unreadLabel = unreadButton.GetComponentInChildren<TMP_Text>();
        unreadButton.gameObject.SetActive(false);
        var hint = Play2UGui.Text("Chat Close Hint", panelRect, "Esc 입력 종료", new Vector2(-57, -23), new Vector2(100, 20),
            13, Play2UGui.Gold, Font, TextAlignmentOptions.MidlineRight);
        hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = Vector2.one;
        closeHint = hint.gameObject;
        var history = Play2HudArt.Button("History", panelRect, "채택 기록", Vector2.zero, new Vector2(90, 27),
            "ActionRow", Play2UGui.Cream, Font, ToggleHistory, 14);
        history.targetGraphic.color = Color.clear;
        var header = (RectTransform)history.transform;
        header.anchorMin = header.anchorMax = new Vector2(.5f, 1);
        header.anchoredPosition = new Vector2(0, -19);
        historyLabel = history.GetComponentInChildren<TMP_Text>();
        chatUnderline = Play2UGui.Panel("Chat Tab Marker", panelRect, Vector2.zero, new Vector2(28, 3),
            Play2UGui.Hex("F5B96C"));
        chatUnderline.anchorMin = chatUnderline.anchorMax = Vector2.up;
        var divider = Play2UGui.Panel("Chat Divider", panelRect, new Vector2(0, -35), new Vector2(302, 1f),
            Play2UGui.Hex("C1CEC22B"));
        divider.anchorMin = divider.anchorMax = new Vector2(.5f, 1f);

        scrollbarRail = Play2UGui.Panel("Scrollbar Rail", scrollRect, Vector2.zero, Vector2.zero, Play2UGui.PineInset);
        scrollbarRail.anchorMin = Vector2.right;
        scrollbarRail.anchorMax = Vector2.one;
        scrollbarRail.offsetMin = new Vector2(8, 0);
        scrollbarRail.offsetMax = new Vector2(11, 0);
        var track = Play2UGui.Fill("Scrollbar", scrollbarRail);
        Play2UGui.Image(track, Color.clear, true);
        var thumb = Play2UGui.Fill("Thumb", track);
        var thumbImage = Play2UGui.Image(thumb, Play2UGui.Hex("B7C9C58F"));
        var bar = track.gameObject.AddComponent<UnityEngine.UI.Scrollbar>();
        bar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
        bar.handleRect = thumb;
        bar.targetGraphic = thumbImage;
        bar.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        scroll.verticalScrollbar = bar;
        scroll.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.AutoHide;

        var inputRect = (RectTransform)input.transform;
        inputRect.offsetMin = new Vector2(10, 12);
        inputRect.offsetMax = new Vector2(-81, 46);
        if (input.targetGraphic != null)
            input.targetGraphic = inputBackground = Play2HudArt.Skin(input.targetGraphic, "PlayingInput");
        input.textComponent.color = ChatText;
        input.textComponent.fontSize = 17;
        if (input.placeholder is TMP_Text placeholder)
        {
            placeholder.color = Play2UGui.Hex("ABAFA1");
            placeholder.fontStyle = FontStyles.Normal;
            placeholder.fontSize = 16;
        }
        inputFocus = inputRect.gameObject.AddComponent<UnityEngine.UI.Outline>();
        inputFocus.effectColor = Play2UGui.Gold;
        inputFocus.effectDistance = new Vector2(1, 1);
        var dealerRect = (RectTransform)dealer.transform;
        dealerRect.anchorMin = dealerRect.anchorMax = dealerRect.pivot = Vector2.zero;
        dealerRect.anchoredPosition = new Vector2(11, 51);
        dealerRect.sizeDelta = new Vector2(123, 34);
        foreach (Transform child in dealerRect) child.gameObject.SetActive(false);
        dealer.graphic = null;
        dealer.transition = UnityEngine.UI.Selectable.Transition.None;
        dealerBackground = Play2UGui.Image(Play2UGui.Fill("Request Mode", dealerRect), Play2UGui.PineInset, true);
        dealer.targetGraphic = dealerBackground;
        dealerLabel = Play2UGui.Text("Request Label", dealerRect, "딜러 요청", Vector2.zero, new Vector2(115, 30),
            16, ChatText, Font, style: FontStyles.Bold);
        requestHint = Play2UGui.Text("Request Hint", panelRect, "", new Vector2(238, 67), new Vector2(188, 31),
            13, Play2UGui.Gold, Font, TextAlignmentOptions.MidlineLeft);
        requestHint.rectTransform.anchorMin = requestHint.rectTransform.anchorMax = Vector2.zero;
        Play2HudArt.Fit((TextMeshProUGUI)requestHint, 11);
        var sendRect = (RectTransform)send.transform;
        sendRect.anchorMin = sendRect.anchorMax = sendRect.pivot = Vector2.right;
        sendRect.anchoredPosition = new Vector2(-8, 12);
        sendRect.sizeDelta = new Vector2(69, 34);
        send.targetGraphic = Play2HudArt.Skin(send.targetGraphic, "PlayingSend");
        sendLabel = send.GetComponentInChildren<TMP_Text>();
        sendLabel.font = Font;
        sendLabel.color = ChatText;
        sendLabel.fontSize = 16;
        // Keep the action readable even while the empty composer cannot send.
        var colors = send.colors;
        colors.disabledColor = new Color(.74f, .75f, .70f, 1f);
        send.colors = colors;
        sendLabel.enableAutoSizing = false;
        var caption = sendLabel.rectTransform;
        caption.anchorMin = Vector2.zero; caption.anchorMax = Vector2.one;
        caption.offsetMin = Vector2.zero; caption.offsetMax = Vector2.zero;
        sendLabel.alignment = TextAlignmentOptions.Center;
        send.transform.SetAsLastSibling();
        ApplyLayout();
    }

    internal bool HandleEnter()
    {
        if (!focusRequested) return false;
        focusRequested = false;
        if (!panel.activeInHierarchy || !input.interactable) return false;
        if (recordsOpen) ToggleHistory();
        input.Select();
        input.ActivateInputField();
        requestedEditFrame = Time.frameCount;
        return true;
    }

    internal void DiscardFocusRequest() => focusRequested = false;
    private void OnDestroy()
    {
        PreserveReconnectDraft();
        openChat?.Dispose();
        if (roomSession != null) roomSession.ChatReceived -= ReceiveChat;
    }

    private void PreserveReconnectDraft()
    {
        if (roomSession == null || !roomSession.CanReconnect || packet?.hasGame != true) return;
        roomSession.ChatDraft = input.text;
        roomSession.ChatDraftToDealer = dealer.isOn;
        roomSession.ChatDeliveryUnconfirmed = pendingId != null || deliveryUnconfirmed;
    }

    private void ToggleHistory()
    {
        if (packet?.hasGame != true) return;
        if (!recordsOpen) savedChatOffset = scroll.verticalNormalizedPosition <= .02f ? -1 : scroll.content.anchoredPosition.y;
        recordsOpen = !recordsOpen;
        if (recordsOpen)
        {
            input.DeactivateInputField();
            requestedEditFrame = -1;
        }
        firstMessage = lastMessage = -2;
        visibleRecords = -1;
        RefreshRows(true);
        if (!recordsOpen) RestoreScroll(savedChatOffset >= 0, savedChatOffset);
    }

    private void ShowLatestMessages()
    {
        if (recordsOpen) ToggleHistory();
        RestoreScroll(false, 0);
        UpdateUnread();
    }

    private void UpdateUnread()
    {
        var entries = packet?.chat?.entries;
        if (entries == null || !panel.activeInHierarchy) return;
        long latest = entries.Length == 0 ? 0 : entries[entries.Length - 1].sequence;
        if (!recordsOpen && (scroll.content.rect.height <= scroll.viewport.rect.height
            || scroll.verticalNormalizedPosition <= .01f))
            readThrough = latest;
        int unread = (int)System.Math.Clamp(latest - readThrough, 0L, entries.LongLength);
        if (unread == shownUnread) return;
        shownUnread = unread;
        if (unread > 0) unreadLabel.text = $"새 대화 {unread} ↓";
        StyleTabs();
    }

    private void LateUpdate()
    {
        UpdateUnread();
        if (composing != IsTyping) SetComposing(IsTyping);
        if (dealerReceiptUntil > 0 && Time.unscaledTime >= dealerReceiptUntil)
        {
            dealerReceiptUntil = 0;
            RefreshComposer();
        }
    }

    internal bool CaptureTableInput()
    {
        // TMP can finish editing before the table processes the same Escape or click.
        if (!panel.activeInHierarchy || (!recordsOpen && !IsTyping && !composing && releasedFocusFrame != Time.frameCount)) return false;
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        bool clickedOutside = mouse != null && mouse.leftButton.wasPressedThisFrame
            && !RectTransformUtility.RectangleContainsScreenPoint(panelRect, mouse.position.ReadValue());
        if (keyboard?.escapeKey.wasPressedThisFrame == true || clickedOutside)
        {
            var events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != null
                && events.currentSelectedGameObject.transform.IsChildOf(panelRect))
                events.SetSelectedGameObject(null);
            input.DeactivateInputField();
            if (recordsOpen) ToggleHistory();
            requestedEditFrame = -1;
        }
        return true;
    }

    internal void SetVillageTheme(bool value)
    {
        village = value;
        bool keepOffset = scroll.verticalNormalizedPosition > .01f;
        float offset = scroll.content.anchoredPosition.y;
        originalPanel.enabled = !village;
        marketPanel.gameObject.SetActive(village);
        marketDock.gameObject.SetActive(village && panel.activeSelf);
        marketHeader.gameObject.SetActive(village);
        chatLayout.spacing = RowSpacing;
        sendLabel.color = village ? Play2HudArt.MarketLightText : ChatText;
        unreadLabel.color = village ? Play2HudArt.MarketLightText : Play2UGui.Gold;
        requestHint.color = village ? Play2HudArt.MarketInk : Play2UGui.Gold;
        unreadButton.targetGraphic.color = village ? Play2UGui.Hex("466C4C") : Play2UGui.PineInset;
        var history = (RectTransform)historyLabel.transform.parent;
        history.anchorMin = history.anchorMax = new Vector2(village ? 0 : .5f, 1);
        history.anchoredPosition = new Vector2(village ? 95 : 0, -19);
        chatHeading.rectTransform.anchoredPosition = new Vector2(village ? 13 : 21, -4);
        ((RectTransform)unreadButton.transform).anchoredPosition = new Vector2(village ? 187 : 9, -5);
        StyleTabs();
        input.textComponent.color = village ? Play2HudArt.MarketInk : ChatText;
        if (input.placeholder is TMP_Text placeholder)
            placeholder.color = village ? Play2HudArt.MarketMuted : Play2UGui.Hex("ABAFA1");
        inputFocus.effectColor = village ? Play2HudArt.MarketEdge : Play2UGui.Gold;
        var colors = send.colors;
        colors.disabledColor = village ? Color.white : new Color(.74f, .75f, .70f, 1f);
        send.colors = colors;
        foreach (var row in rows) PaintRow(row);
        ApplyLayout();
        RefreshComposer();
        if (panel.activeInHierarchy) RestoreScroll(keepOffset, offset);
    }

    private void SetComposing(bool value)
    {
        composing = value;
        closeHint.SetActive(value);
        inputFocus.enabled = value;
        if (inputBackground != null)
            inputBackground.color = village
                ? value ? Color.white : Play2UGui.Hex("FFF9E6")
                : value ? Play2UGui.Hex("24383B") : Play2UGui.Hex("0E191C");
        StyleTabs();
    }

    private void StyleTabs()
    {
        chatTab.gameObject.SetActive(village);
        chatHeading.gameObject.SetActive(village || shownUnread == 0);
        unreadButton.gameObject.SetActive(shownUnread > 0 && (!village || !composing));
        chatUnderline.gameObject.SetActive(village);
        chatUnderline.anchoredPosition = new Vector2(recordsOpen ? 95 : 27, -35);
        var inactive = Play2UGui.Hex("D4DCC1");
        chatHeading.color = village ? recordsOpen ? inactive : Play2HudArt.MarketLightText : ChatText;
        historyLabel.color = village ? recordsOpen ? Play2HudArt.MarketLightText : inactive : ChatText;
    }

    private void ApplyLayout()
    {
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.up;
        panelRect.offsetMin = village ? new Vector2(12, 12) : Vector2.zero;
        panelRect.offsetMax = village ? new Vector2(DockWidth - 12, -Play2TableHud.VillageBarHeight - 12) : new Vector2(DockWidth, 0);
        scrollRect.offsetMin = new Vector2(village ? 8 : 12, village ? 86 : 87);
        scrollRect.offsetMax = new Vector2(village ? -13 : -23, -42);
        var dealerRect = (RectTransform)dealer.transform;
        dealerRect.sizeDelta = new Vector2(village ? 92 : 123, 34);
        dealerLabel.rectTransform.sizeDelta = new Vector2(village ? 86 : 115, 30);
        requestHint.rectTransform.anchoredPosition = new Vector2(village ? 205 : 238, 67);
        SetComposing(composing);
        scrollbarRail.gameObject.SetActive(true);
    }

    private void RestoreScroll(bool keepOffset, float offset)
    {
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
        scroll.StopMovement();
        float maxOffset = scroll.content.rect.height - scroll.viewport.rect.height;
        scroll.verticalNormalizedPosition = keepOffset && maxOffset > 0 ? 1f - Mathf.Clamp01(offset / maxOffset) : 0f;
    }

    internal void Present(HoldemRoomPacket value)
    {
        if (ReferenceEquals(packet, value)) return;
        if (value?.hasGame != true) PreserveReconnectDraft();
        if (packet?.sessionId != value?.sessionId || packet?.viewerSeat != value?.viewerSeat)
            readThrough = value?.chat?.entries?.LastOrDefault()?.sequence ?? 0;
        if (packet?.sessionId != value?.sessionId || packet?.viewerSeat != value?.viewerSeat
            || packet?.game?.handId != value?.game?.handId)
        {
            verdicts.Clear();
            visibleRecords = -1;
            firstMessage = lastMessage = -2;
        }
        packet = value;
        bool visible = packet?.hasGame == true && packet.hasChat;
        panel.SetActive(visible);
        marketDock.gameObject.SetActive(village && visible);
        if (!visible)
        {
            firstMessage = lastMessage = -1;
            recordsOpen = focusRequested = false;
            shownUnread = 0;
            pendingId = pendingText = null;
            dealerReceiptUntil = 0;
            visibleRecords = -1;
            foreach (var row in rows) row.root.gameObject.SetActive(false);
            input.SetTextWithoutNotify("");
            dealer.SetIsOnWithoutNotify(false);
            SetComposing(false);
            return;
        }

        canRequest = !roomSession.PracticeDealerMissing && !packet.game.hasResult && !packet.paused && packet.hasOwnUtterances && packet.ownUtterances.canSubmit
            && (packet.ownUtterances.unlimitedMessages || packet.ownUtterances.remaining > 0)
            && packet.game.street < (int)HoldemStreet.River;
        input.characterLimit = packet.chat.maximumTextLength;
        RefreshComposer();

        if (packet.game.accusationOutcomes != null)
            foreach (var outcome in packet.game.accusationOutcomes)
                RememberVerdict(outcome.street, outcome.accuser, outcome.target, outcome.hit);
        var verdict = packet.hasTurnAccusation ? packet.turnAccusation : null;
        if (verdict != null && verdict.handId == packet.game.handId)
            RememberVerdict(verdict.street, verdict.accuser, verdict.target, verdict.caught);
        RefreshRows(false);
    }

    private void RememberVerdict(int street, int accuser, int target, bool caught)
    {
        var verdict = (street, accuser, target, caught);
        if (!verdicts.Contains(verdict)) verdicts.Add(verdict);
    }

    private void RefreshRows(bool switching)
    {
        var records = packet.dealerSelections;
        int count = (records?.Length ?? 0) + verdicts.Count;
        historyLabel.text = recordsOpen && !village ? "대화로" : count > 0 ? $"판 기록 {count}" : "판 기록";
        StyleTabs();
        if (recordsOpen)
        {
            if (visibleRecords == count && !switching) return;
            visibleRecords = count;
            var allowed = HoldemDealerSelectionPacketReader.Read(packet);
            for (int i = 0; i < allowed.Count; i++)
            {
                var item = allowed[i];
                string card = item.AppliedCard.HasValue ? $"공용 {item.BoardIndex.Value + 1}번  {Play2HandGuide.CardText(item.AppliedCard.Value)}" : "문장 채택";
                string meaning = string.IsNullOrWhiteSpace(item.Interpretation) ? "" : "\n" + item.Interpretation;
                SetRow(i, Name(item.Speaker.Value), item.Text + meaning + "\n" + card, item.Speaker.Value);
            }
            for (int i = 0; i < verdicts.Count; i++)
            {
                var verdict = verdicts[i];
                string outcome = verdict.caught ? "고발 성공" : "고발 실패";
                SetRow(allowed.Count + i, "고발", $"{Name(verdict.accuser)} → {Name(verdict.target)}  {outcome}");
            }
            if (count == 0) SetRow(0, "딜러", "아직 채택된 문장이나 고발이 없어요.");
            for (int i = Mathf.Max(1, count); i < rows.Count; i++) rows[i].root.gameObject.SetActive(false);
            RestoreScroll(false, 0);
            scroll.verticalNormalizedPosition = 1;
            return;
        }
        var entries = packet.chat.entries;
        long oldest = entries.Length == 0 ? -1 : entries[0].sequence;
        long newest = entries.Length == 0 ? -1 : entries[entries.Length - 1].sequence;
        if (oldest == firstMessage && newest == lastMessage) return;
        bool keepScroll = scroll.content.rect.height > scroll.viewport.rect.height
            && scroll.verticalNormalizedPosition > .01f;
        float offset = scroll.content.anchoredPosition.y;
        if (keepScroll && firstMessage >= 0 && oldest > firstMessage)
        {
            int removed = (int)System.Math.Min(rows.Count, oldest - firstMessage);
            for (int i = 0; i < removed; i++) offset -= rows[i].root.rect.height + RowSpacing;
            offset = Mathf.Max(0, offset);
        }
        firstMessage = oldest;
        lastMessage = newest;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            SetRow(i, Name(entry.seat), Play2TableReactions.Describe(entry.text, packet), entry.seat);
        }
        for (int i = entries.Length; i < rows.Count; i++) rows[i].root.gameObject.SetActive(false);
        RestoreScroll(keepScroll, offset);
    }

    private string Name(int seat) => packet.members.FirstOrDefault(m => m.seat == seat)?.name ?? $"{seat}번";

    private void SetRow(int index, string name, string text, int seat = 0)
    {
        if (index == rows.Count) rows.Add(CreateMessageRow());
        var row = rows[index];
        row.seat = seat;
        rows[index] = row;
        row.speaker.text = seat > 0 && seat == packet?.viewerSeat ? "나" : name;
        row.text.text = text;
        PaintRow(row);
        row.root.gameObject.SetActive(true);
    }

    private void PaintRow((RectTransform root, TMP_Text speaker, TMP_Text text, int seat) row)
    {
        var surface = row.root.GetComponent<Image>();
        var layout = row.root.GetComponent<HorizontalLayoutGroup>();
        layout.padding.left = village ? 8 : 0;
        layout.padding.right = village ? 8 : 0;
        Play2HudArt.Apply(surface, "MarketPaper");
        if (!village) surface.color = Color.clear;
        bool own = row.seat > 0 && row.seat == packet?.viewerSeat;
        row.speaker.color = village
            ? own ? Play2HudArt.MarketBrown : Play2HudArt.MarketInk
            : own ? Play2UGui.Gold : GuestText;
        row.text.color = village ? Play2HudArt.MarketInk : ChatText;
    }

    private (RectTransform root, TMP_Text speaker, TMP_Text text, int seat) CreateMessageRow()
    {
        var root = Play2UGui.Rect("Chat Message", scroll.content, Vector2.zero, Vector2.zero);
        Play2UGui.Image(root, Color.clear);
        var line = root.gameObject.AddComponent<HorizontalLayoutGroup>();
        line.childAlignment = TextAnchor.UpperLeft;
        line.childControlWidth = line.childControlHeight = true;
        line.childForceExpandWidth = line.childForceExpandHeight = false;
        line.spacing = 6;

        var speaker = Play2UGui.Text("Speaker", root, "", Vector2.zero, Vector2.zero, 15,
            Play2UGui.Hex("ADCFCA"), Font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        var nameLayout = speaker.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        nameLayout.minWidth = nameLayout.preferredWidth = 64;
        nameLayout.preferredHeight = 25;
        speaker.richText = false;
        speaker.textWrappingMode = TextWrappingModes.NoWrap;
        speaker.overflowMode = TextOverflowModes.Ellipsis;
        Play2HudArt.Fit(speaker, 11);
        var text = Play2UGui.Text("Message", root, "", Vector2.zero, Vector2.zero, 17, ChatText, Font,
            TextAlignmentOptions.Left);
        text.margin = new Vector4(0, 3, 0, 0);
        text.richText = false;
        var textLayout = text.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
        textLayout.minWidth = 0;
        textLayout.preferredWidth = 0;
        textLayout.minHeight = 25;
        textLayout.flexibleWidth = 1;
        return (root, speaker, text, 0);
    }

    private void Send()
    {
        if (packet?.hasGame != true || !send.interactable || string.IsNullOrWhiteSpace(input.text)) return;
        // IME Return may finish a Korean syllable before it means "send".
        if (!string.IsNullOrEmpty(Input.compositionString)) return;
        bool toDealer = dealer.isOn;
        dealerReceiptUntil = 0;
        pendingId = System.Guid.NewGuid().ToString("N");
        pendingText = input.text;
        RefreshComposer();
        if (!table.Submit(new HoldemWireRequest
        {
            type = "chat", id = pendingId, text = pendingText.Trim(), toDealer = toDealer,
            handId = packet.game.handId, street = packet.game.street, version = packet.game.version,
            windowId = toDealer ? packet.ownUtterances.windowId : null
        }))
        {
            pendingId = pendingText = null;
            RefreshComposer();
        }
    }

    private void ReceiveChat(HoldemWireResponse response)
    {
        if (response.id != pendingId || pendingId == null) return;
        if (response.accepted)
        {
            input.SetTextWithoutNotify("");
            dealer.SetIsOnWithoutNotify(false);
            if (response.hasUtteranceReceipt && response.utteranceError == "None")
                dealerReceiptUntil = Time.unscaledTime + 3f;
        }
        pendingId = pendingText = null;
        deliveryUnconfirmed = false;
        RefreshComposer();
        if (!recordsOpen && EventSystem.current?.currentSelectedGameObject == input.gameObject)
            input.ActivateInputField();
    }

    private void RefreshComposer()
    {
        if (sendLabel == null) return;
        bool pending = pendingId != null;
        bool expired = dealer.isOn && !canRequest;
        input.interactable = !pending;
        dealer.interactable = !pending && (canRequest || dealer.isOn);
        send.interactable = !pending && !expired && !string.IsNullOrWhiteSpace(input.text);
        sendLabel.text = pending ? "전송 중" : "전송";
        sendLabel.color = village ? send.interactable ? Play2HudArt.MarketLightText : Play2HudArt.MarketInk : ChatText;
        if (send.targetGraphic != null)
            send.targetGraphic.color = village
                ? send.interactable ? Play2UGui.Hex("386044") : Play2UGui.Hex("D7CCA9")
                : send.interactable ? Play2UGui.Hex("3F6657") : Play2UGui.Hex("354543");
        requestHint.text = pending ? "서버 응답을 기다리는 중"
            : expired ? "요청 종료. 버튼을 끄면 대화로 전송"
            : deliveryUnconfirmed ? "전송 여부를 대화에서 확인해 주세요"
            : dealer.isOn ? "이번 문장을 딜러에게도 전달"
            : dealerReceiptUntil > 0 && string.IsNullOrEmpty(input.text) ? "딜러에게 전달했어요"
            : !canRequest ? "테이블 대화는 계속할 수 있어요" : "문장은 모두에게 보여요";
        bool missing = roomSession.PracticeDealerMissing;
        bool finished = packet?.game != null && (packet.game.hasResult || packet.game.street >= (int)HoldemStreet.River);
        string unavailable = missing ? "딜러 없음" : finished ? "요청 종료" : "요청 대기";
        dealerLabel.text = dealer.isOn ? "요청 켜짐" : canRequest ? "딜러 요청" : unavailable;
        if (village)
        {
            Play2HudArt.Apply(dealerBackground, dealer.isOn ? "MarketPrimary" : canRequest ? "MarketButton" : "MarketSeat");
            dealerLabel.color = canRequest || dealer.isOn ? Play2HudArt.MarketInk : Play2HudArt.MarketMuted;
        }
        else
        {
            dealerBackground.sprite = null;
            dealerBackground.type = Image.Type.Simple;
            dealerLabel.color = dealer.isOn ? Play2UGui.Ink : canRequest ? ChatText : Play2UGui.Hex("B4BEB3");
            dealerBackground.color = dealer.isOn ? Play2UGui.Gold : canRequest ? Play2UGui.PineInset : Play2UGui.Hex("2C3833");
        }
    }
}
