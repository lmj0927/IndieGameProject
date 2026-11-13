using System;
using Poker.Foundation;

namespace Poker.Application
{
    public enum SequentialConversationClockOrigin { Unset = 0, StreetEntry = 1, AfterBetting = 2, ActionTurn = 3 }

    /// <summary>Immutable public host pacing. Null means manual progression, not default times.</summary>
    public sealed class SequentialPacingProfile
    {
        public SequentialPacingProfile(int preflopMilliseconds, int flopMilliseconds, int turnMilliseconds,
            int riverMilliseconds, int revealMilliseconds, SequentialConversationClockOrigin origin,
            bool neutralDealerPreview, int actionTurnMilliseconds = 0)
        {
            foreach (int value in new[] { preflopMilliseconds, flopMilliseconds, turnMilliseconds,
                riverMilliseconds, revealMilliseconds, actionTurnMilliseconds })
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(preflopMilliseconds));
            if (origin != SequentialConversationClockOrigin.StreetEntry && origin != SequentialConversationClockOrigin.AfterBetting
                && origin != SequentialConversationClockOrigin.ActionTurn)
                throw new ArgumentOutOfRangeException(nameof(origin));
            if (origin == SequentialConversationClockOrigin.ActionTurn
                ? actionTurnMilliseconds == 0 || preflopMilliseconds != 0 || flopMilliseconds != 0
                    || turnMilliseconds != 0 || riverMilliseconds != 0
                : actionTurnMilliseconds != 0)
                throw new ArgumentException("Choose either street conversation floors or a positive action-turn limit.");
            PreflopMilliseconds = preflopMilliseconds; FlopMilliseconds = flopMilliseconds;
            TurnMilliseconds = turnMilliseconds; RiverMilliseconds = riverMilliseconds;
            RevealMilliseconds = revealMilliseconds; Origin = origin; NeutralDealerPreview = neutralDealerPreview;
            ActionTurnMilliseconds = actionTurnMilliseconds;
        }
        public int PreflopMilliseconds { get; }
        public int FlopMilliseconds { get; }
        public int TurnMilliseconds { get; }
        public int RiverMilliseconds { get; }
        public int RevealMilliseconds { get; }
        public SequentialConversationClockOrigin Origin { get; }
        public bool NeutralDealerPreview { get; }
        public int ActionTurnMilliseconds { get; }
        public int ConversationMillisecondsFor(HoldemStreet street) => street == HoldemStreet.Preflop ? PreflopMilliseconds
            : street == HoldemStreet.Flop ? FlopMilliseconds : street == HoldemStreet.Turn ? TurnMilliseconds
            : street == HoldemStreet.River ? RiverMilliseconds : throw new ArgumentOutOfRangeException(nameof(street));
        public bool Matches(SequentialPacingProfile other) => other != null
            && PreflopMilliseconds == other.PreflopMilliseconds && FlopMilliseconds == other.FlopMilliseconds
            && TurnMilliseconds == other.TurnMilliseconds && RiverMilliseconds == other.RiverMilliseconds
            && RevealMilliseconds == other.RevealMilliseconds && Origin == other.Origin
            && NeutralDealerPreview == other.NeutralDealerPreview && ActionTurnMilliseconds == other.ActionTurnMilliseconds;
    }

    /// <summary>Public host-clock sample, bound to one input window. Display only; never advances rules.</summary>
    public sealed class HoldemProgressClock
    {
        public const int Requests = 1, Accusations = 2, Proposal = 5, Response = 6;
        public const int SequentialPreflop = 8, SequentialRiver = 11;
        public HoldemProgressClock(Guid handId, Guid windowId, int stage, int remainingMilliseconds,
            SeatId? turnSeat = null, long turnHandVersion = 0)
        {
            if (handId == Guid.Empty || windowId == Guid.Empty) throw new ArgumentException("Clock window IDs are required.");
            if (stage != Requests && stage != Accusations && stage != Proposal && stage != Response
                && (stage < SequentialPreflop || stage > SequentialRiver))
                throw new ArgumentOutOfRangeException(nameof(stage));
            if (remainingMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(remainingMilliseconds));
            if (turnSeat.HasValue ? !turnSeat.Value.IsValid || turnHandVersion < 0 || stage < SequentialPreflop
                : turnHandVersion != 0)
                throw new ArgumentException("Action clocks require a valid actor and hand revision.");
            HandId = handId; WindowId = windowId; Stage = stage; RemainingMilliseconds = remainingMilliseconds;
            TurnSeat = turnSeat; TurnHandVersion = turnHandVersion;
        }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public int Stage { get; }
        public int RemainingMilliseconds { get; }
        public SeatId? TurnSeat { get; }
        public long TurnHandVersion { get; }
        public bool Matches(HoldemSnapshot game) => game != null && game.HandId == HandId && game.Result == null
            && (game.BettingPolicy == HoldemBettingPolicy.SharedAnteSequential
                ? WindowId == HandId && Stage == SequentialPreflop + (int)game.Street
                    && game.Street <= HoldemStreet.River && !game.IsRevealPending && !game.IsSettlementPending
                    && (!TurnSeat.HasValue || game.CurrentSeat == TurnSeat && game.HandVersion == TurnHandVersion)
                : game.BettingPolicy == HoldemBettingPolicy.SharedAnteFixed
                    && Stage < SequentialPreflop && (Stage == Requests ? game.PendingDeal?.WindowId == WindowId
                : Stage == Accusations ? game.Accusations?.WindowId == WindowId
                    && game.Accusations.Phase == HoldemAccusationPhase.Collecting
                : game.FixedBetting?.WindowId == WindowId && game.FixedBetting.Phase == (Stage == Proposal
                    ? FixedBettingPhase.Proposal : FixedBettingPhase.Response)));
        public bool SameSample(HoldemProgressClock other) => other != null && HandId == other.HandId
            && WindowId == other.WindowId && Stage == other.Stage && RemainingMilliseconds == other.RemainingMilliseconds
            && TurnSeat == other.TurnSeat && TurnHandVersion == other.TurnHandVersion;
        public bool FitsSequentialProfile(SequentialPacingProfile profile, bool bettingOpen = false) => profile != null
            && (profile.Origin != SequentialConversationClockOrigin.AfterBetting || !bettingOpen)
            && Stage >= SequentialPreflop && Stage <= SequentialRiver
            && (profile.Origin == SequentialConversationClockOrigin.ActionTurn
                ? TurnSeat.HasValue && bettingOpen && RemainingMilliseconds <= profile.ActionTurnMilliseconds
                : !TurnSeat.HasValue && RemainingMilliseconds <= profile.ConversationMillisecondsFor((HoldemStreet)(Stage - SequentialPreflop)));
    }

    public enum HoldemRoomError
    {
        None, UnknownConnection, Disconnected, Full, AlreadyStarted, HostOnly,
        WaitingForPlayers, PlayersNotReady, Paused, InvalidResumeToken,
        ConnectionInUse, MemberStillConnected, CoreRejected, HostCannotLeave, UtteranceRejected,
        MatchNotOver, StaleRematchConsent, ClientUpgradeRequired, CommandConflict, AccusationsDisabled, ChatRejected
    }

    /// <summary>Deliver only to the admitted connection. ResumeToken is a bearer secret, not public room state.</summary>
    public sealed class HoldemRoomAdmission
    {
        internal HoldemRoomAdmission(SeatId seat, string token) { Seat = seat; ResumeToken = token; }
        public SeatId Seat { get; }
        public string ResumeToken { get; }
    }

    public sealed class HoldemRoomJoinResult
    {
        internal HoldemRoomJoinResult(HoldemRoomError error, HoldemRoomAdmission admission = null)
        { Error = error; Admission = admission; }
        public HoldemRoomError Error { get; }
        public HoldemRoomAdmission Admission { get; }
        public bool Accepted => Error == HoldemRoomError.None;
    }

    public sealed class HoldemRoomReceipt
    {
        internal HoldemRoomReceipt(HoldemRoomError error, HoldemReceipt poker = null)
        { Error = error; Poker = poker; }
        public HoldemRoomError Error { get; }
        public HoldemReceipt Poker { get; }
        public bool Accepted => Error == HoldemRoomError.None;
    }

    /// <summary>No claimed seat: the server resolves the transport connection before constructing a core command.</summary>
    public sealed class HoldemRoomAction
    {
        public HoldemRoomAction(Guid sessionId, Guid handId, Guid commandId, long expectedVersion, BettingAction action)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("Session, hand and command IDs are required.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            SessionId = sessionId; HandId = handId; CommandId = commandId;
            ExpectedVersion = expectedVersion; Action = action ?? throw new ArgumentNullException(nameof(action));
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public long ExpectedVersion { get; }
        public BettingAction Action { get; }
    }

    public sealed class HoldemRoomMemberView
    {
        internal HoldemRoomMemberView(SeatId seat, string name, bool connected, bool ready, bool host,
            bool rematchReady = false, long rematchRevision = 0)
        { Seat = seat; Name = name; Connected = connected; Ready = ready; IsHost = host;
            RematchReady = rematchReady; RematchRevision = rematchRevision; }
        public SeatId Seat { get; }
        public string Name { get; }
        public bool Connected { get; }
        public bool Ready { get; }
        public bool IsHost { get; }
        public bool RematchReady { get; }
        public long RematchRevision { get; }
    }

    /// <summary>Public, immutable setup for this room. No dealer state or admission data.</summary>
    public sealed class HoldemRoomRules
    {
        private readonly long[] anteSteps;
        public HoldemRoomRules(HoldemConfig config, bool receivesUtterances, int seatCapacity = HoldemRoom.Capacity,
            bool publishesUtterances = false, SequentialPacingProfile sequentialPacing = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (sequentialPacing != null && config.BettingPolicy != HoldemBettingPolicy.SharedAnteSequential)
                throw new ArgumentException("Sequential pacing requires sequential rules.", nameof(sequentialPacing));
            ValidateSeatCapacity(seatCapacity);
            if (publishesUtterances && !receivesUtterances)
                throw new ArgumentException("Public speech requires enabled intake.", nameof(publishesUtterances));
            if (config.StartingStack > long.MaxValue / seatCapacity)
                throw new ArgumentOutOfRangeException(nameof(config), "Starting stacks must fit the chip ledger.");
            SeatCapacity = seatCapacity;
            StartingStack = config.StartingStack; SmallBlind = config.SmallBlind; BigBlind = config.BigBlind;
            BettingPolicy = config.BettingPolicy; Ante = config.Ante; FixedBetIncrement = config.FixedBetIncrement;
            SequentialRules = config.SequentialRules;
            SequentialPacing = sequentialPacing;
            anteSteps = config.CopyAnteSteps(); IncrementFollowsAnte = config.IncrementFollowsAnte;
            WaitsForHostDeal = config.DealPolicy == HoldemDealPolicy.WaitForHost;
            PausesAfterReveal = config.RevealPolicy == HoldemRevealPolicy.PauseAfterCommunityReveal;
            ReceivesUtterances = receivesUtterances;
            PublishesUtterances = publishesUtterances;
            AccusationsEnabled = config.AccusationMode != HoldemAccusationMode.Disabled;
            AccusationMode = config.AccusationMode;
        }
        public long StartingStack { get; }
        public int SeatCapacity { get; }
        public static void ValidateSeatCapacity(int value)
        {
            if (value < 2 || value > HoldemRoom.Capacity) throw new ArgumentOutOfRangeException(nameof(value));
        }
        public long SmallBlind { get; }
        public long BigBlind { get; }
        public HoldemBettingPolicy BettingPolicy { get; }
        public long Ante { get; }
        public long FixedBetIncrement { get; }
        public SequentialBettingRules SequentialRules { get; }
        public SequentialPacingProfile SequentialPacing { get; }
        public long[] CopyAnteSteps() => (long[])anteSteps.Clone();
        public bool IncrementFollowsAnte { get; }
        public bool WaitsForHostDeal { get; }
        public bool PausesAfterReveal { get; }
        public bool ReceivesUtterances { get; }
        public bool PublishesUtterances { get; }
        public bool AccusationsEnabled { get; }
        public HoldemAccusationMode AccusationMode { get; }
        public bool Matches(HoldemRoomRules other) => other != null && SeatCapacity == other.SeatCapacity && StartingStack == other.StartingStack
            && SmallBlind == other.SmallBlind && BigBlind == other.BigBlind
            && BettingPolicy == other.BettingPolicy && Ante == other.Ante && FixedBetIncrement == other.FixedBetIncrement
            && WaitsForHostDeal == other.WaitsForHostDeal && PausesAfterReveal == other.PausesAfterReveal
            && ReceivesUtterances == other.ReceivesUtterances && PublishesUtterances == other.PublishesUtterances
            && AccusationsEnabled == other.AccusationsEnabled && AccusationMode == other.AccusationMode
            && IncrementFollowsAnte == other.IncrementFollowsAnte && SameAnteSteps(other) && SameSequentialRules(other)
            && (SequentialPacing == null ? other.SequentialPacing == null : SequentialPacing.Matches(other.SequentialPacing));
        private bool SameSequentialRules(HoldemRoomRules other)
        {
            var a = SequentialRules; var b = other.SequentialRules;
            return a == null ? b == null : b != null && a.MinimumBet == b.MinimumBet
                && a.MaximumOpeningBet == b.MaximumOpeningBet && a.AllowCheck == b.AllowCheck
                && a.FirstSeat == b.FirstSeat && a.AllInCap == b.AllInCap
                && a.AnteShortfall == b.AnteShortfall && a.FineShortfall == b.FineShortfall
                && a.AccusationScope == b.AccusationScope && a.BaitFineLimit == b.BaitFineLimit;
        }
        private bool SameAnteSteps(HoldemRoomRules other)
        {
            if (anteSteps.Length != other.anteSteps.Length) return false;
            for (int i = 0; i < anteSteps.Length; i++) if (anteSteps[i] != other.anteSteps[i]) return false;
            return true;
        }
    }

    /// <summary>One connection's projection. Never broadcast this object to other connections.</summary>
    public sealed class HoldemRoomView
    {
        private readonly HoldemRoomMemberView[] members;
        private readonly HoldemActionNotice[] streetActions;
        private readonly HoldemOwnCardChange[] ownHandCardChanges;
        private readonly HoldemDealerSelectionFeedback[] dealerSelections;
        internal HoldemRoomView(Guid sessionId, long revision, SeatId viewer, bool paused,
            HoldemRoomMemberView[] ownedMembers, HoldemSnapshot game, HoldemActionNotice lastAction,
            HoldemActionNotice[] ownedStreetActions, HoldemUtteranceView ownUtterances = null, HoldemRoomRules rules = null,
            HoldemHandHistory history = null, HoldemPublicUtterances publicUtterances = null,
            long matchNumber = 1, bool rematchSupported = false, HoldemOwnCardChange ownCardChange = null,
            System.Collections.Generic.IReadOnlyList<HoldemOwnCardChange> ownHandCardChanges = null,
            HoldemProgressClock progressClock = null, HoldemEliminatedSpeechView eliminatedSpeech = null,
            HoldemChatLog chat = null, HoldemTurnAccusationNotice turnAccusation = null,
            HoldemDealerSelectionFeedback[] dealerSelections = null)
        { SessionId = sessionId; Revision = revision; ViewerSeat = viewer; Paused = paused;
            members = ownedMembers; Game = game; LastAction = lastAction; streetActions = ownedStreetActions;
            OwnUtterances = ownUtterances; Rules = rules; History = history; PublicUtterances = publicUtterances;
            MatchNumber = matchNumber; RematchSupported = rematchSupported; OwnCardChange = ownCardChange; ProgressClock = progressClock;
            EliminatedSpeech = eliminatedSpeech; Chat = chat; TurnAccusation = turnAccusation;
            this.dealerSelections = dealerSelections == null ? Array.Empty<HoldemDealerSelectionFeedback>()
                : (HoldemDealerSelectionFeedback[])dealerSelections.Clone();
            this.ownHandCardChanges = new HoldemOwnCardChange[ownHandCardChanges?.Count ?? 0];
            for (int i = 0; i < this.ownHandCardChanges.Length; i++) this.ownHandCardChanges[i] = ownHandCardChanges[i]; }
        public Guid SessionId { get; }
        public long Revision { get; }
        public SeatId ViewerSeat { get; }
        public bool Paused { get; }
        public HoldemSnapshot Game { get; }
        public HoldemActionNotice LastAction { get; }
        public HoldemUtteranceView OwnUtterances { get; }
        public HoldemPublicUtterances PublicUtterances { get; }
        public HoldemEliminatedSpeechView EliminatedSpeech { get; }
        public HoldemChatLog Chat { get; }
        public HoldemTurnAccusationNotice TurnAccusation { get; }
        public int DealerSelectionCount => dealerSelections.Length;
        public HoldemDealerSelectionFeedback GetDealerSelection(int index) => dealerSelections[index];
        public HoldemOwnCardChange OwnCardChange { get; }
        public int OwnHandCardChangeCount => ownHandCardChanges.Length;
        public HoldemOwnCardChange GetOwnHandCardChange(int index) => ownHandCardChanges[index];
        public HoldemRoomRules Rules { get; }
        public HoldemProgressClock ProgressClock { get; }
        public HoldemHandHistory History { get; }
        public long MatchNumber { get; }
        public bool RematchSupported { get; }
        // At most one last public betting action per seat, only for the current street.
        public int StreetActionCount => streetActions.Length;
        public HoldemActionNotice GetStreetAction(int index) => streetActions[index];
        public int MemberCount => members.Length;
        public HoldemRoomMemberView GetMember(int index)
        {
            if (index < 0 || index >= members.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return members[index];
        }
    }
}
