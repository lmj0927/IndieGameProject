using System.Collections.Generic;
using System.Linq;
using TMPro;
using Poker.Application;
using Poker.Foundation;
using Poker.Transport;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public sealed class Play2ChatCanvas : MonoBehaviour
{
    [SerializeField] private GameObject panel;
    [SerializeField] private UnityEngine.UI.ScrollRect scroll;
    [SerializeField] private TMPro.TMP_Text messages;
    [SerializeField] private TMPro.TMP_InputField input;
    [SerializeField] private UnityEngine.UI.Toggle dealer;
    [SerializeField] private UnityEngine.UI.Button send;

    internal const float DockWidth = 344f;
    private const float RowSpacing = 1f;
    private static readonly Color ChatText = Play2UGui.Hex("F0E6CB");

    private Play2TablePreview table;
    private HoldemRoomPacket packet;
    private RectTransform panelRect, scrollRect, scrollbarRail;
    private UnityEngine.UI.Image inputBackground;
    private readonly List<(RectTransform root, TMP_Text speaker, TMP_Text text)> rows = new();
    private UnityEngine.UI.Outline inputFocus;
    private GameObject closeHint;
    private long firstMessage = -1, lastMessage = -1;
    private bool composing, recordsOpen, focusRequested;
    private int requestedEditFrame = -1;
    private int releasedFocusFrame = -1;
    private InputAction openChat;
    private TMP_Text historyLabel;
    private TMP_Text requestHint, sendLabel, dealerLabel;
    private UnityEngine.UI.Image dealerBackground;
    private Play2RoomSession roomSession;
    private string pendingId, pendingText;
    private bool canRequest;
    private bool deliveryUnconfirmed;
    private float dealerReceiptUntil;
    private float savedChatOffset, layoutCanvasHeight;
    private int visibleRecords = -1;
    private readonly List<(int street, int accuser, int target, bool caught)> verdicts = new();
    internal TMPro.TMP_FontAsset Font => messages.font;
    internal bool IsTyping => panel.activeInHierarchy && (input.isFocused || requestedEditFrame == Time.frameCount);

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
        input.onSubmit.AddListener(_ => Send());
        dealer.onValueChanged.AddListener(_ => RefreshComposer());
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
        panelRect = (RectTransform)panel.transform;
        panelRect.pivot = Vector2.zero;
        panel.GetComponent<UnityEngine.UI.Graphic>().color = Play2UGui.Hex("152420");

        scrollRect = (RectTransform)scroll.transform;
        scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
        messages.enabled = false;
        var chatLayout = scroll.content.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        chatLayout.childAlignment = TextAnchor.UpperLeft;
        chatLayout.childControlWidth = chatLayout.childControlHeight = true;
        chatLayout.childForceExpandWidth = true;
        chatLayout.childForceExpandHeight = false;
        chatLayout.spacing = RowSpacing;

        var label = Play2UGui.Text("Chat Heading", panelRect, "채팅", new Vector2(21, -4), new Vector2(61, 30),
            18, ChatText, Font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = Vector2.up;
        label.rectTransform.anchoredPosition = new Vector2(21, -4);
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
        var sendRect = (RectTransform)send.transform;
        sendRect.anchorMin = sendRect.anchorMax = sendRect.pivot = Vector2.right;
        sendRect.anchoredPosition = new Vector2(-8, 12);
        sendRect.sizeDelta = new Vector2(69, 34);
        send.targetGraphic = Play2HudArt.Skin(send.targetGraphic, "PlayingSend");
        sendLabel = send.GetComponentInChildren<TMP_Text>();
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

    private void LateUpdate()
    {
        if (composing != IsTyping) SetComposing(IsTyping);
        if (dealerReceiptUntil > 0 && Time.unscaledTime >= dealerReceiptUntil)
        {
            dealerReceiptUntil = 0;
            RefreshComposer();
        }
        if (panel.activeInHierarchy && !Mathf.Approximately(((RectTransform)panelRect.parent).rect.height, layoutCanvasHeight))
        {
            ApplyLayout();
            RestoreScroll(scroll.verticalNormalizedPosition > .01f, scroll.content.anchoredPosition.y);
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

    private void SetComposing(bool value)
    {
        composing = value;
        closeHint.SetActive(value);
        inputFocus.enabled = value;
        if (inputBackground != null)
            inputBackground.color = value ? Play2UGui.Hex("24383B") : Play2UGui.Hex("0E191C");
    }

    private void ApplyLayout()
    {
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.up;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = new Vector2(DockWidth, 0);
        layoutCanvasHeight = ((RectTransform)panelRect.parent).rect.height;
        scrollRect.offsetMin = new Vector2(12, 87);
        scrollRect.offsetMax = new Vector2(-23, -38);
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
        if (!visible)
        {
            firstMessage = lastMessage = -1;
            recordsOpen = focusRequested = false;
            pendingId = pendingText = null;
            dealerReceiptUntil = 0;
            visibleRecords = -1;
            foreach (var row in rows) row.root.gameObject.SetActive(false);
            input.SetTextWithoutNotify("");
            dealer.SetIsOnWithoutNotify(false);
            SetComposing(false);
            return;
        }

        canRequest = !packet.paused && packet.hasOwnUtterances && packet.ownUtterances.canSubmit
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
        historyLabel.text = recordsOpen ? "대화로" : $"판 기록 {count}";
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
                SetRow(i, Name(item.Speaker.Value), item.Text + meaning + "\n" + card);
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
            SetRow(i, Name(entry.seat), Play2TableReactions.Describe(entry.text, packet));
        }
        for (int i = entries.Length; i < rows.Count; i++) rows[i].root.gameObject.SetActive(false);
        RestoreScroll(keepScroll, offset);
    }

    private string Name(int seat) => packet.members.FirstOrDefault(m => m.seat == seat)?.name ?? $"{seat}번";

    private void SetRow(int index, string name, string text)
    {
        if (index == rows.Count) rows.Add(CreateMessageRow());
        var row = rows[index];
        row.speaker.text = name;
        row.speaker.color = name == Name(packet.viewerSeat) ? Play2UGui.Gold : Play2UGui.Hex("ADCFCA");
        row.text.text = text;
        row.root.gameObject.SetActive(true);
    }

    private (RectTransform root, TMP_Text speaker, TMP_Text text) CreateMessageRow()
    {
        var root = Play2UGui.Rect("Chat Message", scroll.content, Vector2.zero, Vector2.zero);
        var line = root.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
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
        return (root, speaker, text);
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
        sendLabel.color = ChatText;
        if (send.targetGraphic != null)
            send.targetGraphic.color = send.interactable ? Play2UGui.Hex("3F6657") : Play2UGui.Hex("354543");
        requestHint.text = pending ? "서버 응답을 기다리는 중"
            : expired ? "요청 종료. 버튼을 끄면 대화로 전송"
            : deliveryUnconfirmed ? "전송 여부를 대화에서 확인해 주세요"
            : dealer.isOn ? "이번 문장을 딜러에게도 전달"
            : dealerReceiptUntil > 0 && string.IsNullOrEmpty(input.text) ? "딜러에게 전달했어요"
            : "문장은 모두에게 보여요";
        dealerLabel.text = dealer.isOn ? "요청 켜짐" : "딜러 요청";
        dealerLabel.color = dealer.isOn ? Play2UGui.Ink : ChatText;
        dealerBackground.color = dealer.isOn ? Play2UGui.Gold : Play2UGui.PineInset;
    }
}
