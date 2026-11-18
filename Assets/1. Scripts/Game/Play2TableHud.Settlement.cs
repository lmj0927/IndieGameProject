using System;
using System.Linq;
using Poker.Application;
using Poker.Foundation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

internal sealed partial class Play2TableHud
{
    private sealed class SettlementCard
    {
        private readonly RectTransform card, content, tableFooter;
        private readonly TMP_FontAsset font;
        private readonly TextMeshProUGUI title, reason, heading;
        private readonly Button next, review, rematchReady;
        private readonly ScrollRect scroll;
        private readonly TextMeshProUGUI nextLabel, rematchLabel, rematchStatus;
        private readonly SeatRow[] seats = new SeatRow[4];
        private readonly RequestRow[] requests = new RequestRow[3];
        private readonly TextMeshProUGUI requestHeading, potDetails, accusationDetails;
        private readonly TextMeshProUGUI tableNoAccusation;
        private const string NoAccusation = "이번 판에는 고발이 없었어요";
        private string handId;
        private bool viewingTable;
        private bool hadResult, hadOddChips;
        private const float PracticeReadSeconds = 4f;
        private float practiceSeconds;
        private bool autoAdvance, reviewHeld, offerSpectating;
        private string practiceAction;
        private int shownSeconds;

        internal SettlementCard(RectTransform parent, TMP_FontAsset font, UnityEngine.Events.UnityAction advance,
            UnityEngine.Events.UnityAction ready, UnityEngine.Events.UnityAction spectate)
        {
            this.font = font;
            card = Play2HudArt.Plate("Settlement Card", parent, Vector2.zero, new Vector2(800, 632), "PinePanel", true);
            card.GetComponent<Image>().color = Play2UGui.Hex("152124FF");
            heading = Label("Result Type", card, new Vector2(-230, 283), new Vector2(290, 24), 15, Play2UGui.Gold);
            title = Label("Winners", card, new Vector2(-80, 249), new Vector2(590, 45), 30, Play2UGui.Cream);
            Play2HudArt.Fit(title, 18);
            reason = Label("Win Reason", card, new Vector2(0, 207), new Vector2(750, 43), 17, Play2UGui.Cream);
            reason.textWrappingMode = TextWrappingModes.Normal;
            Play2HudArt.Fit(reason, 14);
            AnchorTop(heading.rectTransform, new Vector2(-230, -33));
            AnchorTop(title.rectTransform, new Vector2(-80, -67));
            AnchorTop(reason.rectTransform, new Vector2(0, -109));
            var inspect = Play2HudArt.Button("Inspect Table", card, "테이블 보기", new Vector2(317, 281), new Vector2(116, 32),
                "ActionRow", Play2UGui.Cream, font, () => SetTableView(true), 15);
            AnchorTop((RectTransform)inspect.transform, new Vector2(317, -35));
            tableFooter = Play2HudArt.Plate("Table Result Actions", parent, new Vector2(-190, 58), new Vector2(354, 90), "PlayingBadge");
            tableFooter.anchorMin = tableFooter.anchorMax = Vector2.right;
            tableNoAccusation = Label("No Accusation", tableFooter, Vector2.zero, new Vector2(332, 22), 15, Play2UGui.Cream);
            tableNoAccusation.alignment = TextAlignmentOptions.Center;
            AnchorTop(tableNoAccusation.rectTransform, new Vector2(0, -14));
            tableNoAccusation.text = NoAccusation;
            tableFooter.gameObject.SetActive(false);
            review = Play2HudArt.Button("Review Settlement", tableFooter, "결과 자세히", Vector2.zero, new Vector2(132, 41),
                "ActionRow", Play2UGui.Cream, font, () => { reviewHeld = true; nextLabel.text = practiceAction; SetTableView(false); }, 17);
            var reviewRect = (RectTransform)review.transform;
            reviewRect.anchorMin = reviewRect.anchorMax = Vector2.right;
            review.gameObject.SetActive(false);

            var viewport = Play2UGui.Fill("Result Viewport", card);
            viewport.offsetMin = new Vector2(22, 78);
            viewport.offsetMax = new Vector2(-22, -132);
            Play2UGui.Image(viewport, Color.clear, true);
            viewport.gameObject.AddComponent<RectMask2D>();
            content = Play2UGui.Rect("Result Content", viewport, Vector2.zero, new Vector2(0, 450));
            content.anchorMin = Vector2.up;
            content.anchorMax = Vector2.one;
            content.pivot = Vector2.up;
            content.anchoredPosition = Vector2.zero;
            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28;
            var rail = Play2UGui.Fill("Result Scrollbar", card);
            rail.anchorMin = Vector2.right;
            rail.anchorMax = Vector2.one;
            rail.offsetMin = new Vector2(-14, 78);
            rail.offsetMax = new Vector2(-8, -132);
            Play2UGui.Image(rail, Play2UGui.Hex("FFFFFF12"), true);
            var handle = Play2UGui.Fill("Result Scroll Handle", rail);
            var scrollbar = rail.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = Play2UGui.Image(handle, Play2UGui.Hex("A7B3AA99"), true);
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            var headers = TopRect("Result Columns", content, 0, 26);
            Column("플레이어", -291, 144);
            Column("핸드", -159, 82);
            Column("족보", -32, 150);
            Column("최종 5장", 146, 190);
            Column("획득", 316, 96);
            for (int i = 0; i < seats.Length; i++) seats[i] = new SeatRow(content, font);
            potDetails = Label("Pot Breakdown", content, Vector2.zero, new Vector2(744, 60), 15, Play2UGui.Cream);
            potDetails.rectTransform.anchorMin = potDetails.rectTransform.anchorMax = Vector2.up;
            potDetails.rectTransform.pivot = Vector2.up;
            potDetails.textWrappingMode = TextWrappingModes.Normal;
            accusationDetails = Label("Accusation Recap", content, Vector2.zero, new Vector2(744, 72), 15, Play2UGui.Cream);
            accusationDetails.rectTransform.anchorMin = accusationDetails.rectTransform.anchorMax = Vector2.up;
            accusationDetails.rectTransform.pivot = Vector2.up;
            accusationDetails.textWrappingMode = TextWrappingModes.Normal;
            requestHeading = Label("Selected Requests", content, Vector2.zero, new Vector2(744, 26), 18, Play2UGui.Gold);
            requestHeading.rectTransform.anchorMin = requestHeading.rectTransform.anchorMax = Vector2.up;
            requestHeading.rectTransform.pivot = Vector2.up;
            requestHeading.text = "채택된 딜러 요청";
            for (int i = 0; i < requests.Length; i++) requests[i] = new RequestRow(content, font);

            next = Play2HudArt.Button("Next Hand", card, "다음 판", new Vector2(294, -277), new Vector2(162, 43),
                "GoldButton", PaperInk, font, advance, 20);
            nextLabel = next.GetComponentInChildren<TextMeshProUGUI>();
            var nextRect = (RectTransform)next.transform;
            nextRect.anchorMin = nextRect.anchorMax = new Vector2(.5f, 0);
            nextRect.anchoredPosition = new Vector2(294, 39);
            rematchReady = Play2HudArt.Button("Rematch Ready", card, "재경기 준비", Vector2.zero, new Vector2(162, 43),
                "ActionRow", Play2UGui.Cream, font, () => { if (offerSpectating) spectate(); else ready(); }, 18);
            var readyRect = (RectTransform)rematchReady.transform;
            readyRect.anchorMin = readyRect.anchorMax = new Vector2(.5f, 0);
            readyRect.anchoredPosition = new Vector2(-294, 39);
            rematchLabel = rematchReady.GetComponentInChildren<TextMeshProUGUI>();
            foreach (var button in new[] { review, next, rematchReady })
            {
                var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
                var rect = label.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(5, 2);
                rect.offsetMax = new Vector2(-5, -2);
                Play2HudArt.Fit(label, 13);
            }
            rematchStatus = Label("Rematch Status", card, Vector2.zero, new Vector2(380, 38), 15, Play2UGui.Cream);
            rematchStatus.rectTransform.anchorMin = rematchStatus.rectTransform.anchorMax = new Vector2(.5f, 0);
            rematchStatus.rectTransform.anchoredPosition = new Vector2(0, 39);
            rematchStatus.alignment = TextAlignmentOptions.Center;
            var divider = Play2UGui.Panel("Result Footer Rule", card, new Vector2(0, -243), new Vector2(750, 1), Play2UGui.Hex("FFFFFF25"));
            divider.anchorMin = divider.anchorMax = new Vector2(.5f, 0);
            divider.anchoredPosition = new Vector2(0, 73);
            card.gameObject.SetActive(false);

            void Column(string text, float x, float width)
            {
                Label(text, headers, new Vector2(x, 0), new Vector2(width, 24), 14, Play2UGui.Hex("BAC5C4")).text = text;
            }
        }

        private TextMeshProUGUI Label(string label, Transform parent, Vector2 position, Vector2 size, int fontSize, Color color)
        {
            var text = Play2UGui.Text(label, parent, "", position, size, fontSize, color, font,
                TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            text.richText = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }

        private static RectTransform TopRect(string name, RectTransform parent, float top, float height)
        {
            var rect = Play2UGui.Rect(name, parent, new Vector2(0, -top), new Vector2(0, height));
            rect.anchorMin = Vector2.up;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(.5f, 1);
            return rect;
        }

        private static void AnchorTop(RectTransform rect, Vector2 position)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 1);
            rect.anchoredPosition = position;
        }

        private void SetTableView(bool view)
        {
            viewingTable = view;
            card.gameObject.SetActive(!view);
            tableFooter.gameObject.SetActive(view);
            review.gameObject.SetActive(view);
            MoveControl(next.transform, view ? tableFooter : card, 294, 39, 162);
            MoveControl(rematchReady.transform, view ? tableFooter : card, -294, 39, 162);
            MoveControl(rematchStatus.transform, view ? tableFooter : card, 0, view ? 65 : 39, view ? 337 : 380);
            if (!view) return;
            int count = 1 + (rematchReady.gameObject.activeSelf ? 1 : 0) + (next.gameObject.activeSelf ? 1 : 0);
            float width = count == 3 ? 105 : 143;
            float x = -(count - 1) * (width + 7) * .5f;
            float y = rematchStatus.gameObject.activeSelf ? 26 : 35;
            float noteHeight = tableNoAccusation.gameObject.activeSelf ? 24 : 0;
            tableFooter.anchoredPosition = new Vector2(-190, 58 + noteHeight / 2);
            tableFooter.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, (rematchStatus.gameObject.activeSelf ? 90 : 70) + noteHeight);
            MoveControl(review.transform, tableFooter, x, y, width);
            if (rematchReady.gameObject.activeSelf) MoveControl(rematchReady.transform, tableFooter, x += width + 7, y, width);
            if (next.gameObject.activeSelf) MoveControl(next.transform, tableFooter, x + width + 7, y, width);
        }

        private static void MoveControl(Transform transform, RectTransform parent, float x, float y, float width)
        {
            var rect = (RectTransform)transform;
            if (rect.parent != parent) rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0);
            rect.anchoredPosition = new Vector2(x, y);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        }

        internal void Hide()
        {
            card.gameObject.SetActive(false);
            tableFooter.gameObject.SetActive(false);
        }

        internal void Present(HoldemRoomPacket packet, bool oddChips, bool isHost, bool practice, bool watching, bool pending, Func<int, string> name)
        {
            var game = packet.game;
            if (!game.hasResult && !oddChips) { Hide(); return; }
            bool newHand = handId != game.handId;
            var own = practice ? Array.Find(game.seats, seat => seat.seat == packet.viewerSeat) : null;
            bool busted = game.hasResult && own != null && own.stack == 0;
            bool newPractice = busted && (!watching || game.isOver);
            offerSpectating = newPractice && game.canContinue;
            bool folded = own != null && own.status == (int)HoldemSeatStatus.Folded;
            bool continueFold = folded && game.hasResult && game.canContinue;
            bool continueSpectating = busted && watching && game.canContinue;
            bool automatic = practice && (oddChips || continueFold || continueSpectating || newPractice && !watching);
            if (newHand || game.hasResult && !hadResult || oddChips && !hadOddChips)
            {
                viewingTable = game.hasResult && (automatic || game.result?.kind == (int)HoldemResultKind.Showdown);
                autoAdvance = automatic;
                reviewHeld = false;
                practiceSeconds = PracticeReadSeconds;
            }
            handId = game.handId;
            hadResult = game.hasResult;
            hadOddChips = oddChips;
            bool rematch = !busted && game.isOver && packet.hasRematch && packet.rematchSupported;
            var participants = packet.members ?? Array.Empty<HoldemRoomMemberPacket>();
            bool allReady = participants.Length > 0 && participants.All(member => member.connected && member.rematchReady);
            bool ownReady = participants.Any(member => member.seat == packet.viewerSeat && member.rematchReady);
            next.gameObject.SetActive(isHost && (oddChips || game.canContinue || rematch || busted));
            next.interactable = !packet.paused && !pending && (!rematch || allReady);
            practiceAction = oddChips ? "정산하기" : newPractice ? "새 연습" : rematch ? "다시 시작" : "다음 판";
            nextLabel.text = pending ? "처리 중" : practiceAction;
            shownSeconds = -1;
            rematchReady.gameObject.SetActive(rematch || offerSpectating);
            rematchReady.interactable = !packet.paused && !pending;
            rematchLabel.text = offerSpectating ? "계속 관전" : ownReady ? "준비 취소" : "재경기 준비";
            bool waitingForHost = !isHost && !game.isOver && !oddChips && game.canContinue;
            rematchStatus.gameObject.SetActive(!busted && game.isOver || waitingForHost);
            rematchStatus.text = waitingForHost ? "방장이 다음 판을 시작하면 이어집니다."
                : rematch ? "재경기 준비 " + participants.Count(member => member.connected && member.rematchReady) + "/" + participants.Length
                : "방을 나가 새 경기를 시작할 수 있습니다.";
            string recap = game.hasResult ? AccusationRecap(packet, name) : "";
            tableNoAccusation.gameObject.SetActive(recap == NoAccusation);
            SetTableView(viewingTable);
            heading.text = game.isOver ? "경기 종료" : oddChips ? "칩 정산" : "이번 판 결과";
            title.text = Play2SettlementSummary.Heading(game, name, packet.viewerSeat);
            reason.text = Play2SettlementSummary.Reason(packet, name);
            if (oddChips && !isHost) reason.text += " 방장의 정산을 기다리고 있습니다.";
            float y = 30;
            var ordered = game.seats.Where(seat => seat.dealtIn).OrderByDescending(seat => seat.awarded)
                .ThenBy(seat => seat.status == (int)HoldemSeatStatus.Folded).ThenBy(seat => seat.seat).ToArray();
            for (int i = 0; i < seats.Length; i++)
            {
                seats[i].frame.gameObject.SetActive(i < ordered.Length);
                if (i >= ordered.Length) continue;
                seats[i].Present(ordered[i], name(ordered[i].seat), y);
                y += 68;
            }
            var pots = (game.result?.pots ?? Array.Empty<HoldemPotPacket>()).Where(pot => pot != null).ToArray();
            bool showPots = pots.Length > 1 || pots.Any(pot => pot.payouts?.Any(payout => payout?.includesOddChip == true) == true);
            potDetails.gameObject.SetActive(showPots);
            if (showPots)
            {
                potDetails.text = string.Join("\n", pots.Select((pot, i) => (i == 0 ? "메인 팟" : "사이드 팟 " + i)
                    + " " + pot.amount + "칩  " + string.Join(", ", (pot.payouts ?? Array.Empty<HoldemPayoutPacket>()).Where(payout => payout != null).Select(payout => name(payout.seat)
                    + " +" + payout.amount + "칩" + (payout.includesOddChip ? " (나머지 1칩 포함)" : "")))
                    + "\n" + Play2SettlementSummary.PotReason(game, pot)));
                potDetails.rectTransform.anchoredPosition = new Vector2(6, -y);
                float height = potDetails.GetPreferredValues(potDetails.text, 744, float.PositiveInfinity).y + 12;
                potDetails.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                y += height;
            }
            accusationDetails.gameObject.SetActive(recap.Length != 0);
            if (recap.Length != 0)
            {
                accusationDetails.text = recap;
                accusationDetails.rectTransform.anchoredPosition = new Vector2(6, -y);
                float height = accusationDetails.GetPreferredValues(recap, 744, float.PositiveInfinity).y + 12;
                accusationDetails.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                y += height;
            }
            var selections = packet.dealerSelections ?? Array.Empty<HoldemDealerSelectionPacket>();
            int count = game.hasResult ? Mathf.Min(requests.Length, selections.Length) : 0;
            requestHeading.gameObject.SetActive(count > 0);
            requestHeading.rectTransform.anchoredPosition = new Vector2(6, -y - 4);
            if (count > 0) y += 34;
            for (int i = 0; i < requests.Length; i++)
            {
                requests[i].frame.gameObject.SetActive(i < count);
                if (i >= count) continue;
                y += requests[i].Present(selections[i], name(selections[i].speaker), y);
            }
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, y + 4);
            card.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Clamp(y + 214, 350, 632));
            if (newHand) scroll.verticalNormalizedPosition = 1;
        }

        internal void TickPractice(bool halt, Action advance)
        {
            if (!autoAdvance || reviewHeld || !next.isActiveAndEnabled || !next.interactable) return;
            if (!halt) practiceSeconds -= Mathf.Min(Time.unscaledDeltaTime, .25f);
            int seconds = Mathf.Max(0, Mathf.CeilToInt(practiceSeconds));
            if (seconds != shownSeconds)
            {
                shownSeconds = seconds;
                nextLabel.text = $"{seconds}초 후 {practiceAction}";
            }
            if (practiceSeconds > 0f) return;
            autoAdvance = false;
            advance();
        }

        private static string AccusationRecap(HoldemRoomPacket packet, Func<int, string> name)
        {
            var outcomes = packet.game.accusationOutcomes ?? Array.Empty<HoldemAccusationOutcomePacket>();
            var rows = outcomes.Select(o => $"{name(o.accuser)} → {name(o.target)}   {(o.hit ? "고발 적중" : "고발 실패")}"
                + (o.fine > 0 ? $"   벌금 {o.fine}칩" : "")).ToList();
            var last = packet.hasTurnAccusation ? packet.turnAccusation : null;
            if (last != null && last.handId == packet.game.handId &&
                !outcomes.Any(o => o.accuser == last.accuser && o.target == last.target && o.street == last.street && o.hit == last.caught))
                rows.Add($"{name(last.accuser)} → {name(last.target)}   {(last.caught ? "고발 적중" : "고발 실패")}");
            return rows.Count == 0 ? NoAccusation : (outcomes.Length > 0 ? "고발 결과\n" : "최근 고발\n") + string.Join("\n", rows);
        }

        private sealed class SeatRow
        {
            internal readonly RectTransform frame;
            private readonly TextMeshProUGUI name, paid, category, award;
            private readonly MiniCard[] hand = new MiniCard[2], best = new MiniCard[5];
            private readonly Image background;
            internal SeatRow(RectTransform parent, TMP_FontAsset font)
            {
                frame = TopRect("Showdown Player", parent, 0, 62);
                background = Play2UGui.Image(frame, Play2UGui.Hex("FFFFFF09"));
                name = Text("Player", -291, -18, 144, 29, 17);
                name.textWrappingMode = TextWrappingModes.Normal;
                name.overflowMode = TextOverflowModes.Ellipsis;
                paid = Text("Contribution", -291, -43, 144, 20, 13);
                category = Text("Hand Category", -32, -31, 150, 42, 18);
                category.textWrappingMode = TextWrappingModes.NoWrap;
                award = Text("Award", 316, -31, 96, 42, 21);
                for (int i = 0; i < hand.Length; i++) hand[i] = new MiniCard(frame, font, new Vector2(-179 + i * 39, -31));
                for (int i = 0; i < best.Length; i++) best[i] = new MiniCard(frame, font, new Vector2(68 + i * 39, -31));

                TextMeshProUGUI Text(string label, float x, float y, float w, float h, int size)
                {
                    var text = Play2UGui.Text(label, frame, "", new Vector2(x, y), new Vector2(w, h), size,
                        Play2UGui.Cream, font, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
                    text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(.5f, 1);
                    text.richText = false;
                    Play2HudArt.Fit(text, 12);
                    return text;
                }
            }
            internal void Present(HoldemSeatPacket seat, string displayName, float y)
            {
                frame.anchoredPosition = new Vector2(0, -y);
                bool shown = Play2SettlementSummary.IsPublicHand(seat);
                name.text = displayName;
                paid.text = "잔액 " + seat.stack + "칩";
                category.text = shown ? Play2SettlementSummary.HandName(Play2SettlementSummary.Value(seat))
                    : seat.status == (int)HoldemSeatStatus.Folded ? "폴드" : "핸드 비공개";
                award.text = seat.awarded > 0 ? "+" + seat.awarded + "칩" : "—";
                award.color = seat.awarded > 0 ? Play2UGui.Gold : Play2UGui.Hex("899A98");
                background.color = seat.awarded > 0 ? Play2UGui.Hex("514930FF") : Play2UGui.Hex("243236FF");
                for (int i = 0; i < hand.Length; i++) hand[i].Present(shown ? seat.visibleCards[i] : -1);
                var ordered = shown ? Play2SettlementSummary.OrderedBestCards(seat) : Array.Empty<int>();
                for (int i = 0; i < best.Length; i++) best[i].Present(shown ? ordered[i] : -1);
            }
        }

        private sealed class MiniCard
        {
            private readonly RectTransform frame;
            private readonly TextMeshProUGUI rank, suit;
            internal MiniCard(Transform parent, TMP_FontAsset font, Vector2 position)
            {
                frame = Play2HudArt.Plate("Result Card", parent, position, new Vector2(34, 46), "CreamPanel");
                frame.anchorMin = frame.anchorMax = new Vector2(.5f, 1);
                rank = Play2UGui.Text("Rank", frame, "", new Vector2(0, 9), new Vector2(31, 23), 19, PaperInk, font, style: FontStyles.Bold);
                suit = Play2UGui.Text("Suit", frame, "", new Vector2(0, -12), new Vector2(31, 21), 18, PaperInk, font);
            }
            internal void Present(int id)
            {
                frame.gameObject.SetActive(id >= 0);
                if (id < 0) return;
                var value = Poker.Foundation.Card.FromId(id);
                string face = Play2HandGuide.CardText(value);
                rank.text = face.Substring(0, face.Length - 1);
                suit.text = face.Substring(face.Length - 1);
                rank.color = suit.color = value.Suit is Poker.Foundation.Suit.Hearts or Poker.Foundation.Suit.Diamonds ? PaperRed : PaperInk;
            }
        }

        private sealed class RequestRow
        {
            internal readonly RectTransform frame;
            private readonly TextMeshProUGUI source, message, interpretation;
            internal RequestRow(RectTransform parent, TMP_FontAsset font)
            {
                frame = TopRect("Selected Request", parent, 0, 66);
                Play2UGui.Image(frame, Play2UGui.Hex("FFFFFF09"));
                source = Text("Player and Card", 155, 19, 13, Play2UGui.Gold);
                message = Text("Message", 550, 24, 16, Play2UGui.Cream);
                interpretation = Text("Interpretation", 550, 20, 13, Play2UGui.Hex("BAC5C4"));
                source.rectTransform.anchoredPosition = new Vector2(10, -12);
                message.rectTransform.anchoredPosition = new Vector2(180, -8);
                interpretation.rectTransform.anchoredPosition = new Vector2(180, -36);
                message.textWrappingMode = TextWrappingModes.Normal;
                interpretation.textWrappingMode = TextWrappingModes.Normal;
                TextMeshProUGUI Text(string label, float width, float height, int size, Color color)
                {
                    var text = Play2UGui.Text(label, frame, "", Vector2.zero, new Vector2(width, height), size, color, font, TextAlignmentOptions.TopLeft);
                    text.richText = false;
                    text.rectTransform.anchorMin = text.rectTransform.anchorMax = text.rectTransform.pivot = Vector2.up;
                    return text;
                }
            }
            internal float Present(HoldemDealerSelectionPacket item, string name, float y)
            {
                frame.anchoredPosition = new Vector2(0, -y);
                source.text = name + (item.hasAppliedCard ? "\n공용 " + (item.boardIndex + 1) + "번  "
                    + Play2HandGuide.CardText(Poker.Foundation.Card.FromId(item.card)) : "");
                source.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 46);
                message.text = item.text;
                interpretation.text = Brief(item.interpretation, 120);
                float textHeight = Mathf.Max(24, message.GetPreferredValues(message.text, 550, float.PositiveInfinity).y);
                message.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, textHeight);
                interpretation.rectTransform.anchoredPosition = new Vector2(180, -12 - textHeight);
                float meaningHeight = Mathf.Max(20, interpretation.GetPreferredValues(interpretation.text, 550, float.PositiveInfinity).y);
                interpretation.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, meaningHeight);
                float height = Mathf.Max(66, 22 + textHeight + meaningHeight);
                frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                return height + 6;
            }
        }
    }
}
