using System;
using System.Linq;
using Poker.Application;
using Poker.Foundation;
using Poker.Transport;
using UnityEngine;
using UnityEngine.UIElements;

internal sealed class Play2TableHud
{
    private readonly Play2TablePreview table;
    private readonly VisualElement root, seats, sizing, accusation;
    private readonly Label street, pot, turn, notice, target, feedback;
    private readonly Button fold, call, raise, accuse, next, allIn;
    private readonly SliderInt slider;
    private readonly DropdownField suspect;
    private HoldemRoomPacket packet;
    private string lastStatus, noticeKey;
    private readonly System.Collections.Generic.HashSet<string> shownNotices = new System.Collections.Generic.HashSet<string>();
    private bool wasDealing;
    private int[] suspects = Array.Empty<int>();

    public Play2TableHud(VisualElement root, Play2TablePreview table)
    {
        this.root = root;
        this.table = table;
        T Find<T>(string name) where T : VisualElement => root.Q<T>("table-" + name);
        street = Find<Label>("street"); pot = Find<Label>("pot"); turn = Find<Label>("turn");
        notice = Find<Label>("notice"); target = Find<Label>("target");
        feedback = Find<Label>("feedback");
        seats = Find<VisualElement>("seats"); sizing = Find<VisualElement>("sizing"); accusation = Find<VisualElement>("accusation");
        fold = Find<Button>("fold"); call = Find<Button>("call"); raise = Find<Button>("raise"); accuse = Find<Button>("accuse");
        next = Find<Button>("next"); allIn = Find<Button>("all");
        slider = Find<SliderInt>("slider"); suspect = Find<DropdownField>("suspect");
        Find<Button>("leave").clicked += table.Leave;
        fold.clicked += () => Act(BettingActionKind.Fold);
        call.clicked += () => Act(packet.game.legal.check ? BettingActionKind.Check : BettingActionKind.Call);
        raise.clicked += () =>
        {
            Show(accusation, false);
            Show(sizing, sizing.ClassListContains("hidden"));
        };
        accuse.clicked += () =>
        {
            Show(sizing, false);
            Show(accusation, accusation.ClassListContains("hidden"));
        };
        Find<Button>("commit").clicked += () => RaiseTo(slider.value);
        Find<Button>("min").clicked += () => slider.value = slider.lowValue;
        Find<Button>("half").clicked += () =>
        {
            long current = packet.game.seats.Max(s => s.streetContribution);
            slider.value = Mathf.Clamp((int)(current + packet.game.pot / 2), slider.lowValue, slider.highValue);
        };
        allIn.clicked += () => RaiseTo(packet.game.legal.allInTarget);
        slider.RegisterValueChangedCallback(e => target.text = $"이번 베팅 총 {e.newValue} 칩");
        Find<Button>("accuse-confirm").clicked += () =>
        {
            if (suspect.index < 0 || suspect.index >= suspects.Length) return;
            var command = Command("turn-accusation");
            command.accusationTarget = suspects[suspect.index];
            if (table.Submit(command)) Show(accusation, false);
        };
        next.clicked += () =>
        {
            if (packet.game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority)
            {
                var command = Command("settle");
                command.oddChipRule = (int)HoldemOddChipRule.ClockwiseFromButton;
                table.Submit(command);
            }
            else table.StartHand();
        };
    }

    public void Present(HoldemRoomPacket value, string status)
    {
        if (value?.hasGame != true)
        {
            if (packet == null) return;
            packet = null;
            noticeKey = null;
            shownNotices.Clear();
            Show(notice, false);
            Show(root, false);
            return;
        }
        bool dealing = table.IsDealing;
        if (ReferenceEquals(packet, value) && lastStatus == status && wasDealing == dealing) return;
        var previous = packet?.game;
        packet = value;
        lastStatus = status;
        bool dealFinished = wasDealing && !dealing;
        wasDealing = dealing;
        Show(root, true);
        var game = packet.game;
        if (previous?.handId != game.handId) shownNotices.Clear();
        street.text = game.hasResult ? "결과" : game.board.Length == 0 ? "카드 공개 전" : $"공용 카드 {game.board.Length}/5장 공개";
        pot.text = $"팟  {game.pot}";
        seats.Clear();
        foreach (var seat in game.seats)
        {
            string label = seat.status == (int)HoldemSeatStatus.Folded ? " · 폴드"
                : seat.status == (int)HoldemSeatStatus.AllIn ? " · 올인"
                : seat.status == (int)HoldemSeatStatus.Busted ? " · 탈락" : "";
            var plate = new Label($"{Name(seat.seat)}  {seat.stack}칩{label}") { enableRichText = false };
            plate.AddToClassList("table-seat");
            plate.EnableInClassList("current", seat.currentActor);
            seats.Add(plate);
        }
        bool acting = game.hasLegal && game.currentSeat == packet.viewerSeat && !packet.paused
            && !game.hasResult && !game.dealPending && !game.revealPending && !dealing;
        var legal = game.legal;
        fold.SetEnabled(acting && legal.fold);
        call.SetEnabled(acting && (legal.check || legal.call));
        call.text = acting && legal.call ? $"콜 {legal.callAmount}" : "체크";
        raise.SetEnabled(acting && (legal.bet || legal.raise || legal.hasAllInTarget));
        raise.text = acting && legal.bet ? "베팅" : "레이즈";
        if (acting && (legal.bet || legal.raise || legal.hasAllInTarget)
            && (previous == null || previous.version != game.version || dealFinished || slider.highValue != legal.maximumTarget))
        {
            int min = (int)(legal.minimumTarget > 0 ? legal.minimumTarget : legal.allInTarget);
            slider.lowValue = min;
            slider.highValue = (int)Math.Max(min, legal.maximumTarget);
            slider.SetValueWithoutNotify(min);
            target.text = $"이번 베팅 총 {min} 칩";
        }
        allIn.SetEnabled(acting && legal.hasAllInTarget);
        accuse.SetEnabled(acting && game.canAccuseOnTurn);
        if (!acting || previous?.version != game.version)
        {
            Show(sizing, false);
            Show(accusation, false);
        }
        if (acting && game.canAccuseOnTurn && (previous?.version != game.version || suspects.Length == 0))
        {
            suspects = game.seats.Where(s => s.dealtIn && s.seat != packet.viewerSeat).Select(s => s.seat).ToArray();
            suspect.choices = suspects.Select(s => $"{s}번 · {Name(s)}").ToList();
            suspect.index = suspects.Length == 0 ? -1 : 0;
        }
        bool settlement = game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority;
        Show(next, table.IsHost && !packet.paused && (settlement || (game.hasResult && game.canContinue)));
        next.text = settlement ? "정산하기" : "다음 판";
        feedback.text = status;
        Show(feedback, !string.IsNullOrEmpty(status));
        turn.text = packet.paused ? "연결 대기 중"
            : settlement ? "정산 대기" : game.hasResult ? (game.isOver ? "경기 종료" : "이번 판 종료")
            : game.dealPending ? "딜러가 다음 카드를 준비 중입니다."
            : game.revealPending || dealing ? "카드 공개 중"
            : acting ? "내 차례" : $"{Name(game.currentSeat)} 차례";

        if (packet.hasTurnAccusation)
        {
            var result = packet.turnAccusation;
            ShowNotice($"a:{result.handId}:{result.street}:{result.accuser}:{result.target}",
                $"{Name(result.accuser)} → {Name(result.target)}  ·  {(result.caught ? "고발 적중" : "고발 실패")}");
        }
        if (game.hasResult && (game.result.kind != (int)HoldemResultKind.Accusation || !packet.hasTurnAccusation))
        {
            string winners = string.Join(", ", game.seats.Where(s => s.awarded > 0).Select(s => $"{Name(s.seat)} +{s.awarded}"));
            ShowNotice("result:" + game.handId, winners.Length > 0 ? "정산  ·  " + winners : "이번 판 종료");
        }
        else if (!game.hasResult && packet.hasOwnCardChange)
            ShowNotice($"dealer:{game.handId}:{packet.ownCardChange.street}", "딜러가 내 요청을 반영했습니다.");
    }

    private void ShowNotice(string key, string message)
    {
        if (!shownNotices.Add(key)) return;
        noticeKey = key;
        notice.text = message;
        Show(notice, true);
        Play2FrontEnd.Animate(notice, 0f, 10f, 0f);
        notice.schedule.Execute(() => { if (noticeKey == key) Show(notice, false); }).StartingIn(2600);
    }

    private void RaiseTo(long amount) => Act(packet.game.legal.bet || (!packet.game.legal.raise
        && packet.game.seats.All(s => s.streetContribution == 0))
        ? BettingActionKind.BetTo : BettingActionKind.RaiseTo, amount);

    private void Act(BettingActionKind kind, long amount = 0)
    {
        var request = Command("act");
        request.action = (int)kind;
        request.target = amount;
        if (table.Submit(request)) Show(sizing, false);
    }

    private HoldemWireRequest Command(string type) => new HoldemWireRequest
    { type = type, handId = packet.game.handId, version = packet.game.version, street = packet.game.street };

    private string Name(int seat) => packet.members.FirstOrDefault(member => member.seat == seat)?.name ?? $"{seat}번";
    private static void Show(VisualElement element, bool visible) => element.EnableInClassList("hidden", !visible);
}
