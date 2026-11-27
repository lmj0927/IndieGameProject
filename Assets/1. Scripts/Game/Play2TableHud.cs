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
    private readonly RectTransform root, controls, sizing, accusation, betAdjustments, allInOption;
    private readonly RectTransform[] topControls;
    private readonly RectTransform[] seatPanels = new RectTransform[4];
    private readonly TextMeshProUGUI[] seatStates = new TextMeshProUGUI[4];
    private readonly UnityEngine.UI.Image[] seatTurns = new UnityEngine.UI.Image[4];
    private readonly UnityEngine.UI.Outline[] seatOutlines = new UnityEngine.UI.Outline[4];
    private readonly TextMeshProUGUI street, potAndAnte, turn, notice, target, feedback, betHeading, betHand, accusationHand;
    private readonly UnityEngine.UI.Image[] suspectPlates;
    private readonly TextMeshProUGUI[] suspectNames;
    private readonly SettlementCard settlementCard;
    private readonly RectTransform showdownCaption, photoCaption, photoNote;
    private readonly TextMeshProUGUI champion, placements, photoQuote;
    private float photoWidth;
    private readonly UnityEngine.UI.Button photoPose;
    private Play2MatchFinale.Beat lastFinale;
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
    private readonly RectTransform ownTurn;
    private TextMeshProUGUI betCommit;
    private readonly int[] potPercents = { 33, 50, 100, 200 };
    private readonly UnityEngine.UI.Button[] potButtons = new UnityEngine.UI.Button[4];
    private UnityEngine.UI.Slider amountSlider;
    internal bool CanOpenChat => packet != null && !settings.IsOpen && !handGuide.IsOpen;
    internal bool CanOpenEmotes => !actionMenuOpen && !sizing.gameObject.activeSelf && !accusation.gameObject.activeSelf;
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
    private bool announceSettlement, wasWaitingToReveal;
    private int[] suspects = Array.Empty<int>();
    private int suspectIndex;

    public Play2TableHud(Transform parent, Play2TablePreview table, TMP_FontAsset font, Play2FrontEnd owner)
    {
        this.table = table;
        this.owner = owner;
        root = Play2UGui.Fill("Game HUD", parent);
        leaveButton = Play2HudArt.Button("Leave", root, "나가기", new Vector2(60, -32), new Vector2(92, 34),
            "PlayingBadge", Play2UGui.Cream, font, table.Leave, 16);
        leaveButton.GetComponent<RectTransform>().anchorMin = leaveButton.GetComponent<RectTransform>().anchorMax = Vector2.up;
        settingsButton = Play2HudArt.Button("Settings", root, "설정", new Vector2(-49, -32), new Vector2(70, 34),
            "PlayingBadge", Play2UGui.Cream, font, OpenSettings, 16);
        settingsButton.GetComponent<RectTransform>().anchorMin = settingsButton.GetComponent<RectTransform>().anchorMax = Vector2.one;

        potPanel = Play2HudArt.Plate("Table Pot", root, new Vector2(222, -35), new Vector2(216, 52), "PlayingBadge");
        potPanel.GetComponent<UnityEngine.UI.Image>().color = Play2UGui.Hex("142023E8");
        potPanel.anchorMin = potPanel.anchorMax = Vector2.up;
        potAndAnte = Play2UGui.Text("Pot Amount", potPanel, "", new Vector2(0, 6), new Vector2(194, 30), 25,
            Play2UGui.Gold, font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        Play2HudArt.Fit(potAndAnte, 19);
        potShadow = potAndAnte.gameObject.AddComponent<UnityEngine.UI.Shadow>();
        potShadow.effectColor = new Color(0, 0, 0, .7f);
        potShadow.effectDistance = new Vector2(0, -2);
        street = Play2UGui.Text("Table Entry Fee", potPanel, "", new Vector2(0, -13), new Vector2(194, 18), 14,
            Play2UGui.Cream, font, TextAlignmentOptions.MidlineLeft);

        street.textWrappingMode = TextWrappingModes.NoWrap;
        Play2HudArt.Fit(street, 12);

        for (int i = 0; i < seatLabels.Length; i++)
        {
            var plate = Play2HudArt.Plate("Player " + (i + 1), root, Vector2.zero, new Vector2(152, 56), "PlayingBadge");
            seatPanels[i] = plate;
            seatVisibility[i] = plate.gameObject.AddComponent<CanvasGroup>();
            var outline = plate.gameObject.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = Play2UGui.Gold;
            outline.effectDistance = new Vector2(1, -1);
            outline.enabled = false;
            seatOutlines[i] = outline;
            var line = Play2UGui.Panel("Turn Marker", plate, new Vector2(-69, 0), new Vector2(3, 38), Play2UGui.Gold);
            seatTurns[i] = line.GetComponent<UnityEngine.UI.Image>();
            var label = Play2UGui.Text("Info", plate, "", new Vector2(0, 13), new Vector2(136, 20), 17,
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
            seatStates[i].textWrappingMode = TextWrappingModes.NoWrap;
            Play2HudArt.Fit(seatStates[i], 11);
        }

        var noticePanel = Play2HudArt.Plate("Action Notice", root, new Vector2(0, -85), new Vector2(600, 50), "CreamPanel");
        noticePanel.anchorMin = noticePanel.anchorMax = new Vector2(.5f, 1);
        notice = Play2UGui.Text("Text", noticePanel, "", Vector2.zero, new Vector2(574, 44), 18, PaperInk, font,
            style: FontStyles.Bold);
        notice.richText = false;
        Play2HudArt.Fit(notice, 12);
        noticePanel.gameObject.SetActive(false);

        controls = Play2UGui.Panel("Turn Controls", root, new Vector2(-175, 72), new Vector2(318, 107), Color.clear);
        controls.anchorMin = controls.anchorMax = new Vector2(1, 0);
        baseActions = controls.gameObject.AddComponent<CanvasGroup>();
        ownTurn = Play2UGui.Rect("Your Turn", controls, new Vector2(-91, 26), new Vector2(102, 34));
        Play2UGui.Text("Prompt", ownTurn, "내 차례", Vector2.zero, new Vector2(102, 30), 20,
            Play2UGui.Gold, font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        turn = Play2UGui.Text("Turn", root, "", Vector2.zero, new Vector2(246, 26), 18,
            Play2UGui.Cream, font, TextAlignmentOptions.Center, FontStyles.Bold);
        var turnShadow = turn.gameObject.AddComponent<UnityEngine.UI.Shadow>();
        turnShadow.effectColor = new Color(0, 0, 0, .9f);
        turnShadow.effectDistance = new Vector2(1, -2);
        turn.textWrappingMode = TextWrappingModes.NoWrap;
        Play2HudArt.Fit(turn, 12);
        string[] keys = { "F", "E", "R", "X" };
        for (int i = 0; i < keys.Length; i++)
        {
            var row = Play2HudArt.Plate(keys[i] + " Action", controls, Vector2.zero, new Vector2(178, 46),
                i == 1 ? "GoldButton" : "PlayingAction");
            int action = i;
            var actionButton = Play2HudArt.Clickable(row, () => PerformAction(action));
            var colors = actionButton.colors;
            colors.disabledColor = Color.white;
            actionButton.colors = colors;
            actionRows[i] = row.GetComponent<UnityEngine.UI.Image>();
            actionOpacity[i] = row.gameObject.AddComponent<CanvasGroup>();
            actionLabels[i] = Play2UGui.Text("Action", row, "", Vector2.zero, new Vector2(166, 38), i == 1 ? 22 : 18,
                i == 1 ? PaperInk : Play2UGui.Cream, font, style: FontStyles.Bold);
            actionLabels[i].textWrappingMode = TextWrappingModes.NoWrap;
            Play2HudArt.Fit(actionLabels[i], 14);
        }
        var feedbackPanel = Play2HudArt.Plate("Status Toast", root, new Vector2(0, -32), new Vector2(348, 44), "PlayingBadge");
        feedbackPanel.anchorMin = feedbackPanel.anchorMax = new Vector2(.5f, 1);
        feedback = Play2UGui.Text("Feedback", feedbackPanel, "", Vector2.zero, new Vector2(332, 40), 16,
            Play2UGui.Cream, font);
        feedback.richText = false;
        Play2HudArt.Fit(feedback, 13);
        feedbackPanel.gameObject.SetActive(false);
        handGuide = new Play2HandGuide(root, (RectTransform)parent, font);
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
        photoCaption = Play2HudArt.Plate("Match Photo", root, new Vector2(0, -84), new Vector2(0, 63), "PlayingBadge");
        photoCaption.anchorMin = new Vector2(.16f, 1f);
        photoCaption.anchorMax = new Vector2(.84f, 1f);
        champion = Play2UGui.Text("Match Champion", photoCaption, "", new Vector2(0, 11), new Vector2(-24, 33),
            28, Play2UGui.Gold, font, style: FontStyles.Bold);
        placements = Play2UGui.Text("Match Places", photoCaption, "", new Vector2(0, -16), new Vector2(-24, 23), 17, Play2UGui.Cream, font);
        champion.rectTransform.anchorMin = Vector2.up;
        champion.rectTransform.anchorMax = Vector2.one;
        champion.rectTransform.anchoredPosition = new Vector2(0, -20);
        placements.rectTransform.anchorMin = Vector2.zero;
        placements.rectTransform.anchorMax = Vector2.right;
        placements.rectTransform.pivot = new Vector2(.5f, 0);
        placements.rectTransform.anchoredPosition = new Vector2(0, 4);
        photoNote = Play2HudArt.Plate("Dealer Recap", root, Vector2.zero, Vector2.zero, "PlayingBadge");
        photoNote.anchorMin = Vector2.zero;
        photoNote.anchorMax = new Vector2(.43f, 0);
        photoNote.offsetMin = new Vector2(17, 79);
        photoNote.offsetMax = new Vector2(-2, 175);
        var quoteHeading = Play2UGui.Text("Quote Heading", photoNote, "딜러가 고른 마지막 말", Vector2.zero, Vector2.zero,
            13, Play2UGui.Gold, font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        quoteHeading.rectTransform.anchorMin = Vector2.up;
        quoteHeading.rectTransform.anchorMax = Vector2.one;
        quoteHeading.rectTransform.offsetMin = new Vector2(12, -27);
        quoteHeading.rectTransform.offsetMax = new Vector2(-12, -5);
        photoQuote = Play2UGui.Text("Dealer's Last Word", photoNote, "", Vector2.zero, Vector2.zero, 17,
            Play2UGui.Cream, font, TextAlignmentOptions.MidlineLeft);
        photoQuote.rectTransform.anchorMin = Vector2.zero;
        photoQuote.rectTransform.anchorMax = Vector2.one;
        photoQuote.rectTransform.offsetMin = new Vector2(12, 8);
        photoQuote.rectTransform.offsetMax = new Vector2(-12, -26);
        Play2HudArt.Fit(photoQuote, 13);
        photoPose = Play2HudArt.Button("Photo Pose", root, "내 포즈 바꾸기", new Vector2(92, 48), new Vector2(150, 38),
            "PlayingAction", Play2UGui.Cream, font, () => { settlementCard.StayForPhoto(); table.CyclePhotoPose(); }, 16);
        photoPose.GetComponent<RectTransform>().anchorMin = photoPose.GetComponent<RectTransform>().anchorMax = Vector2.zero;
        champion.richText = placements.richText = photoQuote.richText = false;
        Play2HudArt.Fit(champion, 17);
        Play2HudArt.Fit(placements, 12);
        photoCaption.gameObject.SetActive(false);
        photoNote.gameObject.SetActive(false);
        photoPose.gameObject.SetActive(false);
        settlementCard = new SettlementCard(root, font, AdvanceHand, table.ToggleRematchReady, table.WatchRemainingPractice);
        topControls = new[] { (RectTransform)leaveButton.transform, potPanel, handGuide.HelpButton,
            (RectTransform)settingsButton.transform, noticePanel };
        BuildVillageChrome(font);
        settings = new Play2InGameSettings((RectTransform)parent, font, owner.ApplyTableTheme);
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
            handGuide.Close();
            settlementCard.Hide();
            CloseActionPanels();
            root.gameObject.SetActive(false);
            return;
        }
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
        if (value.game.isOver && settlementReady && value.game.hasResult) table.BeginFinale();
        var finale = table.FinaleBeat;
        bool group = finale >= Play2MatchFinale.Beat.Pullback;
        bool portrait = finale >= Play2MatchFinale.Beat.Portrait;
        bool resultBecameVisible = value.game.hasResult && settlementReady
            && (!wasSettlementReady || packet?.game.hasResult != true);
        if (!themeChanged && ReferenceEquals(packet, value) && lastStatus == status && wasDealing == dealing
            && wasSettlementReady == settlementReady && wasWaitingToReveal == table.WaitingToReveal && wasActionPending == actionPending && lastBeat == beat
            && lastFinale == finale && lastAwardIndex == table.AwardIndex && lastRevealingSeat == table.RevealingSeat
            && (!portrait || Mathf.Approximately(photoWidth, root.rect.width))) return;
        themeChanged = false;
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
        lastFinale = finale;
        lastBeat = beat;
        lastAwardIndex = table.AwardIndex;
        lastRevealingSeat = table.RevealingSeat;
        wasDealing = dealing;
        wasWaitingToReveal = table.WaitingToReveal;
        wasSettlementReady = settlementReady;
        root.gameObject.SetActive(true);
        var game = packet.game;
        photoCaption.gameObject.SetActive(portrait);
        photoPose.gameObject.SetActive(portrait && packet.hasChat);
        photoPose.interactable = !packet.paused && !actionPending;
        potAndAnte.transform.parent.gameObject.SetActive(!group);
        var selectionForPhoto = finale == Play2MatchFinale.Beat.Recap
            ? packet.dealerSelections?.LastOrDefault(s => s != null && s.handId == game.handId && s.cardChanged
                && s.hasAppliedCard && s.boardIndex >= 0 && s.boardIndex < game.board.Length && game.board[s.boardIndex] == s.card)
            : null;
        photoNote.gameObject.SetActive(selectionForPhoto != null);
        if (selectionForPhoto != null)
            photoQuote.text = $"{DisplayName(selectionForPhoto.speaker)}  “{Brief(selectionForPhoto.text, 64)}”\n{selectionForPhoto.boardIndex + 1}번째 공용 카드  →  {Play2HandGuide.CardText(Poker.Foundation.Card.FromId(selectionForPhoto.card))}";
        if (portrait)
        {
            champion.text = $"최종 우승   {Name(game.sessionWinner)}";
            LayoutFinalPlaces(game);
        }
        showdownCaption.gameObject.SetActive(showingHands || showingAwards);
        if (showingHands)
        {
            showdownTitle.text = "쇼다운";
            showdownDetail.text = table.RevealingSeat > 0 ? $"{Name(table.RevealingSeat)} 패 공개" : "패 공개";
        }
        else if (showingAwards)
        {
            var paid = game.seats.FirstOrDefault(seat => seat.seat == table.HighlightSeat);
            showdownTitle.text = paid == null ? "쇼다운" : Play2SettlementSummary.HandName(Play2SettlementSummary.Value(paid));
            var award = table.CurrentAward;
            showdownDetail.text = award == null ? "" : award.Caption(game, DisplayName);
        }
        if (previous?.handId != game.handId)
        {
            shownNotices.Clear();
            noticeKey = null;
            notice.transform.parent.gameObject.SetActive(false);
        }
        street.gameObject.SetActive(game.postedAnte > 0 && !game.hasResult);
        street.text = EntryFee(game, packet.rules);
        if (!village) potAndAnte.rectTransform.anchoredPosition = new Vector2(0, street.gameObject.activeSelf ? 6 : 0);
        phase.text = PhaseName(game.street);
        long paidTotal = 0;
        long myShownStack = 0;
        string myStatus = "";
        bool continuedHand = previous != null && previous.handId == game.handId && previousPacket.sessionId == packet.sessionId
            && previousPacket.viewerSeat == packet.viewerSeat;
        for (int i = 0; i < seatLabels.Length; i++)
        {
            var seat = game.seats.FirstOrDefault(s => s.seat == i + 1);
            seatPanels[i].gameObject.SetActive(!group && seat != null
                && !(seat.viewer && (village || dealing && game.board.Length == 0 && !game.hasResult)));
            if (seat == null) continue;
            long displayedAward = showingAwards && table.CurrentAward != null
                ? table.CurrentAward.PayoutThroughThisPot(game, seat.seat) : awaitingWinner ? 0L : seat.awarded;
            paidTotal += displayedAward;
            if (displayedAward > 0)
                table.ShowPayout(seat.seat, displayedAward, continuedHand);
            long shownStack = seat.stack - seat.awarded + displayedAward;
            seatLabels[i].text = seat.viewer ? "" : Name(seat.seat);
            seatStacks[i].text = seat.viewer ? $"내 칩  {shownStack}" : $"{shownStack}칩";
            seatStates[i].text = seat.status == (int)HoldemSeatStatus.Folded ? "폴드"
                : seat.status == (int)HoldemSeatStatus.AllIn ? "올인"
                : beforeAward && seat.status == (int)HoldemSeatStatus.Busted && Play2SettlementSummary.IsPublicHand(seat) ? "올인"
                : seat.status == (int)HoldemSeatStatus.Busted ? "탈락"
                : !game.hasResult && seat.streetContribution > 0 ? $"베팅 {seat.streetContribution}칩" : "";
            if (!game.hasResult && seat.status == (int)HoldemSeatStatus.Active && packet.hasHistory)
            {
                var lastWager = packet.history?.entries?.LastOrDefault(entry => entry.seat == seat.seat
                    && (entry.hasAction || entry.kind == (int)HoldemHistoryKind.UncalledReturn));
                if (lastWager?.kind == (int)HoldemHistoryKind.UncalledReturn)
                    seatStates[i].text = $"반환 {lastWager.amount}칩";
            }
            if (game.hasResult && table.IsHandShown(seat.seat) && !seat.viewer && seat.revealedBestCards?.Length == 5)
            {
                var valueOfHand = HandEvaluator.Evaluate(seat.revealedBestCards.Select(Poker.Foundation.Card.FromId).ToArray());
                seatStates[i].text = Play2SettlementSummary.HandName(valueOfHand);
            }
            bool won = game.hasResult && displayedAward > 0;
            if (won)
            {
                bool hasPublicRank = !seat.viewer && seat.revealedBestCards?.Length == 5 && table.IsHandShown(seat.seat);
                bool shared = game.result?.pots?.Length > 1 || game.result?.pots?.FirstOrDefault()?.payouts?.Length > 1;
                string resultLabel = hasPublicRank ? seatStates[i].text : shared ? "지급" : "승리";
                seatStates[i].text = resultLabel + "  +" + displayedAward + "칩";
            }
            else if (game.hasResult && settlementReady && seat.viewer)
            {
                seatStates[i].text = !seat.dealtIn ? "관전" : seat.status == (int)HoldemSeatStatus.Folded ? "폴드"
                    : seat.status == (int)HoldemSeatStatus.Busted ? "탈락" : "이번 판 패배";
            }
            else if (seat.viewer && (seat.status == (int)HoldemSeatStatus.Folded || seat.status == (int)HoldemSeatStatus.Busted))
                seatStates[i].text = "";
            bool hasTurn = seat.currentActor && !game.hasResult && !packet.paused
                && !game.dealPending && !game.revealPending && !dealing;
            seatTurns[i].enabled = hasTurn || won && !seat.viewer;
            seatTurns[i].rectTransform.anchoredPosition = new Vector2(seat.viewer ? -94 : -69, 0);
            seatOutlines[i].enabled = hasTurn && !seat.viewer;
            if (hasTurn && !seat.viewer)
                seatStates[i].text = "차례" + (seat.streetContribution > 0 ? $"  |  베팅 {seat.streetContribution}칩" : "");
            DrawSeatTheme(i, seat.viewer, hasTurn, won);
            if (seat.viewer)
            {
                myShownStack = shownStack;
                myStatus = seatStates[i].text;
            }
        }
        potAndAnte.text = paidTotal == 0 ? $"팟  {game.pot}칩"
            : paidTotal < game.pot ? $"남은 팟  {game.pot - paidTotal}칩"
            : village ? $"{paidTotal}칩 지급" : $"지급 완료  {paidTotal}칩";
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
        handGuide.Present(packet, dealing, group);
        handGuide.PresentWallet(myShownStack, myStatus);
        accusationHand.text = handGuide.CurrentRank;
        if (settlementReady && (finale == Play2MatchFinale.Beat.None || portrait)) settlementCard.Present(packet, settlement, table.IsHost, table.IsPractice, table.WatchingPractice, actionPending, DisplayName);
        else settlementCard.Hide();
        turn.text = packet.paused ? "연결 대기 중" : table.WaitingToReveal ? "칩 모으는 중"
            : game.dealPending ? "딜러가 요청을 읽고 있어요"
            : game.revealPending || dealing && game.board.Length > 0 ? "카드 공개 중"
            : dealing ? "손패 배분 중" : "처리 중";
        turn.color = Play2UGui.Cream;
        turn.gameObject.SetActive(packet.paused || dealing || !game.hasResult && (game.dealPending || game.revealPending || actionPending));
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
            if (selection != null && !dealing)
            {
                string card = Play2HandGuide.CardText(Poker.Foundation.Card.FromId(selection.card));
                string message = $"내 요청 채택   공용 {selection.boardIndex + 1}번째  {card}\n“{Brief(selection.text, 32)}”";
                ShowNotice($"dealer:{game.handId}:{selection.dealWindowId}", message);
            }
        }
    }

    private void OpenSettings()
    {
        CloseMoreActions();
        ClearSelection();
        settings.Open();
    }

    private static string EntryFee(HoldemGamePacket game, HoldemRoomRulesPacket rules)
    {
        string current = $"입장료 {game.postedAnte}칩";
        var steps = rules?.anteSteps;
        long round = game.roundInMatch;
        if (steps == null || round < 1 || round >= steps.Length) return current;
        for (int i = (int)round; i < steps.Length; i++)
        {
            if (steps[i] <= steps[(int)round - 1]) continue;
            string when = i == round ? "다음 판부터" : $"{i + 1}판부터";
            return $"{current}  →  {when} {steps[i]}칩";
        }
        return current;
    }

    private void LayoutFinalPlaces(HoldemGamePacket game)
    {
        var groups = game.seats.Where(seat => seat.finalPlace > 0)
            .OrderBy(seat => seat.finalPlace).GroupBy(seat => seat.finalPlace).ToArray();
        string Place(IGrouping<int, HoldemSeatPacket> group) => (group.Count() > 1 ? "공동 " : "") + group.Key + "위";
        string compact = string.Join("     ", groups.Select(group =>
            Place(group) + "  " + string.Join(", ", group.Select(seat => Name(seat.seat)))));

        photoWidth = root.rect.width;
        bool wide = placements.GetPreferredValues(compact, 10000f, 10000f).x > photoWidth * .68f - 24f;
        float margin = wide ? .045f : .16f;
        photoCaption.anchorMin = new Vector2(margin, 1);
        photoCaption.anchorMax = new Vector2(1f - margin, 1);
        if (wide)
        {
            var entries = groups.SelectMany(group => group.Select(seat => Place(group) + "  " + Name(seat.seat))).ToArray();
            compact = string.Join("     ", entries.Take(2));
            if (entries.Length > 2) compact += "\n" + string.Join("     ", entries.Skip(2));
        }
        placements.text = compact;
        float width = Mathf.Max(1f, photoWidth * (1f - 2f * margin) - 24f);
        float rowHeight = Mathf.Max(23f, Mathf.Ceil(placements.GetPreferredValues(compact, width, 10000f).y) + 3f);
        placements.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, rowHeight);
        float height = 40f + rowHeight;
        photoCaption.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        photoCaption.anchoredPosition = new Vector2(0, -52f - height / 2f);
    }

    // Read the finished head pose and seat camera after the all-in rise and first-person lean.
    internal void AlignTableLabels()
    {
        if (packet == null || !root.gameObject.activeInHierarchy) return;
        var area = root.rect;
        AlignVillageChrome();
        turn.rectTransform.anchoredPosition = new Vector2(0, (table.BoardTopViewportY - .5f) * area.height + 18f);
        for (int i = 0; i < seatPanels.Length; i++)
        {
            var plate = seatPanels[i];
            if (!plate.gameObject.activeSelf) continue;
            if (i + 1 == packet.viewerSeat)
            {
                seatVisibility[i].alpha = 1f;
                plate.anchoredPosition = new Vector2(-.17f * area.width, area.yMin + 43);
                continue;
            }

            var point = table.PlayerLabelViewport(i + 1);
            bool visible = point.z > 0f && point.x >= 0f && point.x <= 1f && point.y >= 0f && point.y <= 1f;
            seatVisibility[i].alpha = visible ? 1f : 0f;
            if (!visible) continue;
            Vector2 half = plate.rect.size * .5f;
            float x = Mathf.Clamp((point.x - .5f) * area.width, area.xMin + half.x + 8, area.xMax - half.x - 8);
            float y = Mathf.Clamp((point.y - .5f) * area.height + half.y + 8, area.yMin + 350, area.yMax - half.y - 4);
            if (village) y = Mathf.Min(y, area.yMax - VillageBarHeight - half.y - 8);
            foreach (var control in topControls)
            {
                if (!control.gameObject.activeSelf) continue;
                var rect = control.rect;
                var center = (Vector2)root.InverseTransformPoint(control.TransformPoint(rect.center));
                if (Mathf.Abs(x - center.x) < half.x + rect.width / 2 + 4
                    && Mathf.Abs(y - center.y) < half.y + rect.height / 2 + 4)
                    y = center.y - rect.height / 2 - half.y - 4;
            }
            plate.anchoredPosition = new Vector2(x, y);
        }
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
        if (chatFocused)
        {
            CloseMoreActions();
            return;
        }
        if (handGuide.IsOpen)
        {
            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.hKey.wasPressedThisFrame) handGuide.Close();
            return;
        }

        bool selecting = sizing.gameObject.activeSelf || accusation.gameObject.activeSelf;
        if (!selecting && keyboard.escapeKey.wasPressedThisFrame)
        {
            if (actionMenuOpen) CloseMoreActions();
            else OpenSettings();
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
        if (!acting)
        {
            CloseMoreActions();
            return;
        }

        var legal = packet.game.legal;
        string call = legal.callAmount > 0 ? $"콜 {legal.callAmount}칩" : "체크";
        SetAction(0, "폴드", legal.fold);
        SetAction(1, call, legal.check || legal.call);
        bool opening = packet.game.seats.All(s => s.streetContribution == 0);
        SetAction(2, !legal.bet && !legal.raise && legal.hasAllInTarget ? "올인" : opening ? "베팅" : "레이즈",
            legal.bet || legal.raise || legal.hasAllInTarget);
        SetAction(3, "고발", packet.game.canAccuseOnTurn);
        turnContext.text = legal.callAmount > 0 ? $"맞추려면 {legal.callAmount}칩" : "추가로 낼 칩 없음";
        RefreshTurnControls();
    }

    private void SetAction(int index, string label, bool enabled)
    {
        actionRows[index].GetComponent<Play2ActionButton>().Intent = $"{packet.game.handId}:{packet.game.street}:{packet.game.version}:{label}";
        actionOpacity[index].interactable = enabled;
        DrawActionTheme(index, enabled);
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
            Play2HudArt.Apply(plate, village
                ? selected ? "MarketPrimary" : "MarketButton"
                : selected ? "SelectedTarget" : "HeaderBadge");
            suspectNames[i].color = selected ? PaperRed : village ? Play2HudArt.MarketInk : Play2UGui.Cream;
            suspectNames[i].text = Name(suspects[i]);
        }
    }

    internal void TickPracticeAdvance(bool interacting)
    {
        if (!table.IsPractice || packet?.game == null || !wasSettlementReady) return;
        bool halt = interacting || packet.game.isOver && table.FinaleBeat < Play2MatchFinale.Beat.Recap || handGuide.IsOpen || settings.IsOpen || packet.paused || table.ActionPending || table.IsDealing || table.IsBetting;
        settlementCard.TickPractice(halt, AdvanceHand);
    }

    private void AdvanceHand()
    {
        if (packet?.game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority)
        {
            var command = Command("settle");
            command.oddChipRule = (int)HoldemOddChipRule.ClockwiseFromButton;
            table.Submit(command);
        }
        else if (table.IsPractice && (!table.WatchingPractice || packet?.game?.isOver == true) && packet?.game?.hasResult == true
            && Array.Exists(packet.game.seats, s => s.seat == packet.viewerSeat && s.stack == 0))
            table.NewPracticeAfterBust(packet.game.handId);
        else if (packet?.game?.isOver == true) table.RestartMatch();
        else table.StartHand();
    }

    private void CloseActionPanels()
    {
        CloseMoreActions();
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
