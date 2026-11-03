using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Reads only public chip movements from the containing validated game projection.</summary>
    public static class HoldemHistoryPacketReader
    {
        public static HoldemHandHistory Read(HoldemRoomPacket room, HoldemHandHistory previous = null)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            if (!room.hasHistory)
            {
                if (previous != null) throw Invalid();
                return null;
            }
            var input = room.history;
            var game = room.game;
            if (room.protocol != HoldemRoomPacketMapper.ProtocolVersion || !room.hasGame || game == null
                || input == null || input.entries == null || input.entries.Length < 2
                || input.entries.Length > HoldemRoom.MaximumHistoryEntries || input.omittedCount < 0
                || input.omittedCount > long.MaxValue - input.entries.Length
                || input.omittedCount > 0 && input.entries.Length != HoldemRoom.MaximumHistoryEntries
                || game.handNumber < 1 || game.seats == null) throw Invalid();
            Guid session = Id(room.sessionId), hand = Id(game.handId);
            var seats = new HashSet<int>();
            foreach (var seat in game.seats) if (seat != null && seat.dealtIn) seats.Add(seat.seat);
            bool shared = game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteFixed;
            bool sequential = game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteSequential;
            bool hasAnte = shared || sequential;
            var roundConfig = hasAnte ? HoldemRoomPacketMapper.ReadRoundConfig(room) : null;
            if (!Enum.IsDefined(typeof(HoldemBettingPolicy), game.bettingPolicy)
                || hasAnte && (!room.hasRules || room.rules == null || room.rules.bettingPolicy != game.bettingPolicy
                    || game.postedAnte <= 0 || game.postedAnte > roundConfig.Ante
                    || shared && room.rules.fixedBetIncrement <= 0)) throw Invalid();
            var anteSeats = new List<HoldemSeatPacket>();
            foreach (var seat in game.seats) if (seat != null && seat.dealtIn) anteSeats.Add(seat);
            anteSeats.Sort((a, b) => a.tableIndex.CompareTo(b.tableIndex));
            int openingCount = hasAnte ? anteSeats.Count : 2;
            if (input.omittedCount == 0 && input.entries.Length < openingCount) throw Invalid();
            var entries = new List<HoldemHistoryEntry>(input.entries.Length);
            int lastStreet = -1;
            HoldemHistoryEntry lastAction = null;
            long netPaid = 0;
            var accusationFolds = new HashSet<int>();
            for (int i = 0; i < input.entries.Length; i++)
            {
                var entry = input.entries[i];
                if (entry == null || !Enum.IsDefined(typeof(HoldemHistoryKind), entry.kind)
                    || !seats.Contains(entry.seat) || entry.street < 0 || entry.street > 3
                    || entry.street > game.street || entry.street < lastStreet || entry.amount < 0 || entry.streetTotal < 0)
                    throw Invalid();
                long ordinal = input.omittedCount + i;
                var kind = (HoldemHistoryKind)entry.kind;
                BettingActionKind? action = null;
                if (kind == HoldemHistoryKind.SmallBlind || kind == HoldemHistoryKind.BigBlind)
                {
                    if (hasAnte || ordinal != (kind == HoldemHistoryKind.SmallBlind ? 0 : 1) || entry.street != 0
                        || entry.seat != (kind == HoldemHistoryKind.SmallBlind ? game.smallBlind : game.bigBlind)
                        || entry.amount <= 0 || entry.streetTotal != entry.amount || entry.hasAction || entry.action != 0) throw Invalid();
                }
                else if (kind == HoldemHistoryKind.Ante)
                {
                    bool full = entry.amount == game.postedAnte;
                    bool shortPaid = sequential && roundConfig.SequentialRules.AnteShortfall == ChipShortfallPolicy.PayAvailable
                        && entry.amount > 0 && entry.amount < game.postedAnte && entry.allIn;
                    if (!hasAnte || ordinal >= openingCount || entry.seat != anteSeats[(int)ordinal].seat
                        || entry.street != 0 || !(full || shortPaid) || entry.streetTotal != entry.amount
                        || entry.hasAction || entry.action != 0) throw Invalid();
                }
                else if (kind == HoldemHistoryKind.FixedWager || kind == HoldemHistoryKind.FixedFold || kind == HoldemHistoryKind.AccusationFold)
                {
                    if (!(shared || sequential && kind == HoldemHistoryKind.AccusationFold)
                        || ordinal < openingCount || entry.street == 0 || entry.hasAction || entry.action != 0) throw Invalid();
                    if (kind == HoldemHistoryKind.AccusationFold)
                    {
                        if (!(room.rules.accusationMode == 2 || sequential && room.rules.accusationMode == 3)
                            || !accusationFolds.Add(entry.seat)) throw Invalid();
                        bool matched = false;
                        foreach (var outcome in game.accusationOutcomes ?? Array.Empty<HoldemAccusationOutcomePacket>())
                            if (outcome != null && !outcome.hit && outcome.accuser == entry.seat && outcome.street == entry.street) matched = true;
                        if (!matched) throw Invalid();
                    }
                    if (kind == HoldemHistoryKind.FixedWager
                        ? entry.amount <= 0 || entry.amount > roundConfig.FixedBetIncrement || entry.streetTotal < entry.amount
                        : entry.amount != 0 || entry.streetTotal != 0 || entry.allIn) throw Invalid();
                }
                else if (kind == HoldemHistoryKind.UncalledReturn)
                {
                    if (ordinal < openingCount || entry.amount <= 0 || entry.streetTotal != 0 || entry.allIn || entry.hasAction || entry.action != 0)
                        throw Invalid();
                }
                else
                {
                    if (shared || ordinal < openingCount || !entry.hasAction || !Enum.IsDefined(typeof(BettingActionKind), entry.action)) throw Invalid();
                    action = (BettingActionKind)entry.action;
                    bool pays = action == BettingActionKind.Call || action == BettingActionKind.BetTo || action == BettingActionKind.RaiseTo;
                    if (pays ? entry.amount <= 0 || entry.streetTotal < entry.amount : entry.amount != 0 || entry.allIn) throw Invalid();
                }
                lastStreet = entry.street;
                var decoded = new HoldemHistoryEntry(kind, (HoldemStreet)entry.street, new SeatId(entry.seat), action,
                    entry.amount, entry.streetTotal, entry.allIn);
                entries.Add(decoded);
                if (kind == HoldemHistoryKind.Action) lastAction = decoded;
                if (input.omittedCount == 0)
                {
                    try { netPaid = checked(netPaid + (kind == HoldemHistoryKind.UncalledReturn ? -entry.amount : entry.amount)); }
                    catch (OverflowException) { throw Invalid(); }
                }
            }
            if (room.hasLastAction != (lastAction != null) || input.omittedCount == 0 && netPaid != game.pot) throw Invalid();
            if (lastAction != null && (room.lastAction == null || room.lastAction.seat != lastAction.Seat.Value
                || room.lastAction.kind != (int)lastAction.Action.Value || room.lastAction.street != (int)lastAction.Street
                || room.lastAction.paid != lastAction.Amount)) throw Invalid();
            var result = new HoldemHandHistory(session, hand, game.handNumber, entries, input.omittedCount);
            if (previous != null)
            {
                if (previous.SessionId != session || game.handNumber < previous.HandNumber
                    || (game.handNumber == previous.HandNumber) != (hand == previous.HandId)) throw Invalid();
                if (hand == previous.HandId)
                {
                    long oldEnd = previous.OmittedCount + previous.Count, newEnd = result.OmittedCount + result.Count;
                    if (result.OmittedCount < previous.OmittedCount || newEnd < oldEnd) throw Invalid();
                    for (long ordinal = Math.Max(previous.OmittedCount, result.OmittedCount); ordinal < oldEnd; ordinal++)
                        if (!Same(previous.GetEntry((int)(ordinal - previous.OmittedCount)), result.GetEntry((int)(ordinal - result.OmittedCount))))
                            throw Invalid();
                }
            }
            return result;
        }

        private static bool Same(HoldemHistoryEntry a, HoldemHistoryEntry b) => a.Kind == b.Kind && a.Street == b.Street
            && a.Seat == b.Seat && a.Action == b.Action && a.Amount == b.Amount && a.StreetTotal == b.StreetTotal && a.IsAllIn == b.IsAllIn;
        private static Guid Id(string value)
        {
            if (value == null || value.Length != 32 || !Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty) throw Invalid();
            return id;
        }
        private static ArgumentException Invalid() => new ArgumentException("Invalid public betting history.");
    }
}
