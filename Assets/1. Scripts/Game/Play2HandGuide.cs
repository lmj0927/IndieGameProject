using System;
using System.Linq;
using Poker.Application;
using Poker.Foundation;
using TMPro;
using UnityEngine;
using PokerCard = Poker.Foundation.Card;

internal sealed class Play2HandGuide
{
    private static readonly (string name, string cards)[] Ranks =
    {
        ("로열 플러시", "10♠ J♠ Q♠ K♠ A♠"),
        ("스트레이트 플러시", "5♥ 6♥ 7♥ 8♥ 9♥"),
        ("포카드", "7♠ 7♦ 7♥ 7♣ 2♠"),
        ("풀하우스", "K♣ K♦ K♠ 5♥ 5♣"),
        ("플러시", "2♦ 4♦ 5♦ 9♦ J♦"),
        ("스트레이트", "8♦ 9♠ 10♦ J♠ Q♣"),
        ("트리플", "3♠ 3♥ 3♣ 9♦ J♣"),
        ("투페어", "A♣ A♥ 3♠ 3♦ K♣"),
        ("원페어", "9♠ 9♣ 3♦ 5♥ J♠"),
        ("하이 카드", "A♦ J♣ 9♥ 6♣ 2♠")
    };

    private readonly TextMeshProUGUI summary, cards, wallet, result;
    private readonly RectTransform walletDivider;
    private readonly RectTransform panel, legend, overlay;
    private bool expanded, actionPickerOpen, village;
    internal RectTransform HelpButton { get; }
    internal bool IsOpen => expanded && !actionPickerOpen;
    private readonly UnityEngine.UI.Image[] rankBackgrounds = new UnityEngine.UI.Image[Ranks.Length];
    private string handId;
    private int[] holeCards = Array.Empty<int>(), board = Array.Empty<int>();
    private int rankIndex = -1;

    internal Play2HandGuide(RectTransform parent, RectTransform screen, TMP_FontAsset font)
    {
        panel = Play2HudArt.Plate("My Hand", parent, new Vector2(0, 56), new Vector2(202, 85), "PlayingBadge");
        panel.anchorMin = panel.anchorMax = new Vector2(.33f, 0);
        panel.GetComponent<UnityEngine.UI.Image>().color = Play2UGui.Hex("10191CBF");
        panel.SetAsFirstSibling();
        Play2HudArt.Clickable(panel, Toggle);
        summary = Play2UGui.Text("Current Rank", panel, "", new Vector2(0, 18), new Vector2(182, 32), 23,
            Play2UGui.Gold, font, style: FontStyles.Bold);
        summary.textWrappingMode = TextWrappingModes.NoWrap;
        Play2HudArt.Fit(summary, 16);
        wallet = Play2UGui.Text("Your Chips", panel, "", new Vector2(44, 0), new Vector2(102, 31), 20,
            Play2HudArt.MarketInk, font, TextAlignmentOptions.MidlineRight, FontStyles.Bold);
        wallet.textWrappingMode = TextWrappingModes.NoWrap;
        Play2HudArt.Fit(wallet, 13);
        walletDivider = Play2UGui.Panel("Wallet Divider", panel, Vector2.zero, new Vector2(1, 21),
            Play2UGui.Hex("C6B58F"));
        result = Play2UGui.Text("Your Result", panel, "", new Vector2(0, -17), new Vector2(212, 20), 15,
            Play2HudArt.MarketBrown, font, style: FontStyles.Bold);
        result.textWrappingMode = TextWrappingModes.NoWrap;
        Play2HudArt.Fit(result, 12);
        var help = Play2HudArt.Button("Hand Help", parent, "족보 ?", new Vector2(-131, -32), new Vector2(80, 34),
            "PlayingBadge", Play2UGui.Cream, font, Toggle, 16);
        HelpButton = help.GetComponent<RectTransform>();
        HelpButton.anchorMin = HelpButton.anchorMax = Vector2.one;
        overlay = Play2UGui.Fill("Hand Guide Overlay", screen);
        Play2UGui.Image(overlay, new Color(0, 0, 0, .28f), true);
        legend = Play2HudArt.Plate("Hand Rankings", overlay, Vector2.zero, new Vector2(480, 552), "PinePanel", true);
        legend.GetComponent<UnityEngine.UI.Image>().color = Play2UGui.Hex("152124FF");
        Play2UGui.Text("Title", legend, "포커 족보", new Vector2(-95, 238), new Vector2(220, 34), 25,
            Play2UGui.Cream, font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        Play2HudArt.Button("Close Hand Help", legend, "닫기", new Vector2(167, 239), new Vector2(80, 32),
            "ActionRow", Play2UGui.Cream, font, Close, 16);
        cards = Play2UGui.Text("Rank Detail", legend, "", new Vector2(0, -242), new Vector2(430, 29), 17,
            Play2UGui.Gold, font);
        for (int i = 0; i < Ranks.Length; i++)
        {
            var row = Play2UGui.Rect(Ranks[i].name, legend, new Vector2(0, 180 - 40 * i), new Vector2(426, 38));
            var emphasis = Play2UGui.Fill("Current Rank Highlight", row);
            rankBackgrounds[i] = Play2UGui.Image(emphasis, Play2UGui.Hex("C5A46A46"));
            emphasis.gameObject.SetActive(false);
            var label = Play2UGui.Text("Rank", row, Ranks[i].name, new Vector2(82, 0), new Vector2(222, 31), 18,
                Play2UGui.Cream, font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            string[] examples = Ranks[i].cards.Split(' ');
            for (int card = 0; card < examples.Length; card++) AddExampleCard(row, examples[card], card, font);
        }
        UpdateLayout();
        ShowRank(-1, "카드 배분 대기", "");
    }

    private static void AddExampleCard(RectTransform row, string face, int index, TMP_FontAsset font)
    {
        var card = Play2HudArt.Plate(face, row, Vector2.zero, Vector2.zero, "PlayingCard");
        card.anchorMin = Vector2.zero;
        card.anchorMax = Vector2.up;
        float x = 2 + index * 30;
        card.offsetMin = new Vector2(x, 2);
        card.offsetMax = new Vector2(x + 28, -2);

        string rank = face.Substring(0, face.Length - 1);
        char suit = face[face.Length - 1];
        var ink = suit == '♦' || suit == '♥' ? Play2UGui.Hex("8D1F1A") : Play2UGui.Ink;
        var rankLabel = Play2UGui.Text("Card Rank", card, rank, Vector2.zero, new Vector2(26, 16), 15,
            ink, font, style: FontStyles.Bold);
        SetCardTextBand(rankLabel, .43f, .96f);
        var suitLabel = Play2UGui.Text("Card Suit", card, suit.ToString(), Vector2.zero, new Vector2(26, 16), 14,
            ink, font, style: FontStyles.Bold);
        SetCardTextBand(suitLabel, .06f, .53f);
    }

    private static void SetCardTextBand(TextMeshProUGUI label, float bottom, float top)
    {
        label.rectTransform.anchorMin = new Vector2(0, bottom);
        label.rectTransform.anchorMax = new Vector2(1, top);
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        Play2HudArt.Fit(label, 6);
    }

    internal void Toggle()
    {
        expanded = !expanded;
        UpdateLayout();
    }

    internal void Close() { expanded = false; UpdateLayout(); }
    internal void SetActionPickerOpen(bool value) { actionPickerOpen = value; UpdateLayout(); }
    internal string CurrentRank => summary.text;

    internal void SetVillageTheme(bool value)
    {
        village = value;
        Play2HudArt.Apply(panel.GetComponent<UnityEngine.UI.Image>(), village ? "MarketSeat" : "PlayingBadge");
        if (!village) panel.GetComponent<UnityEngine.UI.Image>().color = Play2UGui.Hex("10191CBF");
        panel.anchorMin = panel.anchorMax = new Vector2(village ? .22f : .33f, 0);
        panel.sizeDelta = village ? new Vector2(230, 68) : new Vector2(202, 85);
        panel.anchoredPosition = new Vector2(0, village ? 54 : 56);
        summary.rectTransform.sizeDelta = village ? new Vector2(98, 31) : new Vector2(182, 32);
        summary.rectTransform.anchoredPosition = village ? new Vector2(-54, 0) : new Vector2(0, 18);
        summary.alignment = village ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Center;
        summary.color = village ? Play2HudArt.MarketMuted : Play2UGui.Gold;
        wallet.gameObject.SetActive(village);
        walletDivider.gameObject.SetActive(village);
        result.gameObject.SetActive(false);
    }

    internal void PresentWallet(long chips, string playerState)
    {
        if (!village) return;
        wallet.text = $"내 칩 {chips}";
        bool hasStatus = !string.IsNullOrEmpty(playerState);
        result.gameObject.SetActive(hasStatus);
        result.text = playerState;
        float y = hasStatus ? 8 : 0;
        summary.rectTransform.anchoredPosition = new Vector2(-54, y);
        walletDivider.anchoredPosition = new Vector2(1, y);
        wallet.rectTransform.anchoredPosition = new Vector2(53, y);
    }

    private void UpdateLayout()
    {
        overlay.gameObject.SetActive(IsOpen);
        if (IsOpen) overlay.SetAsLastSibling();
    }

    internal void Present(HoldemRoomPacket packet, bool dealing, bool portrait = false)
    {
        var game = packet.game;
        panel.gameObject.SetActive(!portrait && !(dealing && game.board.Length == 0 && !game.hasResult));
        if (handId != game.handId)
        {
            handId = game.handId;
            holeCards = board = Array.Empty<int>();
            ShowRank(-1, "카드 배분 대기", "");
        }

        var viewer = game.seats.FirstOrDefault(seat => seat.seat == packet.viewerSeat);
        if (viewer?.status == (int)HoldemSeatStatus.Folded || (viewer?.status == (int)HoldemSeatStatus.Busted
            && !Play2SettlementSummary.IsPublicHand(viewer)))
        {
            string label = viewer.status == (int)HoldemSeatStatus.Folded ? "폴드" : "탈락";
            ShowRank(-1, label, "대화는 계속할 수 있습니다.");
            holeCards = board = Array.Empty<int>();
            return;
        }
        if (dealing) return;
        var myCards = viewer?.dealtIn == true ? viewer.visibleCards : null;
        if (myCards?.Length != 2)
        {
            string label = viewer?.dealtIn == true ? "카드 배분 대기" : "이번 판 내 카드 없음";
            if (summary.text != label) ShowRank(-1, label, "");
            holeCards = board = Array.Empty<int>();
            return;
        }
        if (holeCards.SequenceEqual(myCards) && board.SequenceEqual(game.board)) return;
        holeCards = myCards;
        board = game.board;

        if (board.Length >= 3)
        {
            var best = HoldemBestHand.Evaluate(board.Select(PokerCard.FromId).ToArray(), myCards.Select(PokerCard.FromId).ToArray());
            var value = best.Value;
            int index = value.Category == HandCategory.StraightFlush && value.GetTieBreaker(0) == (int)Rank.Ace
                ? 0 : 10 - (int)value.Category;
            string bestCards = string.Join("   ", Enumerable.Range(0, best.BestCardCount).Select(best.GetBestCard)
                .OrderByDescending(card => card.Rank).Select(CardText));
            ShowRank(index, RankName(index, value.GetTieBreaker(0), index == 7 ? value.GetTieBreaker(1) : 0), bestCards);
            return;
        }

        var groups = myCards.Concat(board).Select(PokerCard.FromId).GroupBy(card => (int)card.Rank)
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key).ToArray();
        int count = groups[0].Count();
        bool twoPairs = count == 2 && groups.Length > 1 && groups[1].Count() == 2;
        int partialIndex = count == 4 ? 2 : count == 3 ? 6 : twoPairs ? 7 : count == 2 ? 8 : 9;
        ShowRank(partialIndex, RankName(partialIndex, groups[0].Key, twoPairs ? groups[1].Key : 0),
            $"현재 보이는 카드 {myCards.Length + board.Length}장");
    }

    private void ShowRank(int index, string label, string detail)
    {
        if (rankIndex >= 0)
        {
            rankBackgrounds[rankIndex].gameObject.SetActive(false);
        }
        rankIndex = index;
        if (index >= 0)
        {
            rankBackgrounds[index].gameObject.SetActive(true);
        }
        summary.text = label;
        cards.text = detail;
    }

    private static string RankName(int index, int high, int low)
    {
        if (index == 7) return $"{RankText(high)}, {RankText(low)} 투페어";
        if (index == 2 || index == 6 || index == 8 || index == 9) return $"{RankText(high)} {Ranks[index].name}";
        return Ranks[index].name;
    }

    internal static string CategoryLabel(HandValue value) =>
        Ranks[value.Category == HandCategory.StraightFlush && value.GetTieBreaker(0) == (int)Rank.Ace
            ? 0 : 10 - (int)value.Category].name;

    internal static string CardText(PokerCard card) => RankText((int)card.Rank) + "♣♦♥♠"[(int)card.Suit - 1];

    private static string RankText(int rank) => rank switch
    {
        (int)Rank.Ace => "A",
        (int)Rank.King => "K",
        (int)Rank.Queen => "Q",
        (int)Rank.Jack => "J",
        _ => rank.ToString()
    };
}
