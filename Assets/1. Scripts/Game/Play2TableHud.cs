using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Poker.Application;
using Poker.Foundation;
using Poker.Transport;
using TMPro;
using UnityEngine;

internal sealed partial class Play2TableHud
{
    private readonly Play2TablePreview table;
    private readonly Play2FrontEnd owner;
    private readonly RectTransform root, controls, sizing, accusation, betAdjustments, allInOption, potPanel;
    private readonly RectTransform[] seatPanels = new RectTransform[4];
    private readonly TextMeshProUGUI[] seatStates = new TextMeshProUGUI[4];
    private readonly UnityEngine.UI.Image[] seatTurns = new UnityEngine.UI.Image[4];
    private readonly UnityEngine.UI.Outline[] seatOutlines = new UnityEngine.UI.Outline[4];
    private readonly TextMeshProUGUI street, potAndAnte, turn, notice, target, feedback, betHeading, betHand, accusationHand;
    private readonly UnityEngine.UI.Image turnMark;
    private readonly UnityEngine.UI.Image[] suspectPlates;
    private readonly TextMeshProUGUI[] suspectNames;
    private readonly SettlementCard settlementCard;
    private readonly RectTransform showdownCaption;
    private readonly TextMeshProUGUI showdownTitle, showdownDetail;
    private Play2TablePreview.ShowdownBeat lastBeat;
    private int lastAwardIndex, lastRevealingSeat;
    private readonly Play2InGameSettings settings;
    private readonly TextMeshProUGUI[] seatLabels = new TextMeshProUGUI[4], seatStacks = new TextMeshProUGUI[4];
    private readonly UnityEngine.UI.Image[] actionRows = new UnityEngine.UI.Image[4];
    private readonly CanvasGroup[] actionOpacity = new CanvasGroup[4];
    private readonly TextMeshProUGUI[] actionLabels = new TextMeshProUGUI[4];
    private readonly Play2HandGuide handGuide;
    private bool acting;
    private readonly CanvasGroup baseActions;
    private TextMeshProUGUI betCommit;
    private readonly int[] potPercents = { 33, 50, 100, 200 };
    private readonly UnityEngine.UI.Button[] potButtons = new UnityEngine.UI.Button[4];
    private UnityEngine.UI.Slider amountSlider;
    internal bool CanOpenChat => packet != null && !settings.IsOpen && !handGuide.IsOpen;
    private long WholeStack
    {
        get { var self = packet.game.seats.First(s => s.seat == packet.viewerSeat); return self.stack + self.streetContribution; }
    }
    private long minimumBet, maximumBet, betAmount;
    private HoldemRoomPacket packet;
    private string lastStatus, noticeKey;
    private Coroutine feedbackRoutine;
    private readonly HashSet<string> shownNotices = new HashSet<string>();
    private bool wasDealing, wasSettlementReady, wasActionPending;
    private const float VerdictReadTime = 5.5f;
    private float settlementRevealUntil, noticeUntil;
    private bool announceSettlement;
    private int[] suspects = Array.Empty<int>();
    private int suspectIndex;

    public Play2TableHud(Transform parent, Play2TablePreview table, TMP_FontAsset font, Play2FrontEnd owner)
    {
        this.table = table;
        this.owner = owner;
        root = Play2UGui.Fill("Game HUD", parent);
        var leave = Play2HudArt.Button("Leave", root, "나가기", new Vector2(60, -32), new Vector2(92, 34),
            "PlayingBadge", Play2UGui.Cream, font, table.Leave, 16);
        leave.GetComponent<RectTransform>().anchorMin = leave.GetComponent<RectTransform>().anchorMax = Vector2.up;
        var options = Play2HudArt.Button("Settings", root, "설정", new Vector2(-49, -32), new Vector2(70, 34),
            "PlayingBadge", Play2UGui.Cream, font, OpenSettings, 16);
        options.GetComponent<RectTransform>().anchorMin = options.GetComponent<RectTransform>().anchorMax = Vector2.one;

        potPanel = Play2UGui.Panel("Table Pot", root, Vector2.zero, new Vector2(190, 62), Color.clear);
        potAndAnte = Play2UGui.Text("Pot Amount", potPanel, "", new Vector2(0, 10), new Vector2(180, 34), 25,
            Play2UGui.Gold, font, style: FontStyles.Bold);
        Play2HudArt.Fit(potAndAnte, 19);
        var potShadow = potAndAnte.gameObject.AddComponent<UnityEngine.UI.Shadow>();
        potShadow.effectColor = new Color(0, 0, 0, .7f);
        potShadow.effectDistance = new Vector2(0, -2);
        street = Play2UGui.Text("Table Entry Fee", potPanel, "", new Vector2(0, -17), new Vector2(180, 20), 13,
            Play2UGui.Cream, font);

        for (int i = 0; i < seatLabels.Length; i++)
        {
            var plate = Play2HudArt.Plate("Player " + (i + 1), root, Vector2.zero, new Vector2(152, 56), "PlayingBadge");
            seatPanels[i] = plate;
            var outline = plate.gameObject.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = Play2UGui.Gold;
            outline.effectDistance = new Vector2(2, -2);
            outline.enabled = false;
            seatOutlines[i] = outline;
            var line = Play2UGui.Panel("Turn Marker", plate, new Vector2(-69, 0), new Vector2(3, 38), Play2UGui.Gold);
            seatTurns[i] = line.GetComponent<UnityEngine.UI.Image>();
            var label = Play2UGui.Text("Info", plate, "", new Vector2(0, 13), new Vector2(136, 20), 15,
                Play2UGui.Cream, font, style: FontStyles.Bold);
            label.richText = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            Play2HudArt.Fit(label, 12);
            seatLabels[i] = label;
            var stack = Play2UGui.Text("Chips", plate, "", new Vector2(0, -6), new Vector2(144, 24), 20,
                Play2UGui.Cream, font, style: FontStyles.Bold);
            stack.textWrappingMode = TextWrappingModes.NoWrap;
            Play2HudArt.Fit(stack, 14);
            seatStacks[i] = stack;
            seatStates[i] = Play2UGui.Text("Seat State", plate, "", new Vector2(0, -19), new Vector2(136, 17), 14,
                Play2UGui.Gold, font);
            seatStates[i].richText = false;
        }

        var noticePanel = Play2HudArt.Plate("Action Notice", root, new Vector2(0, -32), new Vector2(600, 50), "CreamPanel");
        noticePanel.anchorMin = noticePanel.anchorMax = new Vector2(.5f, 1);
        notice = Play2UGui.Text("Text", noticePanel, "", Vector2.zero, new Vector2(574, 44), 18, PaperInk, font,
            style: FontStyles.Bold);
        notice.richText = false;
        Play2HudArt.Fit(notice, 12);
        noticePanel.gameObject.SetActive(false);

        controls = Play2UGui.Panel("Action Keys", root, new Vector2(-200, 73), new Vector2(370, 118), Color.clear);
        controls.anchorMin = controls.anchorMax = new Vector2(1, 0);
        baseActions = controls.gameObject.AddComponent<CanvasGroup>();
        turnMark = Play2UGui.Panel("Turn Marker", controls, new Vector2(-170, 36), new Vector2(4, 24), Color.clear)
            .GetComponent<UnityEngine.UI.Image>();
        turn = Play2UGui.Text("Turn", controls, "", new Vector2(-39, 36), new Vector2(232, 25), 18,
            Play2UGui.Cream, font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        turn.textWrappingMode = TextWrappingModes.NoWrap;
        Play2HudArt.Fit(turn, 12);
        string[] keys = { "F", "E", "R", "X" };
        for (int i = 0; i < keys.Length; i++)
        {
            var position = i == 3 ? new Vector2(127, 36) : new Vector2(-119 + 119 * i, -15);
            var size = i == 3 ? new Vector2(82, 29) : new Vector2(111, 51);
            var row = Play2HudArt.Plate(keys[i] + " Action", controls, position, size, i == 2 ? "GoldButton" : "PlayingAction");
            int action = i;
            var actionButton = Play2HudArt.Clickable(row, () => PerformAction(action));
            var colors = actionButton.colors;
            colors.disabledColor = Color.white;
            actionButton.colors = colors;
            actionRows[i] = row.GetComponent<UnityEngine.UI.Image>();
            actionOpacity[i] = row.gameObject.AddComponent<CanvasGroup>();
            actionLabels[i] = Play2UGui.Text("Action", row, "", Vector2.zero, size - new Vector2(10, 4), i == 3 ? 15 : 19,
                i == 2 ? PaperInk : Play2UGui.Cream, font, style: FontStyles.Bold);
            actionLabels[i].textWrappingMode = TextWrappingModes.NoWrap;
            Play2HudArt.Fit(actionLabels[i], 14);
        }
        var feedbackPanel = Play2HudArt.Plate("Status Toast", root, new Vector2(0, -32), new Vector2(480, 42), "PlayingBadge");
        feedbackPanel.anchorMin = feedbackPanel.anchorMax = new Vector2(.5f, 1);
        feedback = Play2UGui.Text("Feedback", feedbackPanel, "", Vector2.zero, new Vector2(456, 34), 16,
            Play2UGui.Cream, font);
        feedback.richText = false;
        Play2HudArt.Fit(feedback, 13);
        feedbackPanel.gameObject.SetActive(false);
        handGuide = new Play2HandGuide(root, font);
        (sizing, betHand, target, betHeading, betAdjustments, allInOption) = BuildBetPicker(root, font);
        (accusation, accusationHand, suspectPlates, suspectNames) = BuildAccusationPicker(root, font);
        showdownCaption = Play2HudArt.Plate("Showdown Caption", root, new Vector2(-190, 65), new Vector2(344, 82), "PlayingBadge");
        showdownCaption.anchorMin = showdownCaption.anchorMax = Vector2.right;
        showdownTitle = Play2UGui.Text("Showdown Title", showdownCaption, "", new Vector2(0, 13), new Vector2(317, 44),
            37, Play2UGui.Gold, font, style: FontStyles.Bold);
        showdownDetail = Play2UGui.Text("Showdown Detail", showdownCaption, "", new Vector2(0, -19), new Vector2(317, 25),
            18, Play2UGui.Cream, font);
        showdownTitle.richText = showdownDetail.richText = false;
        Play2HudArt.Fit(showdownTitle, 16);
        Play2HudArt.Fit(showdownDetail, 13);
        showdownCaption.gameObject.SetActive(false);
        settlementCard = new SettlementCard(root, font, AdvanceHand, table.ToggleRematchReady);
        settings = new Play2InGameSettings(root, font);
        root.gameObject.SetActive(false);
    }

    public void Present(HoldemRoomPacket value, string status)
    {
        if (value?.hasGame != true)
        {
            packet = null;
            acting = false;
            noticeKey = null;
            notice.transform.parent.gameObject.SetActive(false);
            shownNotices.Clear();
            if (settings.IsOpen) settings.Close();
            settlementCard.Hide();
            CloseActionPanels();
            root.gameObject.SetActive(false);
            return;
        }
        UpdatePositions(value);
        bool dealing = table.IsDealing;
        var beat = table.Showdown;
        bool showingHands = beat == Play2TablePreview.ShowdownBeat.Reveal;
        bool showingAwards = beat == Play2TablePreview.ShowdownBeat.Award;
        bool beforeAward = showingHands || beat == Play2TablePreview.ShowdownBeat.Waiting;
        bool actionPending = table.ActionPending;
        var verdict = value.hasTurnAccusation ? value.turnAccusation : null;
        string verdictKey = verdict != null && verdict.handId == value.game.handId
            ? $"a:{verdict.handId}:{verdict.street}:{verdict.accuser}:{verdict.target}" : null;
        if (value.game.hasResult && (packet?.game.hasResult != true || packet.game.handId != value.game.handId))
        {
            announceSettlement = packet?.hasGame == true && packet.game.handId == value.game.handId;
            settlementRevealUntil = Time.unscaledTime;
            if (announceSettlement)
            {
                settlementRevealUntil += 1.1f;
                if (verdictKey != null)
                {
                    if (!shownNotices.Contains(verdictKey)) settlementRevealUntil = Time.unscaledTime + VerdictReadTime;
                    else if (noticeKey == verdictKey) settlementRevealUntil = Mathf.Max(settlementRevealUntil, noticeUntil);
                }
            }
        }
        bool settlementReady = !value.game.hasResult
            || Time.unscaledTime >= settlementRevealUntil && !dealing && !table.IsBetting && !beforeAward && !showingAwards;
        bool resultBecameVisible = value.game.hasResult && settlementReady
            && (!wasSettlementReady || packet?.game.hasResult != true);
        if (ReferenceEquals(packet, value) && lastStatus == status && wasDealing == dealing
            && wasSettlementReady == settlementReady && wasActionPending == actionPending && lastBeat == beat
            && lastAwardIndex == table.AwardIndex && lastRevealingSeat == table.RevealingSeat) return;
        bool awaitingWinner = beforeAward || (value.game.hasResult && !settlementReady && beat == Play2TablePreview.ShowdownBeat.None);
        var previousPacket = packet;
        var previous = previousPacket?.game;
        if (lastStatus != status)
        {
            if (feedbackRoutine != null) owner.StopCoroutine(feedbackRoutine);
            if (!string.IsNullOrEmpty(status)) notice.transform.parent.gameObject.SetActive(false);
            feedback.text = status ?? "";
            feedback.transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(status));
            feedbackRoutine = owner.StartCoroutine(HideFeedback());
        }
        bool firstAward = showingAwards && lastBeat != Play2TablePreview.ShowdownBeat.Award;
        packet = value; lastStatus = status;
        lastBeat = beat;
        lastAwardIndex = table.AwardIndex;
        lastRevealingSeat = table.RevealingSeat;
        wasDealing = dealing;
        wasSettlementReady = settlementReady;
        root.gameObject.SetActive(true);
        var game = packet.game;
        showdownCaption.gameObject.SetActive(showingHands || showingAwards);
        if (showingHands)
        {
            showdownTitle.text = "쇼다운";
            showdownDetail.text = table.RevealingSeat > 0 ? $"{Name(table.RevealingSeat)} 패 공개" : "패 공개";
        }
        else if (showingAwards)
        {
            var paid = game.seats.FirstOrDefault(seat => seat.seat == table.HighlightSeat);
            showdownTitle.text = paid == null ? "쇼다운" : Play2HandGuide.CategoryLabel(Play2SettlementSummary.Value(paid));
            var award = table.CurrentAward;
            showdownDetail.text = award == null ? "" : award.Caption(game, DisplayName);
        }
        if (previous?.handId != game.handId)
        {
            shownNotices.Clear();
            noticeKey = null;
            notice.transform.parent.gameObject.SetActive(false);
        }
        street.gameObject.SetActive(game.board.Length == 0 && !game.hasResult);
        street.text = $"입장료 {game.postedAnte}칩";
        potAndAnte.text = $"팟  {game.pot}칩";
        for (int i = 0; i < seatLabels.Length; i++)
        {
            var seat = game.seats.FirstOrDefault(s => s.seat == i + 1);
            seatPanels[i].gameObject.SetActive(seat != null);
            if (seat == null) continue;
            long displayedAward = showingAwards && table.CurrentAward != null
                ? table.CurrentAward.PayoutThroughThisPot(game, seat.seat) : awaitingWinner ? 0L : seat.awarded;
            long shownStack = seat.stack - seat.awarded + displayedAward;
            seatLabels[i].text = seat.viewer ? "" : Name(seat.seat);
            seatStacks[i].text = seat.viewer ? $"내 칩  {shownStack}" : $"{shownStack}칩";
            seatStates[i].text = seat.status == (int)HoldemSeatStatus.Folded ? "폴드"
                : seat.status == (int)HoldemSeatStatus.AllIn ? "올인"
                : beforeAward && seat.status == (int)HoldemSeatStatus.Busted && Play2SettlementSummary.IsPublicHand(seat) ? "올인"
                : seat.status == (int)HoldemSeatStatus.Busted ? "탈락"
                : seat.streetContribution > 0 ? $"베팅 {seat.streetContribution}칩" : "";
            if (game.hasResult && table.IsHandShown(seat.seat) && !seat.viewer && seat.revealedBestCards?.Length == 5)
            {
                var valueOfHand = HandEvaluator.Evaluate(seat.revealedBestCards.Select(Poker.Foundation.Card.FromId).ToArray());
                seatStates[i].text = Play2HandGuide.CategoryLabel(valueOfHand);
            }
            bool won = game.hasResult && displayedAward > 0;
            if (won) seatStates[i].text = (string.IsNullOrEmpty(seatStates[i].text) ? "승리" : seatStates[i].text)
                + "  +" + displayedAward + "칩";
            if (seat.viewer && !won && (seat.status == (int)HoldemSeatStatus.Folded || seat.status == (int)HoldemSeatStatus.Busted))
                seatStates[i].text = "";
            bool hasTurn = seat.currentActor && !game.hasResult && !packet.paused
                && !game.dealPending && !game.revealPending && !dealing;
            seatTurns[i].enabled = (hasTurn || won) && !seat.viewer;
            seatOutlines[i].enabled = false;
            bool withStatus = hasTurn || won || !string.IsNullOrEmpty(seatStates[i].text);
            seatLabels[i].rectTransform.anchoredPosition = new Vector2(0, withStatus ? 16 : 12);
            seatStacks[i].rectTransform.anchoredPosition = new Vector2(0, seat.viewer ? 5 : withStatus ? -1 : -8);
            seatPanels[i].GetComponent<UnityEngine.UI.Image>().color = seat.viewer
                ? Color.clear : Play2UGui.Hex(won ? "473F2BEA" : hasTurn ? "14292FEF" : "10191C9E");
            seatLabels[i].color = hasTurn || won ? Play2UGui.Gold : Play2UGui.Cream;
            if (hasTurn && !seat.viewer) seatStates[i].text = "차례" + (seat.streetContribution > 0 ? $"  |  베팅 {seat.streetContribution}칩" : "");
        }
        bool wasActing = acting;
        acting = game.hasLegal && game.currentSeat == packet.viewerSeat && !packet.paused
            && !game.hasResult && !game.dealPending && !game.revealPending && !dealing && !actionPending;
        if (acting && !wasActing && !wasActionPending) table.PlayTurnSound();
        wasActionPending = actionPending;
        if ((firstAward || resultBecameVisible) && announceSettlement)
        {
            table.PlayResultSound();
            announceSettlement = false;
        }
        bool settlement = game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority;
        bool bettingRound = !game.hasResult && !settlement;
        controls.gameObject.SetActive(bettingRound);
        bool sameWindow = previous?.handId == game.handId && previous.street == game.street
            && previousPacket.viewerSeat == packet.viewerSeat && previousPacket.sessionId == packet.sessionId;
        if (!acting || !sameWindow || previous.currentSeat != game.currentSeat)
            CloseActionPanels();
        DrawActions();
        if (acting && game.canAccuseOnTurn && (previous?.version != game.version || suspects.Length == 0))
        {
            suspects = game.seats.Where(s => s.dealtIn && s.seat != packet.viewerSeat && s.status != (int)HoldemSeatStatus.Folded).Select(s => s.seat).ToArray();
            suspectIndex = 0;
            UpdateSuspect();
        }
        handGuide.Present(packet, dealing);
        accusationHand.text = handGuide.CurrentRank;
        if (settlementReady) settlementCard.Present(packet, settlement, table.IsHost, actionPending, DisplayName);
        else settlementCard.Hide();
        turn.text = packet.paused ? "연결 대기 중" : settlement ? "정산 대기" : game.hasResult ? (game.isOver ? "경기 종료" : "이번 판 종료")
            : game.dealPending ? "딜러가 요청을 읽고 있어요" : game.revealPending || dealing ? "카드 공개 중"
            : actionPending ? "처리 중"
            : acting ? "내 차례" : $"{Name(game.currentSeat)} 차례";
        turn.color = acting ? Play2UGui.Gold : Play2UGui.Cream;
        turnMark.color = acting ? Play2UGui.Hex("E9CC8C") : Color.clear;
        // A restored hand already has its public verdict in the recap.
        if (verdictKey != null && previous?.handId != game.handId) shownNotices.Add(verdictKey);
        if (verdictKey != null)
        {
            var result = verdict;
            string outcome = $"{Name(result.accuser)} → {Name(result.target)}   "
                + (result.caught ? $"고발 적중\n{Name(result.target)} 폴드" : $"고발 실패\n{Name(result.accuser)} 폴드");
            if (!result.caught && packet.hasHistory)
            {
                var fine = packet.history?.entries?.LastOrDefault(entry =>
                    entry.kind == (int)HoldemHistoryKind.AccusationFine && entry.seat == result.accuser
                    && entry.street == result.street);
                if (fine != null && fine.amount > 0) outcome += $", 벌금 {fine.amount}칩";
            }
            ShowNotice(verdictKey, outcome);
        }
        if (!game.hasResult && packet.dealerSelections != null)
        {
            var selection = Enumerable.Range(0, game.board.Length)
                .Select(index => HoldemDealerSelectionPacketReader.FindOwnAppliedCard(packet, index))
                .LastOrDefault(item => item != null && item.street == game.street);
            if (selection != null)
            {
                if (!dealing)
                {
                    string card = Play2HandGuide.CardText(Poker.Foundation.Card.FromId(selection.card));
                    string message = $"요청 채택 · {card} · 왼쪽 {selection.boardIndex + 1}번째";
                    if (!string.IsNullOrWhiteSpace(selection.interpretation))
                        message += $"\nAI 해석: {Brief(selection.interpretation, 120)}";
                    ShowNotice($"dealer:{game.handId}:{selection.dealWindowId}", message);
                }
            }

        }



    }

    private void OpenSettings()
    {
        ClearSelection();
        settings.Open();
    }

    private void UpdatePositions(HoldemRoomPacket value)
    {
        PositionAtViewport(potPanel, table.PotLabelViewport, 105, 160);
        var noticePanel = (RectTransform)notice.transform.parent;
        float noticeBottom = -noticePanel.anchoredPosition.y + noticePanel.rect.height / 2;
        for (int i = 0; i < seatPanels.Length; i++)
        {
            if (i + 1 == value.viewerSeat)
                seatPanels[i].anchoredPosition = new Vector2(90, -root.rect.height / 2 + 35);
            else
            {
                var point = table.PlayerLabelViewport(i + 1);
                point.y += (seatPanels[i].rect.height / 2 + 8) / root.rect.height;
                float width = root.rect.width;
                bool nearMenu = point.x * width < 196 || (1 - point.x) * width < 244;
                float topMargin = nearMenu ? 93 : 46;
                // Reserve the notice lane even while it is empty so a verdict never moves the nameplate.
                float plateX = Mathf.Clamp((point.x - .5f) * width, root.rect.xMin + 101, root.rect.xMax - 101);
                if (Mathf.Abs(plateX) < (noticePanel.rect.width + seatPanels[i].rect.width) / 2 + 6)
                    topMargin = Mathf.Max(topMargin, noticeBottom + seatPanels[i].rect.height / 2 + 6);
                PositionAtViewport(seatPanels[i], point, 101, 350, topMargin);
            }
        }
    }

    private void PositionAtViewport(RectTransform rect, Vector3 point, float sideMargin, float bottomMargin, float topMargin = 95)
    {
        var area = root.rect;
        var local = new Vector2((point.x - .5f) * area.width, (point.y - .5f) * area.height);
        rect.anchoredPosition = new Vector2(Mathf.Clamp(local.x, area.xMin + sideMargin, area.xMax - sideMargin),
            Mathf.Clamp(local.y, area.yMin + bottomMargin, area.yMax - topMargin));
    }

    public void HandleKeys(bool chatFocused)
    {
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (packet == null || keyboard == null || !Application.isFocused) return;
        if (settings.IsOpen)
        {
            if (keyboard.escapeKey.wasPressedThisFrame) settings.Close();
            return;
        }
        if (chatFocused) return;
        if (handGuide.IsOpen)
        {
            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.hKey.wasPressedThisFrame) handGuide.Close();
            return;
        }

        bool selecting = sizing.gameObject.activeSelf || accusation.gameObject.activeSelf;
        if (!selecting && keyboard.escapeKey.wasPressedThisFrame)
        {
            OpenSettings();
            return;
        }
        if (keyboard.hKey.wasPressedThisFrame && !selecting) handGuide.Toggle();
        if (!acting) return;

        if (sizing.gameObject.activeSelf)
        {
            HandleSizing(keyboard);
            return;
        }
        if (accusation.gameObject.activeSelf)
        {
            HandleAccusation(keyboard);
            return;
        }

        if (keyboard.fKey.wasPressedThisFrame) PerformAction(0);
        else if (keyboard.eKey.wasPressedThisFrame) PerformAction(1);
        else if (keyboard.rKey.wasPressedThisFrame) PerformAction(2);
        else if (keyboard.xKey.wasPressedThisFrame) PerformAction(3);
    }

    private void PerformAction(int index)
    {
        if (!acting || table.ActionPending || settings.IsOpen || handGuide.IsOpen
            || !actionOpacity[index].interactable || !baseActions.interactable) return;
        if (index == 0) Act(BettingActionKind.Fold);
        else if (index == 1) Act(packet.game.legal.check ? BettingActionKind.Check : BettingActionKind.Call);
        else if (index == 2)
        {
            CloseActionPanels();
            OpenSizing();
        }
        else if (suspects.Length > 0)
        {
            CloseActionPanels();
            ClearSelection();
            accusation.gameObject.SetActive(true);
            SetPickerOpen(true);
        }
    }

    private void DrawActions()
    {
        foreach (var row in actionRows) row.gameObject.SetActive(acting);
        if (!acting) return;

        var legal = packet.game.legal;
        string call = legal.callAmount > 0 ? $"콜 {legal.callAmount}칩" : "체크";
        SetAction(0, "폴드", legal.fold);
        SetAction(1, call, legal.check || legal.call);
        bool opening = packet.game.seats.All(s => s.streetContribution == 0);
        SetAction(2, !legal.bet && !legal.raise && legal.hasAllInTarget ? "올인" : opening ? "베팅" : "레이즈",
            legal.bet || legal.raise || legal.hasAllInTarget);
        SetAction(3, "고발", packet.game.canAccuseOnTurn);
    }

    private void SetAction(int index, string label, bool enabled)
    {
        actionRows[index].GetComponent<Play2ActionButton>().Intent = $"{packet.game.handId}:{packet.game.street}:{packet.game.version}:{label}";
        actionOpacity[index].interactable = enabled;
        Play2HudArt.Apply(actionRows[index], enabled && index == 2 ? "GoldButton" : "PlayingAction");
        if (!enabled) actionRows[index].color = Play2UGui.Hex("1A282BFF");
        actionLabels[index].color = !enabled ? Play2UGui.Hex("8FA19FFF")
            : index == 2 ? PaperInk : Play2UGui.Cream;
        actionLabels[index].text = label;
    }

    private void OpenSizing()
    {
        ClearSelection();
        var legal = packet.game.legal;
        bool adjustable = legal.minimumTarget > 0 && legal.maximumTarget >= legal.minimumTarget;
        minimumBet = adjustable ? legal.minimumTarget : legal.allInTarget;
        maximumBet = adjustable ? legal.maximumTarget : minimumBet;
        betAmount = minimumBet;
        amountSlider.maxValue = maximumBet + (legal.hasAllInTarget && legal.allInTarget > maximumBet ? 1 : 0);
        amountSlider.minValue = minimumBet;
        betAdjustments.gameObject.SetActive(adjustable);
        allInOption.gameObject.SetActive(legal.hasAllInTarget || maximumBet == WholeStack);
        decimal previousTarget = -1;
        for (int i = 0; i < potButtons.Length; i++)
        {
            decimal targetChips = PotTarget(potPercents[i]);
            potButtons[i].interactable = adjustable && targetChips >= minimumBet && targetChips <= maximumBet && targetChips != previousTarget;
            previousTarget = targetChips;
        }
        UpdateBetAmount();
        sizing.gameObject.SetActive(true);
        SetPickerOpen(true);
    }

    private void HandleSizing(UnityEngine.InputSystem.Keyboard keyboard)
    {
        if (keyboard.escapeKey.wasPressedThisFrame || keyboard.rKey.wasPressedThisFrame)
        {
            CloseActionPanels();
            return;
        }

        if (keyboard.aKey.wasPressedThisFrame && allInOption.gameObject.activeInHierarchy) SelectBet(WholeStack);
        else if (keyboard.homeKey.wasPressedThisFrame) SelectBet(minimumBet);
        else if (keyboard.pKey.wasPressedThisFrame) SelectPot(50);
        else if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame)
        {
            long step = keyboard.shiftKey.isPressed ? 10 : 1;
            StepBet(keyboard.leftArrowKey.wasPressedThisFrame ? -step : step);
        }
        else if (keyboard.spaceKey.wasPressedThisFrame) RaiseTo(betAmount);
    }

    private void StepBet(long delta)
    {
        if (betAmount > maximumBet && delta < 0) SelectBet(maximumBet);
        else if (delta > 0 && betAmount >= maximumBet && packet.game.legal.hasAllInTarget) SelectBet(packet.game.legal.allInTarget);
        else SelectBet(Math.Clamp(betAmount + delta, minimumBet, maximumBet));
    }

    private decimal PotTarget(int percent)
    {
        var game = packet.game;
        long paid = game.seats.First(s => s.seat == packet.viewerSeat).streetContribution;
        long call = game.legal.callAmount;
        decimal raise = decimal.Round(((decimal)game.pot + call) * percent / 100, 0, MidpointRounding.AwayFromZero);
        return (decimal)paid + call + raise;
    }

    private void SelectPot(int percent)
    {
        decimal targetChips = PotTarget(percent);
        if (targetChips >= minimumBet && targetChips <= maximumBet) SelectBet((long)targetChips);
    }

    private void SelectBet(long value)
    {
        betAmount = value;
        UpdateBetAmount();
    }

    private void HandleAccusation(UnityEngine.InputSystem.Keyboard keyboard)
    {
        if (keyboard.escapeKey.wasPressedThisFrame || keyboard.xKey.wasPressedThisFrame)
        {
            CloseActionPanels();
            return;
        }
        if (!packet.game.canAccuseOnTurn || suspects.Length == 0)
        {
            CloseActionPanels();
            return;
        }

        if (keyboard.spaceKey.wasPressedThisFrame) ConfirmAccusation();
        else if (keyboard.leftArrowKey.wasPressedThisFrame) StepSuspect(-1);
        else if (keyboard.rightArrowKey.wasPressedThisFrame) StepSuspect(1);
    }

    private void StepSuspect(int step)
    {
        if (suspects.Length == 0) return;
        suspectIndex = (suspectIndex + suspects.Length + step) % suspects.Length;
        UpdateSuspect();
    }

    private void ConfirmAccusation()
    {
        if (!acting || !packet.game.canAccuseOnTurn || suspectIndex >= suspects.Length) return;
        var command = Command("turn-accusation");
        command.accusationTarget = suspects[suspectIndex];
        if (table.Submit(command)) CloseActionPanels();
    }

    private void UpdateBetAmount()
    {
        long paid = packet.game.seats.First(s => s.seat == packet.viewerSeat).streetContribution;
        string action = betAmount == WholeStack ? "올인" : packet.game.seats.All(s => s.streetContribution == 0) ? "베팅" : "레이즈";
        betHeading.text = "지금 낼 금액";
        betCommit.text = $"{betAmount - paid}칩 {action}";
        target.text = $"{betAmount - paid}칩";
        betHand.text = packet.game.legal.callAmount > 0 ? $"콜 {packet.game.legal.callAmount}칩 포함" : "";
        amountSlider.SetValueWithoutNotify(betAmount > maximumBet ? maximumBet + 1 : betAmount);
    }

    private void UpdateSuspect()
    {
        for (int i = 0; i < suspectPlates.Length; i++)
        {
            var plate = suspectPlates[i];
            plate.gameObject.SetActive(i < suspects.Length);
            if (i >= suspects.Length) continue;
            bool selected = i == suspectIndex;
            plate.rectTransform.anchoredPosition = new Vector2((i - (suspects.Length - 1) * .5f) * 106, 22);
            Play2HudArt.Apply(plate, selected ? "SelectedTarget" : "HeaderBadge");
            suspectNames[i].color = selected ? PaperRed : Play2UGui.Cream;
            suspectNames[i].text = Name(suspects[i]);
        }
    }

    private void AdvanceHand()
    {
        if (packet?.game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority)
        {
            var command = Command("settle");
            command.oddChipRule = (int)HoldemOddChipRule.ClockwiseFromButton;
            table.Submit(command);
        }
        else if (packet?.game?.isOver == true) table.RestartMatch();
        else table.StartHand();
    }

    private void CloseActionPanels()
    {
        sizing.gameObject.SetActive(false);
        accusation.gameObject.SetActive(false);
        SetPickerOpen(false);
    }

    private void SetPickerOpen(bool open)
    {
        baseActions.alpha = open ? 0 : 1;
        baseActions.interactable = baseActions.blocksRaycasts = !open;
        handGuide.SetActionPickerOpen(open);
    }

    private static void ClearSelection() => UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);

    private static string Brief(string value, int limit) => string.IsNullOrWhiteSpace(value) ? "해석 정보 없음"
        : value.Length <= limit ? value : value.Substring(0, limit) + "…";
    private IEnumerator HideFeedback()
    {
        yield return new WaitForSecondsRealtime(4);
        feedback.text = "";
        feedback.transform.parent.gameObject.SetActive(false);
        feedbackRoutine = null;
    }
    private void ShowNotice(string key, string message)
    {
        if (!shownNotices.Add(key)) return;
        if (feedbackRoutine != null) owner.StopCoroutine(feedbackRoutine);
        feedbackRoutine = null;
        feedback.text = "";
        feedback.transform.parent.gameObject.SetActive(false);
        noticeKey = key; notice.text = message; notice.transform.parent.gameObject.SetActive(true);
        noticeUntil = Time.unscaledTime + (key.StartsWith("dealer:") ? 7f : VerdictReadTime);
        owner.StartCoroutine(HideNotice(key, noticeUntil));
    }
    private IEnumerator HideNotice(string key, float until)
    {
        var panel = notice.transform.parent;
        float elapsed = 0f;
        while (elapsed < .25f && noticeKey == key)
        {
            elapsed += Time.unscaledDeltaTime;
            panel.localScale = Vector3.one * Mathf.Lerp(.85f, 1f, Mathf.Clamp01(elapsed / .25f));
            yield return null;
        }
        while (Time.unscaledTime < until && noticeKey == key) yield return null;
        if (noticeKey == key) notice.transform.parent.gameObject.SetActive(false);
    }
    private void RaiseTo(long amount) => Act(packet.game.legal.bet || (!packet.game.legal.raise
        && packet.game.seats.All(s => s.streetContribution == 0)) ? BettingActionKind.BetTo : BettingActionKind.RaiseTo, amount);
    private void Act(BettingActionKind kind, long amount = 0)
    {
        if (!acting || table.ActionPending) return;
        var request = Command("act"); request.action = (int)kind; request.target = amount;
        if (table.Submit(request)) CloseActionPanels();
    }
    private HoldemWireRequest Command(string type) => new HoldemWireRequest
    { type = type, handId = packet.game.handId, version = packet.game.version, street = packet.game.street };
    private string DisplayName(int seat) => seat == packet.viewerSeat ? "나" : Name(seat);
    private string Name(int seat) => packet.members.FirstOrDefault(member => member.seat == seat)?.name ?? $"{seat}번";
}
