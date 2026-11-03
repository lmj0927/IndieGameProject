using System;

namespace Poker.Foundation
{
    /// <summary>Immutable shared hand rules. The social prototype uses explicit ante/fixed-wager settings.</summary>
    public sealed class HoldemConfig
    {
        private readonly long[] anteSteps;
        public HoldemConfig(long startingStack, long smallBlind, long bigBlind,
            HoldemRevealPolicy revealPolicy = HoldemRevealPolicy.Automatic,
            HoldemAccusationMode accusationMode = HoldemAccusationMode.Disabled,
            HoldemDealPolicy dealPolicy = HoldemDealPolicy.Automatic,
            HoldemBettingPolicy bettingPolicy = HoldemBettingPolicy.NoLimit,
            long ante = 0, long fixedBetIncrement = 0, long[] anteSteps = null, bool incrementFollowsAnte = false,
            SequentialBettingRules sequentialRules = null, bool turnAccusations = false)
        {
            if (startingStack <= 0) throw new ArgumentOutOfRangeException(nameof(startingStack));
            if (bettingPolicy == HoldemBettingPolicy.NoLimit)
            {
                if (smallBlind <= 0) throw new ArgumentOutOfRangeException(nameof(smallBlind));
                if (bigBlind < smallBlind) throw new ArgumentOutOfRangeException(nameof(bigBlind));
                if (ante != 0 || fixedBetIncrement != 0 || anteSteps?.Length > 0 || incrementFollowsAnte)
                    throw new ArgumentException("Ante and fixed increment require the shared-ante policy.");
            }
            else if (bettingPolicy == HoldemBettingPolicy.SharedAnteFixed)
            {
                if (smallBlind != 0 || bigBlind != 0)
                    throw new ArgumentException("Shared ante does not post blinds.");
                if (ante <= 0) throw new ArgumentOutOfRangeException(nameof(ante));
                if (fixedBetIncrement <= 0) throw new ArgumentOutOfRangeException(nameof(fixedBetIncrement));
                if (revealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal || dealPolicy != HoldemDealPolicy.WaitForHost)
                    throw new ArgumentException("Shared-ante flow requires host deal and reveal windows.");
            }
            else if (bettingPolicy == HoldemBettingPolicy.SharedAnteSequential)
            {
                if (smallBlind != 0 || bigBlind != 0 || fixedBetIncrement != 0 || incrementFollowsAnte)
                    throw new ArgumentException("Sequential ante play does not use blinds or fixed increments.");
                if (ante <= 0) throw new ArgumentOutOfRangeException(nameof(ante));
                if (sequentialRules == null) throw new ArgumentNullException(nameof(sequentialRules),
                    "The unresolved sequential options must be supplied explicitly.");
                if (revealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal || dealPolicy != HoldemDealPolicy.WaitForHost)
                    throw new ArgumentException("Sequential ante play requires host deal and reveal boundaries.");
            }
            else throw new ArgumentOutOfRangeException(nameof(bettingPolicy));
            if (sequentialRules != null && bettingPolicy != HoldemBettingPolicy.SharedAnteSequential)
                throw new ArgumentException("Sequential options require sequential ante play.", nameof(sequentialRules));
            if (anteSteps != null && anteSteps.Length > 0)
            {
                if (anteSteps.Length > 32 || anteSteps[0] != ante) throw new ArgumentException("Ante steps must begin at the configured ante and contain at most 32 entries.", nameof(anteSteps));
                long previous = 0;
                foreach (long value in anteSteps)
                {
                    if (value <= 0 || value < previous) throw new ArgumentException("Ante steps must be positive and nondecreasing.", nameof(anteSteps));
                    previous = value;
                }
            }
            if (incrementFollowsAnte && fixedBetIncrement != ante)
                throw new ArgumentException("An ante-linked increment must start at the ante.", nameof(incrementFollowsAnte));
            if (revealPolicy != HoldemRevealPolicy.Automatic && revealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal)
                throw new ArgumentOutOfRangeException(nameof(revealPolicy));
            if (accusationMode != HoldemAccusationMode.Disabled && accusationMode != HoldemAccusationMode.CollectLatestChoiceUntilHostCloses
                && accusationMode != HoldemAccusationMode.SingleClaimSelectedAtClose
                && accusationMode != HoldemAccusationMode.OnTurnFirstOrbit)
                throw new ArgumentOutOfRangeException(nameof(accusationMode));
            if (accusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose && bettingPolicy != HoldemBettingPolicy.SharedAnteFixed)
                throw new ArgumentException("Single-claim consequences require the shared-ante policy.", nameof(accusationMode));
            if (accusationMode == HoldemAccusationMode.OnTurnFirstOrbit && bettingPolicy != HoldemBettingPolicy.SharedAnteSequential)
                throw new ArgumentException("On-turn accusations require sequential ante play.", nameof(accusationMode));
            if (bettingPolicy == HoldemBettingPolicy.SharedAnteSequential
                && accusationMode != HoldemAccusationMode.Disabled && accusationMode != HoldemAccusationMode.OnTurnFirstOrbit)
                throw new ArgumentException("Sequential ante play cannot use a simultaneous accusation window.", nameof(accusationMode));
            if (accusationMode != HoldemAccusationMode.Disabled && revealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal)
                throw new ArgumentException("Accusation input requires community-card reveal windows.", nameof(accusationMode));
            if (dealPolicy != HoldemDealPolicy.Automatic && dealPolicy != HoldemDealPolicy.WaitForHost)
                throw new ArgumentOutOfRangeException(nameof(dealPolicy));
            if (turnAccusations && (accusationMode != HoldemAccusationMode.Disabled || bettingPolicy != HoldemBettingPolicy.NoLimit))
                throw new ArgumentException("Legacy turn accusations require no-limit play without another accusation mode.", nameof(turnAccusations));
            StartingStack = startingStack;
            SmallBlind = smallBlind;
            BigBlind = bigBlind;
            RevealPolicy = revealPolicy;
            AccusationMode = accusationMode;
            DealPolicy = dealPolicy;
            BettingPolicy = bettingPolicy;
            Ante = ante;
            FixedBetIncrement = fixedBetIncrement;
            this.anteSteps = anteSteps == null ? Array.Empty<long>() : (long[])anteSteps.Clone();
            IncrementFollowsAnte = incrementFollowsAnte;
            SequentialRules = sequentialRules;
            TurnAccusations = turnAccusations;
        }

        public static HoldemConfig CreateSharedAnteFixed(long startingStack, long ante, long increment,
            HoldemAccusationMode accusationMode = HoldemAccusationMode.CollectLatestChoiceUntilHostCloses,
            long[] anteSteps = null, bool incrementFollowsAnte = false)
            => new HoldemConfig(startingStack, 0, 0, HoldemRevealPolicy.PauseAfterCommunityReveal,
                accusationMode, HoldemDealPolicy.WaitForHost, HoldemBettingPolicy.SharedAnteFixed, ante, increment, anteSteps, incrementFollowsAnte);

        public long[] CopyAnteSteps() => (long[])anteSteps.Clone();
        public static HoldemConfig CreateSharedAnteSequential(long startingStack, long ante,
            SequentialBettingRules rules, long[] anteSteps = null)
            => new HoldemConfig(startingStack, 0, 0, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.OnTurnFirstOrbit, HoldemDealPolicy.WaitForHost,
                HoldemBettingPolicy.SharedAnteSequential, ante: ante, anteSteps: anteSteps, sequentialRules: rules);

        public int AnteStepCount => anteSteps.Length;
        public bool IncrementFollowsAnte { get; }
        public long AnteForRound(long roundInMatch)
        {
            if (roundInMatch < 1) throw new ArgumentOutOfRangeException(nameof(roundInMatch));
            return anteSteps.Length == 0 ? Ante : anteSteps[(int)Math.Min(roundInMatch - 1, anteSteps.Length - 1L)];
        }
        public HoldemConfig ForRound(long roundInMatch)
        {
            if (roundInMatch < 1) throw new ArgumentOutOfRangeException(nameof(roundInMatch));
            if (anteSteps.Length == 0 || BettingPolicy == HoldemBettingPolicy.NoLimit) return this;
            long currentAnte = AnteForRound(roundInMatch);
            if (BettingPolicy == HoldemBettingPolicy.SharedAnteSequential)
                return new HoldemConfig(StartingStack, 0, 0, RevealPolicy, AccusationMode, DealPolicy,
                    BettingPolicy, ante: currentAnte, sequentialRules: SequentialRules);
            return CreateSharedAnteFixed(StartingStack, currentAnte, IncrementFollowsAnte ? currentAnte : FixedBetIncrement, AccusationMode);
        }

        public long StartingStack { get; }
        public long SmallBlind { get; }
        public long BigBlind { get; }
        public HoldemRevealPolicy RevealPolicy { get; }
        public HoldemAccusationMode AccusationMode { get; }
        public bool TurnAccusations { get; }
        public HoldemDealPolicy DealPolicy { get; }
        public HoldemBettingPolicy BettingPolicy { get; }
        public long Ante { get; }
        public long FixedBetIncrement { get; }
        public SequentialBettingRules SequentialRules { get; }
        public long MinimumBet => BettingPolicy == HoldemBettingPolicy.SharedAnteSequential
            ? SequentialRules.MinimumBet : BettingPolicy == HoldemBettingPolicy.SharedAnteFixed ? FixedBetIncrement : BigBlind;
    }

    public enum HoldemBettingPolicy
    {
        NoLimit = 0,
        SharedAnteFixed = 1,
        SharedAnteSequential = 2
    }

    public enum HoldemDealPolicy
    {
        Automatic = 0,
        WaitForHost = 1
    }

    public enum HoldemAccusationMode
    {
        Disabled = 0,
        CollectLatestChoiceUntilHostCloses = 1,
        // Selection (lottery/priority/first input) belongs to the host adapter, not the card engine.
        SingleClaimSelectedAtClose = 2,
        OnTurnFirstOrbit = 3
    }

    public enum HoldemRevealPolicy
    {
        Automatic = 0,
        PauseAfterCommunityReveal = 1
    }

    public enum HoldemStreet
    {
        Preflop = 0,
        Flop = 1,
        Turn = 2,
        River = 3,
        Complete = 4
    }

    public enum HoldemResultKind
    {
        Fold = 1,
        Showdown = 2,
        Accusation = 3
    }

    public enum HoldemButtonPolicy
    {
        PokerStarsForwardMoving = 1
    }

    public enum HoldemOddChipRule
    {
        RequireExplicitPriority = 0,
        ClockwiseFromButton = 1
    }

    public enum HoldemSettlementState
    {
        None = 0,
        AwaitingOddChipPriority = 1,
        Settled = 2
    }

    public enum HoldemSeatStatus
    {
        Busted = 0,
        Active = 1,
        Folded = 2,
        AllIn = 3
    }
}
