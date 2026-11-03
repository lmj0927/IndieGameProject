using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public enum HoldemCommandError
    {
        None = 0,
        WrongSession,
        WrongHand,
        UnauthorizedSeat,
        NotStarted,
        Busy,
        VersionMismatch,
        WrongTurn,
        IllegalAction,
        HandComplete,
        CannotContinue,
        AlreadyStarted,
        HandIdReused,
        CommandConflict,
        SettlementRuleRequired,
        SettlementNotPending,
        InvalidSettlementRule,
        RevealPending,
        RevealNotPending,
        WrongRevealWindow,
        AccusationNotEnabled,
        AccusationWindowClosed,
        AccusationWindowNotClosed,
        AccusationResponsesPending,
        InvalidAccusationTarget,
        UnknownAccusation,
        AccusationVerdictRecorded,
        AccusationVerdictsPending,
        AccusationConsequencesPending,
        DealPending,
        DealNotPending,
        WrongDealWindow,
        MatchNotOver,
        InvalidCardChange,
        AccusationNotAllowed,
        FixedBettingNotEnabled,
        BettingWindowClosed,
        WrongBettingWindow,
        StaleChoice,
        DealerEvidenceUnavailable,
        InvalidAccusationEvidence,
        AccusationTurnUnavailable,
        AccusationSettlementRuleRequired,
        AccusationFineUnavailable,
        FinalConversationPending,
        FinalConversationNotPending
    }

    public sealed class HoldemStartCommand
    {
        public HoldemStartCommand(Guid sessionId, Guid handId, Guid commandId, long expectedVersion)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            SessionId = sessionId;
            HandId = handId;
            CommandId = commandId;
            ExpectedVersion = expectedVersion;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        /// <summary>The authority session version, not the per-hand version.</summary>
        public long ExpectedVersion { get; }

        internal bool HasSamePayload(HoldemStartCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId
            && CommandId == other.CommandId && ExpectedVersion == other.ExpectedVersion;
    }

    public sealed class HoldemCommand
    {
        private HoldemCommand(Guid sessionId, Guid handId, Guid commandId, SeatId seat,
            long expectedVersion, BettingAction action)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            Action = action ?? throw new ArgumentNullException(nameof(action));
            SessionId = sessionId;
            HandId = handId;
            CommandId = commandId;
            Seat = seat;
            ExpectedVersion = expectedVersion;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId Seat { get; }
        /// <summary>The authority session version, not the per-hand version.</summary>
        public long ExpectedVersion { get; }
        public BettingAction Action { get; }

        public static HoldemCommand Act(Guid sessionId, Guid handId, Guid commandId,
            SeatId seat, long expectedVersion, BettingAction action)
            => new HoldemCommand(sessionId, handId, commandId, seat, expectedVersion, action);

        internal bool HasSamePayload(HoldemCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && CommandId == other.CommandId
            && Seat == other.Seat && ExpectedVersion == other.ExpectedVersion
            && Action.Kind == other.Action.Kind && Action.Target == other.Action.Target;
    }

    public sealed class HoldemReceipt
    {
        internal HoldemReceipt(Guid sessionId, Guid handId, Guid commandId, SeatId? seat,
            long? version, HoldemCommandError error)
        {
            SessionId = sessionId;
            HandId = handId;
            CommandId = commandId;
            Seat = seat;
            Version = version;
            Error = error;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId? Seat { get; }
        public long? Version { get; }
        public HoldemCommandError Error { get; }
        public bool Accepted => Error == HoldemCommandError.None;
    }

    /// <summary>Single-authority two-to-four-seat session with a persistent clockwise roster.</summary>
    public sealed partial class HoldemSession
    {
        private sealed class AcceptedCommand
        {
            public AcceptedCommand(HoldemCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private sealed class AcceptedReveal
        {
            public AcceptedReveal(HoldemRevealCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemRevealCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private sealed class AcceptedFinalConversation
        {
            public AcceptedFinalConversation(FinalConversationCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public FinalConversationCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private sealed class FixedBettingWindowState
        {
            public FixedBettingWindowState(HoldemHand hand, long openedVersion)
            {
                Id = Guid.NewGuid(); HandId = hand.HandId; Street = hand.Street;
                Phase = hand.CurrentBetting.FixedPhase.Value; OpenedVersion = openedVersion;
            }
            public Guid Id { get; }
            public Guid HandId { get; }
            public HoldemStreet Street { get; }
            public FixedBettingPhase Phase { get; }
            public long OpenedVersion { get; }
        }

        private sealed class AcceptedFixedBetting
        {
            public AcceptedFixedBetting(HoldemWindowCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemWindowCommand Command { get; }
            public HoldemReceipt Receipt { get; }
            public bool Matches(HoldemWindowCommand other) =>
                Command is FixedBettingChoiceCommand choice && other is FixedBettingChoiceCommand otherChoice
                    ? choice.HasSamePayload(otherChoice)
                    : Command is FixedBettingHostCommand host && other is FixedBettingHostCommand otherHost
                        && host.HasSamePayload(otherHost);
        }

        private sealed class SettlementCommand
        {
            public SettlementCommand(Guid commandId, Guid handId, long expectedVersion,
                HoldemOddChipRule rule)
            { CommandId = commandId; HandId = handId; ExpectedVersion = expectedVersion; Rule = rule; }
            public Guid CommandId { get; }
            public Guid HandId { get; }
            public long ExpectedVersion { get; }
            public HoldemOddChipRule Rule { get; }
            public bool HasSamePayload(SettlementCommand other) => other != null
                && CommandId == other.CommandId && HandId == other.HandId
                && ExpectedVersion == other.ExpectedVersion && Rule == other.Rule;
        }

        private readonly HoldemConfig config;
        private readonly IRandomSource random;
        private readonly SeatId[] roster;
        private readonly SeatId initialButton;
        private readonly HoldemOddChipRule oddChipRule;
        private readonly ChipLedger initialLedger;
        private readonly Dictionary<Guid, AcceptedCommand> accepted = new Dictionary<Guid, AcceptedCommand>();
        private readonly Dictionary<Guid, AcceptedReveal> acceptedReveals = new Dictionary<Guid, AcceptedReveal>();
        private readonly Dictionary<Guid, AcceptedFinalConversation> acceptedFinalConversations = new Dictionary<Guid, AcceptedFinalConversation>();
        private readonly HashSet<Guid> usedCommandIds = new HashSet<Guid>();
        private HashSet<Guid> startedHandIds = new HashSet<Guid>();
        private ChipLedger settledLedger;
        private HoldemHand currentHand;
        private long roundInMatch;
        private HoldemStartCommand currentStartCommand;
        private HoldemReceipt currentStartReceipt;
        private Guid currentStartBasisHandId;
        private SettlementCommand currentSettlementCommand;
        private HoldemReceipt currentSettlementReceipt;
        private bool processing;
        private FixedBettingWindowState fixedBettingWindow;
        private readonly Dictionary<SeatId, long> fixedChoiceRevisions = new Dictionary<SeatId, long>();
        private readonly Dictionary<Guid, AcceptedFixedBetting> acceptedFixedBetting = new Dictionary<Guid, AcceptedFixedBetting>();

        public HoldemSession(Guid sessionId, HoldemConfig config, SeatId humanSeat,
            SeatId opponentSeat, IRandomSource random)
            : this(sessionId, config, new[] { humanSeat, opponentSeat }, humanSeat,
                random, HoldemOddChipRule.RequireExplicitPriority) { }

        public HoldemSession(Guid sessionId, HoldemConfig config,
            IReadOnlyList<SeatId> seatsInTableOrder, SeatId initialButton,
            IRandomSource random,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority)
            : this(sessionId, config, CreateEqualLedger(config, seatsInTableOrder),
                seatsInTableOrder, initialButton, random, oddChipRule) { }

        public HoldemSession(Guid sessionId, HoldemConfig config, ChipLedger startingLedger,
            IReadOnlyList<SeatId> seatsInTableOrder, SeatId initialButton,
            IRandomSource random,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            if (startingLedger == null) throw new ArgumentNullException(nameof(startingLedger));
            ValidateRule(oddChipRule);
            roster = HoldemSeatOrder.CopyTable(seatsInTableOrder);
            if (startingLedger.SeatCount != roster.Length || startingLedger.TotalCommitted != 0)
                throw new ArgumentException("The starting ledger must contain the full uncommitted roster.", nameof(startingLedger));
            for (int i = 0; i < roster.Length; i++) startingLedger.GetChips(roster[i]);
            HoldemSeatOrder.IndexOf(roster, initialButton);
            if (CountFunded(startingLedger, roster) < 2)
                throw new ArgumentException("A session requires at least two funded seats.", nameof(startingLedger));
            SessionId = sessionId;
            this.initialButton = initialButton;
            this.oddChipRule = oddChipRule;
            initialLedger = startingLedger; // ChipLedger is immutable.
            settledLedger = startingLedger;
        }

        public Guid SessionId { get; }
        public HoldemButtonPolicy ButtonPolicy => HoldemButtonPolicy.PokerStarsForwardMoving;
        public HoldemOddChipRule OddChipRule => oddChipRule;
        public int SeatCount => roster.Length;
        public SeatId GetSeatAt(int index)
        {
            if (index < 0 || index >= roster.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return roster[index];
        }
        public SeatId HumanSeat => CompatibilitySeat(0);
        public SeatId OpponentSeat => CompatibilitySeat(1);
        public long Version { get; private set; }
        public long HandNumber { get; private set; }
        /// <summary>Explicit matches within this authority lifetime; hand and command IDs never reset.</summary>
        public long MatchNumber { get; private set; } = 1;
        public Guid? CurrentHandId => currentHand?.HandId;
        public bool HasActiveHand => currentHand != null && !currentHand.IsComplete;
        public int FundedSeatCount => CountFunded(settledLedger, roster);
        /// <summary>Uses the last fully settled balance (or the initial roster), never an in-hand all-in stack.</summary>
        public bool IsEliminatedAfterSettlement(SeatId seat) => settledLedger.GetChips(seat).Stack == 0;
        public bool CanContinue => (currentHand == null || currentHand.IsComplete) && FundedSeatCount >= 2;
        public bool IsOver => currentHand != null && currentHand.IsComplete && FundedSeatCount <= 1;
        public SeatId? SessionWinnerSeat
        {
            get
            {
                if (!IsOver) return null;
                for (int i = 0; i < roster.Length; i++)
                    if (settledLedger.GetChips(roster[i]).Stack > 0) return roster[i];
                return null;
            }
        }
        public SeatId? BustedSeat
        {
            get
            {
                RequireHeadsUp();
                if (!IsOver) return null;
                return settledLedger.GetChips(roster[0]).Stack == 0 ? roster[0] : roster[1];
            }
        }

        public HoldemReceipt StartNextHand(HoldemStartCommand command)
            => StartHand(command, Guid.Empty);

        /// <summary>Only after final settlement. Room adapters must obtain fresh consent from every participant.</summary>
        public HoldemReceipt RestartMatch(HoldemStartCommand command, Guid completedHandId)
        {
            if (completedHandId == Guid.Empty) throw new ArgumentException("A completed hand ID is required.", nameof(completedHandId));
            return StartHand(command, completedHandId);
        }

        private HoldemReceipt StartHand(HoldemStartCommand command, Guid completedHandId)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (currentStartCommand != null && currentStartCommand.CommandId == command.CommandId)
                return currentStartCommand.HasSamePayload(command) && currentStartBasisHandId == completedHandId ? currentStartReceipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (currentSettlementCommand != null && currentSettlementCommand.CommandId == command.CommandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (accepted.ContainsKey(command.CommandId))
                return Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId))
                return Reject(command, HoldemCommandError.CommandConflict);
            if (currentHand != null && currentHand.HandId == command.HandId)
                return Reject(command, HoldemCommandError.AlreadyStarted);
            if (startedHandIds.Contains(command.HandId))
                return Reject(command, HoldemCommandError.HandIdReused);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            bool rematch = completedHandId != Guid.Empty;
            if (rematch && currentHand?.HandId != completedHandId) return Reject(command, HoldemCommandError.WrongHand);
            if (rematch && !IsOver) return Reject(command, HoldemCommandError.MatchNotOver);
            if (currentHand != null && !currentHand.IsComplete)
                return Reject(command, currentHand.IsSettlementPending
                    ? HoldemCommandError.SettlementRuleRequired : HoldemCommandError.AlreadyStarted);
            if (!rematch && !CanContinue) return Reject(command, HoldemCommandError.CannotContinue);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                long nextHandNumber = checked(HandNumber + 1);
                long nextRoundInMatch = rematch ? 1 : checked(roundInMatch + 1);
                long nextMatchNumber = rematch ? checked(MatchNumber + 1) : MatchNumber;
                var nextStartedHandIds = new HashSet<Guid>(startedHandIds);
                if (!nextStartedHandIds.Add(command.HandId))
                    throw new InvalidOperationException("A prevalidated hand ID could not be reserved.");
                var baseLedger = rematch ? initialLedger : settledLedger;
                SeatId button = rematch ? ResolveInitialButton() : ResolveNextButton();
                HoldemHand candidate = HoldemHand.Begin(command.HandId, baseLedger,
                    roster, button, config.ForRound(nextRoundInMatch), random, oddChipRule);
                HoldemAccusationWindow nextWindow = AccusationsFor(candidate);
                HoldemDealWindow nextDeal = DealWindowFor(candidate);
                var nextLedger = candidate.IsComplete ? MergeSettled(candidate, baseLedger) : baseLedger;
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                accusations = nextWindow;
                pendingDeal = nextDeal;
                Version = nextVersion;
                HandNumber = nextHandNumber;
                roundInMatch = nextRoundInMatch;
                MatchNumber = nextMatchNumber;
                settledLedger = nextLedger;
                startedHandIds = nextStartedHandIds;
                currentStartCommand = command;
                currentStartReceipt = receipt;
                currentStartBasisHandId = completedHandId;
                currentSettlementCommand = null;
                currentSettlementReceipt = null;
                accepted.Clear();
                acceptedReveals.Clear();
                acceptedFinalConversations.Clear();
                acceptedDeals.Clear();
                dealRecords = Array.Empty<HoldemDealRecord>();
                acceptedAccusations.Clear();
                accusationOutcomes = Array.Empty<HoldemAccusationOutcome>();
                acceptedFixedBetting.Clear();
                fixedChoiceRevisions.Clear();
                fixedBettingWindow = FixedWindowFor(candidate, nextVersion);
                usedCommandIds.Add(command.CommandId);
                return receipt;
            }
            finally { processing = false; }
        }

        public HoldemReceipt StartNextHand(Guid handId, Guid commandId, long expectedVersion)
            => StartNextHand(new HoldemStartCommand(SessionId, handId, commandId, expectedVersion));

        /// <summary>Seat-authenticated simultaneous input. Other seats' changes do not invalidate this window.</summary>
        public HoldemReceipt SubmitFixedBettingChoice(SeatId authorizedSeat, FixedBettingChoiceCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat)
                return Reject(command, HoldemCommandError.UnauthorizedSeat, command.Seat);
            HoldemReceipt prior = ValidateFixedEnvelope(command, command.Seat);
            if (prior != null) return prior;
            if (command.Phase != fixedBettingWindow.Phase)
                return Reject(command, HoldemCommandError.WrongBettingWindow, command.Seat);
            if (!currentHand.WasDealtIn(command.Seat) || currentHand.IsFolded(command.Seat))
                return Reject(command, HoldemCommandError.UnauthorizedSeat, command.Seat);
            if (command.Phase == FixedBettingPhase.Response && currentHand.CurrentBetting.IsSealedRaiser(command.Seat))
                return Reject(command, HoldemCommandError.IllegalAction, command.Seat);
            fixedChoiceRevisions.TryGetValue(command.Seat, out long previousRevision);
            if (command.ChoiceRevision <= previousRevision)
                return Reject(command, HoldemCommandError.StaleChoice, command.Seat);
            processing = true;
            try
            {
                HoldemHand candidate = command.Phase == FixedBettingPhase.Proposal
                    ? currentHand.SubmitFixedProposal(command.Seat, command.Proposal.Value)
                    : currentHand.SubmitFixedResponse(command.Seat, command.Response.Value);
                HoldemReceipt receipt = CommitFixedBetting(command, candidate, command.Seat);
                fixedChoiceRevisions[command.Seat] = command.ChoiceRevision;
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>Trusted host-only deadline. A stale host version cannot close a newly edited stage.</summary>
        public HoldemReceipt SealFixedBetting(FixedBettingHostCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            HoldemReceipt prior = ValidateFixedEnvelope(command, null);
            if (prior != null) return prior;
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch, null);
            if (command.Phase != fixedBettingWindow.Phase)
                return Reject(command, HoldemCommandError.WrongBettingWindow, null);
            processing = true;
            try { return CommitFixedBetting(command, currentHand.SealFixedBettingStage(), null); }
            finally { processing = false; }
        }

        private HoldemReceipt ValidateFixedEnvelope(HoldemWindowCommand command, SeatId? seat)
        {
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession, seat);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted, seat);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand, seat);
            if (processing) return Reject(command, HoldemCommandError.Busy, seat);
            if (acceptedFixedBetting.TryGetValue(command.CommandId, out AcceptedFixedBetting previous))
                return previous.Matches(command) ? previous.Receipt : Reject(command, HoldemCommandError.CommandConflict, seat);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict, seat);
            if (config.BettingPolicy != HoldemBettingPolicy.SharedAnteFixed)
                return Reject(command, HoldemCommandError.FixedBettingNotEnabled, seat);
            if (fixedBettingWindow == null || currentHand.IsComplete || currentHand.IsDealPending || currentHand.IsRevealPending)
                return Reject(command, HoldemCommandError.BettingWindowClosed, seat);
            if (command.WindowId != fixedBettingWindow.Id || command.Street != fixedBettingWindow.Street)
                return Reject(command, HoldemCommandError.WrongBettingWindow, seat);
            if (command.ExpectedVersion < fixedBettingWindow.OpenedVersion || command.ExpectedVersion > Version)
                return Reject(command, HoldemCommandError.VersionMismatch, seat);
            return null;
        }

        private HoldemReceipt CommitFixedBetting(HoldemWindowCommand command, HoldemHand candidate, SeatId? seat)
        {
            long nextVersion = checked(Version + 1);
            ChipLedger nextLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
            HoldemAccusationWindow nextAccusations = AccusationsFor(candidate);
            HoldemDealWindow nextDeal = DealWindowFor(candidate);
            FixedBettingWindowState nextWindow = FixedWindowFor(candidate, nextVersion);
            var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId, seat,
                nextVersion, HoldemCommandError.None);
            if (nextWindow != fixedBettingWindow) fixedChoiceRevisions.Clear();
            currentHand = candidate; settledLedger = nextLedger; accusations = nextAccusations;
            pendingDeal = nextDeal; fixedBettingWindow = nextWindow; Version = nextVersion;
            acceptedFixedBetting.Add(command.CommandId, new AcceptedFixedBetting(command, receipt));
            usedCommandIds.Add(command.CommandId);
            return receipt;
        }

        private FixedBettingWindowState FixedWindowFor(HoldemHand candidate, long version)
        {
            if (candidate.Config.BettingPolicy != HoldemBettingPolicy.SharedAnteFixed || candidate.IsComplete
                || candidate.IsSettlementPending || candidate.IsDealPending || candidate.IsRevealPending
                || candidate.CurrentBetting.IsComplete) return null;
            FixedBettingPhase phase = candidate.CurrentBetting.FixedPhase.Value;
            return fixedBettingWindow != null && fixedBettingWindow.HandId == candidate.HandId
                && fixedBettingWindow.Street == candidate.Street && fixedBettingWindow.Phase == phase
                ? fixedBettingWindow : new FixedBettingWindowState(candidate, version);
        }

        private FixedBettingView FixedBettingForViewer(SeatId viewer)
        {
            if (fixedBettingWindow == null) return null;
            BettingRound betting = currentHand.CurrentBetting;
            bool eligible = currentHand.WasDealtIn(viewer) && !currentHand.IsFolded(viewer);
            var publicRaisers = new List<SeatId>();
            if (fixedBettingWindow.Phase == FixedBettingPhase.Response)
                for (int i = 0; i < betting.SeatCount; i++)
                    if (betting.IsSealedRaiser(betting.GetSeatAt(i))) publicRaisers.Add(betting.GetSeatAt(i));
            fixedChoiceRevisions.TryGetValue(viewer, out long revision);
            return new FixedBettingView(fixedBettingWindow.Id, fixedBettingWindow.Street, fixedBettingWindow.Phase,
                betting.FixedIncrement, eligible, eligible ? betting.GetOwnFixedProposal(viewer) : null,
                eligible ? betting.GetOwnFixedResponse(viewer) : null, revision, publicRaisers, viewer);
        }

        /// <summary>The adapter supplies authorizedSeat from access control, not request data.</summary>
        public HoldemReceipt Submit(SeatId authorizedSeat, HoldemCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat)
                return Reject(command, HoldemCommandError.UnauthorizedSeat);
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (currentStartReceipt != null && command.CommandId == currentStartReceipt.CommandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (currentSettlementCommand != null && command.CommandId == currentSettlementCommand.CommandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (accepted.TryGetValue(command.CommandId, out AcceptedCommand previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId))
                return Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (currentHand.IsSettlementPending) return Reject(command, HoldemCommandError.SettlementRuleRequired);
            if (currentHand.IsDealPending) return Reject(command, HoldemCommandError.DealPending);
            if (currentHand.IsRevealPending) return Reject(command, HoldemCommandError.RevealPending);
            if (currentHand.IsFinalConversationPending) return Reject(command, HoldemCommandError.FinalConversationPending);
            if (currentHand.IsComplete) return Reject(command, HoldemCommandError.HandComplete);
            if (currentHand.CurrentSeat != command.Seat) return Reject(command, HoldemCommandError.WrongTurn);
            if (!currentHand.CurrentBetting.GetLegalActions().Allows(command.Action))
                return Reject(command, HoldemCommandError.IllegalAction);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.Apply(command.Seat, command.Action);
                HoldemAccusationWindow nextWindow = AccusationsFor(candidate);
                HoldemDealWindow nextDeal = DealWindowFor(candidate);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    command.Seat, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                accusations = nextWindow;
                pendingDeal = nextDeal;
                Version = nextVersion;
                accepted.Add(command.CommandId, new AcceptedCommand(command, receipt));
                usedCommandIds.Add(command.CommandId);
                if (candidate.IsComplete) settledLedger = MergeSettled(candidate);
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>
        /// Trusted host only: the transport must authorize this operation before calling it.
        /// Carries no accusation verdict or payout; normal showdown settlement stays in the hand core.
        /// </summary>
        public HoldemReceipt ResumeAfterReveal(HoldemRevealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (acceptedReveals.TryGetValue(command.CommandId, out AcceptedReveal previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (!currentHand.IsRevealPending) return Reject(command, HoldemCommandError.RevealNotPending);
            if (command.Street != currentHand.Street) return Reject(command, HoldemCommandError.WrongRevealWindow);
            HoldemCommandError accusationError = AccusationResumeError();
            if (accusationError != HoldemCommandError.None) return Reject(command, accusationError);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.ResumeAfterReveal(command.Street);
                ChipLedger candidateLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
                HoldemAccusationWindow nextWindow = AccusationsFor(candidate);
                HoldemDealWindow nextDeal = DealWindowFor(candidate);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                accusations = nextWindow;
                pendingDeal = nextDeal;
                settledLedger = candidateLedger;
                Version = nextVersion;
                fixedBettingWindow = FixedWindowFor(candidate, nextVersion);
                fixedChoiceRevisions.Clear();
                acceptedReveals.Add(command.CommandId, new AcceptedReveal(command, receipt));
                usedCommandIds.Add(command.CommandId);
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>Trusted host only; a client cannot close dialogue or publish showdown hands.</summary>
        public HoldemReceipt CloseFinalConversation(FinalConversationCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (acceptedFinalConversations.TryGetValue(command.CommandId, out var previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (!currentHand.IsFinalConversationPending) return Reject(command, HoldemCommandError.FinalConversationNotPending);
            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.CloseFinalConversation();
                ChipLedger candidateLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate; settledLedger = candidateLedger; Version = nextVersion;
                acceptedFinalConversations.Add(command.CommandId, new AcceptedFinalConversation(command, receipt));
                usedCommandIds.Add(command.CommandId);
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>Trusted host adoption of one explicit rule for the current pending hand.</summary>
        public HoldemReceipt ResolvePendingSettlement(Guid commandId, long expectedSessionVersion,
            HoldemOddChipRule rule)
        {
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (expectedSessionVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedSessionVersion));
            Guid handId = currentHand?.HandId ?? Guid.Empty;
            var command = new SettlementCommand(commandId, handId, expectedSessionVersion, rule);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (currentSettlementCommand != null && currentSettlementCommand.CommandId == commandId)
                return currentSettlementCommand.HasSamePayload(command) ? currentSettlementReceipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (currentStartReceipt != null && currentStartReceipt.CommandId == commandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (accepted.ContainsKey(commandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(commandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (expectedSessionVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (!currentHand.IsSettlementPending) return Reject(command, HoldemCommandError.SettlementNotPending);
            if (rule != HoldemOddChipRule.ClockwiseFromButton)
                return Reject(command, HoldemCommandError.InvalidSettlementRule);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.ResolvePendingSettlement(rule);
                var receipt = new HoldemReceipt(SessionId, currentHand.HandId, commandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                Version = nextVersion;
                currentSettlementCommand = command;
                currentSettlementReceipt = receipt;
                usedCommandIds.Add(commandId);
                settledLedger = MergeSettled(candidate);
                return receipt;
            }
            finally { processing = false; }
        }

        public HoldemSnapshot GetSnapshot(SeatId viewer)
        {
            if (currentHand == null) throw new InvalidOperationException("No Hold'em hand has started.");
            HoldemSeatOrder.IndexOf(roster, viewer);
            SeatId? compatibilityBusted = null;
            if (roster.Length == 2 && IsOver)
                compatibilityBusted = settledLedger.GetChips(roster[0]).Stack == 0 ? roster[0] : roster[1];
            return new HoldemSnapshot(SessionId, Version, HandNumber, ButtonPolicy,
                CanContinue, IsOver, SessionWinnerSeat, compatibilityBusted,
                roster, settledLedger, currentHand, viewer, currentHand.IsRevealPending ? accusations?.ForViewer(viewer) : null, pendingDeal,
                FixedBettingForViewer(viewer), accusationOutcomes, roundInMatch);
        }

        private SeatId ResolveNextButton()
        {
            Predicate<SeatId> funded = seat => settledLedger.GetChips(seat).Stack > 0;
            if (currentHand != null) return HoldemSeatOrder.Next(roster, currentHand.ButtonSeat, funded);
            if (funded(initialButton)) return initialButton;
            return HoldemSeatOrder.Next(roster, initialButton, funded);
        }

        private ChipLedger MergeSettled(HoldemHand hand)
            => MergeSettled(hand, settledLedger);

        private SeatId ResolveInitialButton() => initialLedger.GetChips(initialButton).Stack > 0 ? initialButton
            : HoldemSeatOrder.Next(roster, initialButton, seat => initialLedger.GetChips(seat).Stack > 0);

        private ChipLedger MergeSettled(HoldemHand hand, ChipLedger baseLedger)
        {
            if (!hand.IsComplete) throw new InvalidOperationException("Only a completed hand can update session stacks.");
            var balances = new SeatChips[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                SeatId seat = roster[i];
                long stack = hand.WasDealtIn(seat)
                    ? hand.Result.Ledger.GetChips(seat).Stack
                    : baseLedger.GetChips(seat).Stack;
                balances[i] = new SeatChips(seat, stack);
            }
            return ChipLedger.Create(balances);
        }

        private SeatId CompatibilitySeat(int index)
        {
            RequireHeadsUp();
            return roster[index];
        }

        private void RequireHeadsUp()
        {
            if (roster.Length != 2)
                throw new InvalidOperationException("The heads-up compatibility alias is unavailable at a multi-seat table.");
        }

        private HoldemReceipt Reject(HoldemCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId,
                command.Seat, null, error);

        private HoldemReceipt Reject(HoldemStartCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId,
                null, null, error);

        private HoldemReceipt Reject(SettlementCommand command, HoldemCommandError error)
            => new HoldemReceipt(SessionId, command.HandId, command.CommandId, null, null, error);

        private HoldemReceipt Reject(HoldemRevealCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId, null, null, error);

        private HoldemReceipt Reject(FinalConversationCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId, null, null, error);

        private static ChipLedger CreateEqualLedger(HoldemConfig config,
            IReadOnlyList<SeatId> seatsInTableOrder)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            SeatId[] seats = HoldemSeatOrder.CopyTable(seatsInTableOrder);
            var balances = new SeatChips[seats.Length];
            for (int i = 0; i < seats.Length; i++) balances[i] = new SeatChips(seats[i], config.StartingStack);
            return ChipLedger.Create(balances);
        }

        private static int CountFunded(ChipLedger ledger, IReadOnlyList<SeatId> seats)
        {
            int count = 0;
            for (int i = 0; i < seats.Count; i++) if (ledger.GetChips(seats[i]).Stack > 0) count++;
            return count;
        }

        private static void ValidateRule(HoldemOddChipRule rule)
        {
            if (rule != HoldemOddChipRule.RequireExplicitPriority && rule != HoldemOddChipRule.ClockwiseFromButton)
                throw new ArgumentOutOfRangeException(nameof(rule));
        }
    }
}
