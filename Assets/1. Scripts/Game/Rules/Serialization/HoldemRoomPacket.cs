using System;
using Poker.Foundation;

namespace Poker.Application
{
    // Explicit wire fields: never serialize HoldemRoom, HoldemSession or an admission into a state broadcast.
    // Protocol 1 uses seats 1..4; 0 means no seat. Optional objects require their has* flag.
    // Card IDs use Card.Id (0..51). Public best-five cards may be evaluated for display only;
    // clients must use the host's result/payouts and never calculate authoritative settlement.
    [Serializable]
    public sealed class HoldemRoomPacket
    {
        public int protocol;
        public string sessionId;
        public long revision;
        public int viewerSeat;
        public bool paused, hasGame, hasLastAction;
        public bool supportsLobbyLeave;
        public bool hasRematch, rematchSupported;
        public long matchNumber;
        public bool hasRules;
        public HoldemRoomRulesPacket rules;
        public bool hasHistory;
        public HoldemHistoryPacket history;
        public bool hasOwnUtterances;
        public HoldemOwnUtterancePacket ownUtterances;
        public bool hasPublicUtterances;
        public HoldemPublicUtterancePacket publicUtterances;
        public bool hasOwnCardChange;
        public HoldemOwnCardChangePacket ownCardChange;
        public bool hasChat, hasTurnAccusation;
        public HoldemChatPacket chat;
        public HoldemTurnAccusationPacket turnAccusation;
        public bool hasOwnHandCardChanges;
        public HoldemOwnCardChangePacket[] ownHandCardChanges;
        public HoldemRoomMemberPacket[] members;
        public HoldemGamePacket game;
        public HoldemActionPacket lastAction;
        public bool hasProgressClock;
        public HoldemProgressClockPacket progressClock;
        // Sequential speech is transferred in bounded pages. Cleared after client-side assembly.
        public bool hasSpeechPage;
        public int speechStart, speechTotal;
        public HoldemDesignationPatchPacket[] designationPatches;
        public bool hasEliminatedSpeech;
        public HoldemEliminatedSpeechPacket eliminatedSpeech;

        public int OwnDesignationRevision => hasOwnUtterances ? ownUtterances?.designationRevision ?? 0 : 0;
        public long EliminatedSpeechRevision => hasEliminatedSpeech ? eliminatedSpeech?.revision ?? 0 : 0;
        public bool IsNewerThan(HoldemRoomPacket previous) => previous == null || revision > previous.revision
            || revision == previous.revision && (OwnDesignationRevision > previous.OwnDesignationRevision
                || EliminatedSpeechRevision > previous.EliminatedSpeechRevision);
    }

    [Serializable]
    public sealed class HoldemChatPacket
    {
        public int maximumTextLength;
        public HoldemChatEntryPacket[] entries;
    }

    [Serializable]
    public sealed class HoldemChatEntryPacket
    {
        public long sequence;
        public int seat;
        public string text;
    }

    [Serializable]
    public sealed class HoldemTurnAccusationPacket
    {
        public string handId;
        public int street, accuser, target;
        public bool caught;
    }

    [Serializable]
    public sealed class HoldemEliminatedSpeechPacket
    {
        public long revision;
        public HoldemOwnUtterancePacket own;
        public HoldemPublicUtterancePacket messages;
        public bool hasPage;
        public int start, total;
        public HoldemDesignationPatchPacket[] designationPatches;
    }

    [Serializable]
    public sealed class HoldemDesignationPatchPacket
    {
        public string commandId, designationId;
    }

    [Serializable]
    public sealed class HoldemProgressClockPacket
    {
        public string handId, windowId;
        public int stage, remainingMilliseconds;
        public int turnSeat;
        public long turnHandVersion;
    }

    // This connection's revealed success only. No source deck position, original
    // card, raw interpretation, other speaker or host record belongs on the wire.
    [Serializable]
    public sealed class HoldemOwnCardChangePacket
    {
        public string handId, dealWindowId;
        public int street, boardIndex, card;
    }

    [Serializable]
    public sealed class HoldemHistoryPacket
    {
        public long omittedCount;
        public HoldemHistoryEntryPacket[] entries;
    }

    [Serializable]
    public sealed class HoldemHistoryEntryPacket
    {
        public int kind, street, seat, action;
        public long amount, streetTotal;
        public bool hasAction, allIn;
    }

    [Serializable]
    public sealed class HoldemRoomRulesPacket
    {
        // Omitted/zero in older protocol-1 four-player rooms. New senders explicitly specify 2..4.
        public int seatCapacity;
        public long startingStack, smallBlind, bigBlind;
        public int bettingPolicy;
        public long ante, fixedBetIncrement;
        public long[] anteSteps;
        public bool incrementFollowsAnte;
        public bool waitsForHostDeal, pausesAfterReveal, receivesUtterances, publishesUtterances;
        public bool accusationsEnabled;
        public int accusationMode;
        public bool hasSequentialRules;
        public SequentialBettingRulesPacket sequentialRules;
        public bool hasSequentialPacing;
        public SequentialPacingPacket sequentialPacing;
    }

    [Serializable]
    public sealed class SequentialPacingPacket
    {
        public int preflopMilliseconds, flopMilliseconds, turnMilliseconds, riverMilliseconds, revealMilliseconds, origin;
        public int actionTurnMilliseconds;
        public bool neutralDealerPreview;
    }

    [Serializable]
    public sealed class SequentialBettingRulesPacket
    {
        public long minimumBet, maximumOpeningBet;
        public bool allowCheck;
        public int firstSeat, allInCap, anteShortfall, fineShortfall, accusationScope, baitFineLimit;
    }

    // This viewer's choice and result only. No other claims, decisions, AI output or manipulation ledger.
    [Serializable]
    public sealed class HoldemAccusationPacket
    {
        public string windowId;
        public int phase, eligibleCount, responseCount;
        public bool canRespond, hasResponded, hasOwnTarget, hasOwnVerdict, ownVerdict;
        public int ownTarget;
        public long ownChoiceRevision;
        public bool ownAdmitted;
        public int[] targets;
    }

    [Serializable]
    public sealed class HoldemAccusationOutcomePacket
    {
        public string windowId;
        public int street, accuser, target;
        public bool hit;
        public long fine, potAward;
    }

    [Serializable]
    public sealed class HoldemPublicUtterancePacket
    {
        // Missing in older senders: keep the legacy separate history display in that case.
        public bool hasHistoryOrder;
        public HoldemPublicUtteranceEntryPacket[] entries;
    }

    [Serializable]
    public sealed class HoldemPublicUtteranceEntryPacket
    {
        public int seat, street;
        public string text;
        public long historyPosition;
    }

    // Source text confirmation only. Speaker/session/hand come from the containing bound room packet.
    // Never add host batch ordering, interpretation, future cards, or success to this projection.
    [Serializable]
    public sealed class HoldemOwnUtterancePacket
    {
        public string windowId;
        public bool canSubmit;
        public bool canDesignate;
        public int designationRevision;
        public bool backlogged;
        public bool unlimitedMessages;
        public int maximumTextLength, remaining;
        public HoldemOwnUtteranceEntryPacket[] entries;
    }

    [Serializable]
    public sealed class HoldemOwnUtteranceEntryPacket
    {
        public string commandId, windowId, text, designationId;
        public int street;
        public bool designationEligible;
    }

    [Serializable]
    public sealed class HoldemRoomMemberPacket
    {
        public int seat;
        public string name;
        public bool connected, ready, host;
        public bool rematchReady;
        public long rematchRevision;
    }

    [Serializable]
    public sealed class HoldemGamePacket
    {
        public int bettingPolicy;
        public long postedAnte;
        public bool hasFixedBetting;
        public FixedBettingPacket fixedBetting;
        public bool hasAccusations;
        public HoldemAccusationPacket accusations;
        public HoldemAccusationOutcomePacket[] accusationOutcomes;
        public string handId;
        public long version, handVersion, handNumber, pot, roundInMatch;
        public int street, settlementState, button, smallBlind, bigBlind, currentSeat, sessionWinner;
        public bool canContinue, isOver, revealPending, dealPending, hasLegal, hasResult, canAccuseOnTurn;
        public bool finalConversationPending;
        public string dealWindowId;
        public int dealStreet;
        public int[] board;
        public HoldemSeatPacket[] seats;
        public HoldemLegalPacket legal;
        public HoldemResultPacket result;
        // Optional protocol-1 display extension; an older sender omits this and uses lastAction only.
        public bool hasStreetActions;
        public HoldemActionPacket[] streetActions;
    }

    // A viewer's own pending choice plus only already-sealed public raisers.
    [Serializable]
    public sealed class FixedBettingPacket
    {
        public string windowId;
        public int phase, street, ownProposal, ownResponse;
        public long increment, ownChoiceRevision;
        public bool canPropose, canRespond;
        public int[] raisers;
    }

    [Serializable]
    public sealed class HoldemSeatPacket
    {
        public int seat, tableIndex, status;
        public bool viewer, dealtIn, button, smallBlind, bigBlind, currentActor;
        public long stack, committed, streetContribution, awarded;
        public int[] visibleCards, revealedBestCards;
    }

    [Serializable]
    public sealed class HoldemLegalPacket
    {
        public bool fold, check, call, bet, raise;
        public long callAmount, minimumTarget, maximumTarget;
        public bool hasAllInTarget;
        public long allInTarget;
    }

    [Serializable]
    public sealed class HoldemResultPacket
    {
        public int kind, winnerSeat, foldedSeat, accusedSeat;
        public long pot;
        public HoldemPotPacket[] pots;
    }

    [Serializable]
    public sealed class HoldemPotPacket
    {
        public long lowerBound, contributionCap, amount;
        public int[] eligibleSeats;
        public HoldemPayoutPacket[] payouts;
    }

    [Serializable]
    public sealed class HoldemPayoutPacket
    {
        public int seat;
        public long amount;
        public bool includesOddChip;
    }

    [Serializable]
    public sealed class HoldemActionPacket
    {
        public int seat, kind, street;
        public long paid, target;
    }

    public static class HoldemRoomPacketMapper
    {
        public static SequentialPacingProfile ReadSequentialPacing(HoldemRoomRulesPacket rules)
        {
            if (rules == null) throw new ArgumentException("Missing room rules.");
            var p = rules.sequentialPacing;
            if (!rules.hasSequentialPacing)
            {
                // JsonUtility can materialize an omitted optional object as an empty one.
                if (p != null && (p.preflopMilliseconds != 0 || p.flopMilliseconds != 0 || p.turnMilliseconds != 0
                    || p.riverMilliseconds != 0 || p.revealMilliseconds != 0 || p.origin != 0 || p.neutralDealerPreview
                    || p.actionTurnMilliseconds != 0))
                    throw new ArgumentException("Unexpected sequential pacing payload.");
                return null;
            }
            if (p == null || rules.bettingPolicy != (int)HoldemBettingPolicy.SharedAnteSequential)
                throw new ArgumentException("Sequential pacing requires sequential room rules.");
            return new SequentialPacingProfile(p.preflopMilliseconds, p.flopMilliseconds, p.turnMilliseconds,
                p.riverMilliseconds, p.revealMilliseconds, (SequentialConversationClockOrigin)p.origin, p.neutralDealerPreview,
                p.actionTurnMilliseconds);
        }

        public static HoldemConfig ReadRulesConfig(HoldemRoomRulesPacket r)
        {
            ReadSequentialPacing(r);
            if (r == null || !Enum.IsDefined(typeof(HoldemAccusationMode), r.accusationMode)
                || !r.accusationsEnabled && r.accusationMode != 0
                || r.hasSequentialRules != (r.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteSequential))
                throw new ArgumentException("Invalid room rules.");
            SequentialBettingRules sequential = null;
            if (r.hasSequentialRules)
            {
                var s = r.sequentialRules;
                if (s == null) throw new ArgumentException("Missing sequential rules.");
                sequential = new SequentialBettingRules(s.minimumBet, s.maximumOpeningBet, s.allowCheck,
                    (SequentialFirstSeat)s.firstSeat, (SequentialAllInCap)s.allInCap,
                    (ChipShortfallPolicy)s.anteShortfall, (ChipShortfallPolicy)s.fineShortfall,
                    (TurnAccusationScope)s.accusationScope, (BaitFineLimit)s.baitFineLimit);
            }
            return new HoldemConfig(r.startingStack, r.smallBlind, r.bigBlind,
                r.pausesAfterReveal ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic,
                r.accusationsEnabled ? (HoldemAccusationMode)(r.accusationMode == 0 ? 1 : r.accusationMode) : HoldemAccusationMode.Disabled,
                r.waitsForHostDeal ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic,
                (HoldemBettingPolicy)r.bettingPolicy, r.ante, r.fixedBetIncrement, r.anteSteps, r.incrementFollowsAnte, sequential);
        }
        public static HoldemConfig ReadRoundConfig(HoldemRoomPacket packet)
        {
            if (packet?.game == null) throw new ArgumentException("Missing round state.");
            var rules = ReadRulesConfig(packet.rules);
            long round = packet.game.roundInMatch;
            // Fixed-ante legacy senders omit this counter. An escalating room must supply it.
            if (round == 0 && rules.AnteStepCount == 0) round = packet.game.handNumber;
            if (round < 1 || round > packet.game.handNumber) throw new ArgumentException("Invalid match round.");
            return rules.ForRound(round);
        }
        public const int ProtocolVersion = 1;

        public static HoldemProgressClock ReadProgressClock(HoldemRoomPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (!packet.hasProgressClock)
            {
                // Unity's JSON serializer can materialize an empty nested object for a null field.
                var absent = packet.progressClock;
                if (absent != null && (!string.IsNullOrEmpty(absent.handId) || !string.IsNullOrEmpty(absent.windowId)
                    || absent.stage != 0 || absent.remainingMilliseconds != 0 || absent.turnSeat != 0 || absent.turnHandVersion != 0))
                    throw new ArgumentException("Unexpected clock payload.");
                return null; // Older and externally driven rooms need not supply a clock.
            }
            var value = packet.progressClock;
            var game = packet.game;
            if (value == null || !packet.hasGame || game == null || game.hasResult
                || game.bettingPolicy != (int)HoldemBettingPolicy.SharedAnteFixed
                    && game.bettingPolicy != (int)HoldemBettingPolicy.SharedAnteSequential
                || value.handId != game.handId || value.handId?.Length != 32 || value.windowId?.Length != 32
                || !Guid.TryParseExact(value.handId, "N", out var hand) || !Guid.TryParseExact(value.windowId, "N", out var window))
                throw new ArgumentException("Clock does not belong to the current game.");
            var sample = new HoldemProgressClock(hand, window, value.stage, value.remainingMilliseconds,
                value.turnSeat == 0 ? (SeatId?)null : new SeatId(value.turnSeat), value.turnHandVersion);
            bool matches = game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteSequential
                ? window == hand && sample.Stage == HoldemProgressClock.SequentialPreflop + game.street
                    && game.street >= 0 && game.street <= (int)HoldemStreet.River && !game.revealPending
                    && game.settlementState != (int)HoldemSettlementState.AwaitingOddChipPriority
                    && (!sample.TurnSeat.HasValue || value.turnSeat == game.currentSeat && value.turnHandVersion == game.handVersion)
                : sample.Stage < HoldemProgressClock.SequentialPreflop
                    && (sample.Stage == HoldemProgressClock.Requests ? game.dealPending && game.dealWindowId == value.windowId
                : sample.Stage == HoldemProgressClock.Accusations ? game.hasAccusations && game.accusations != null
                    && game.accusations.windowId == value.windowId && game.accusations.phase == (int)HoldemAccusationPhase.Collecting
                : game.hasFixedBetting && game.fixedBetting != null && game.fixedBetting.windowId == value.windowId
                    && game.fixedBetting.phase == (int)(sample.Stage == HoldemProgressClock.Proposal ? FixedBettingPhase.Proposal : FixedBettingPhase.Response));
            if (!matches) throw new ArgumentException("Clock does not belong to the current input window.");
            if (game.bettingPolicy == (int)HoldemBettingPolicy.SharedAnteSequential
                && !sample.FitsSequentialProfile(ReadSequentialPacing(packet.rules), game.currentSeat > 0))
                throw new ArgumentException("Clock exceeds or lacks the room's sequential pacing profile.");
            return sample;
        }

        /// <summary>Call separately for each bound connection's room view. Returns a detached payload, not authority.</summary>
        public static HoldemRoomPacket Create(HoldemRoomView source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var packet = new HoldemRoomPacket
            {
                protocol = ProtocolVersion, sessionId = source.SessionId.ToString("N"), revision = source.Revision,
                viewerSeat = source.ViewerSeat.Value, paused = source.Paused, hasGame = source.Game != null,
                supportsLobbyLeave = true, hasOwnUtterances = source.OwnUtterances != null,
                hasRematch = true, rematchSupported = source.RematchSupported, matchNumber = source.MatchNumber,
                hasPublicUtterances = source.PublicUtterances != null,
                hasOwnCardChange = source.OwnCardChange != null,
                hasOwnHandCardChanges = source.OwnHandCardChangeCount > 0,
                hasRules = source.Rules != null,
                hasHistory = source.History != null,
                members = new HoldemRoomMemberPacket[source.MemberCount], hasLastAction = source.LastAction != null
            };
            if (source.ProgressClock != null)
            {
                var clock = source.ProgressClock;
                packet.hasProgressClock = true;
                packet.progressClock = new HoldemProgressClockPacket { handId = clock.HandId.ToString("N"),
                    windowId = clock.WindowId.ToString("N"), stage = clock.Stage, remainingMilliseconds = clock.RemainingMilliseconds,
                    turnSeat = clock.TurnSeat?.Value ?? 0, turnHandVersion = clock.TurnHandVersion };
            }
            if (source.OwnCardChange != null)
            {
                var own = source.OwnCardChange;
                packet.ownCardChange = new HoldemOwnCardChangePacket { handId = own.HandId.ToString("N"),
                    dealWindowId = own.DealWindowId.ToString("N"), street = (int)own.Street,
                    boardIndex = own.BoardIndex, card = own.Card.Id };
            }
            if (source.OwnHandCardChangeCount > 0)
            {
                packet.ownHandCardChanges = new HoldemOwnCardChangePacket[source.OwnHandCardChangeCount];
                for (int i = 0; i < packet.ownHandCardChanges.Length; i++)
                {
                    var own = source.GetOwnHandCardChange(i);
                    packet.ownHandCardChanges[i] = new HoldemOwnCardChangePacket { handId = own.HandId.ToString("N"),
                        dealWindowId = own.DealWindowId.ToString("N"), street = (int)own.Street,
                        boardIndex = own.BoardIndex, card = own.Card.Id };
                }
            }
            if (source.History != null)
            {
                var history = source.History;
                packet.history = new HoldemHistoryPacket { omittedCount = history.OmittedCount,
                    entries = new HoldemHistoryEntryPacket[history.Count] };
                for (int i = 0; i < history.Count; i++)
                {
                    var entry = history.GetEntry(i);
                    packet.history.entries[i] = new HoldemHistoryEntryPacket { kind = (int)entry.Kind, street = (int)entry.Street,
                        seat = entry.Seat.Value, hasAction = entry.Action.HasValue, action = (int)(entry.Action ?? 0),
                        amount = entry.Amount, streetTotal = entry.StreetTotal, allIn = entry.IsAllIn };
                }
            }
            if (source.Rules != null)
            {
                var rules = source.Rules;
                packet.rules = new HoldemRoomRulesPacket { seatCapacity = rules.SeatCapacity, startingStack = rules.StartingStack,
                    bettingPolicy = (int)rules.BettingPolicy, ante = rules.Ante, fixedBetIncrement = rules.FixedBetIncrement,
                    anteSteps = rules.CopyAnteSteps(), incrementFollowsAnte = rules.IncrementFollowsAnte,
                    smallBlind = rules.SmallBlind, bigBlind = rules.BigBlind, waitsForHostDeal = rules.WaitsForHostDeal,
                    pausesAfterReveal = rules.PausesAfterReveal, receivesUtterances = rules.ReceivesUtterances,
                    publishesUtterances = rules.PublishesUtterances, accusationsEnabled = rules.AccusationsEnabled,
                    accusationMode = (int)rules.AccusationMode };
                if (rules.SequentialRules != null)
                {
                    var s = rules.SequentialRules;
                    packet.rules.hasSequentialRules = true;
                    packet.rules.sequentialRules = new SequentialBettingRulesPacket {
                        minimumBet = s.MinimumBet, maximumOpeningBet = s.MaximumOpeningBet, allowCheck = s.AllowCheck,
                        firstSeat = (int)s.FirstSeat, allInCap = (int)s.AllInCap, anteShortfall = (int)s.AnteShortfall,
                        fineShortfall = (int)s.FineShortfall, accusationScope = (int)s.AccusationScope,
                        baitFineLimit = (int)s.BaitFineLimit };
                }
                if (rules.SequentialPacing != null)
                {
                    var p = rules.SequentialPacing;
                    packet.rules.hasSequentialPacing = true;
                    packet.rules.sequentialPacing = new SequentialPacingPacket {
                        preflopMilliseconds = p.PreflopMilliseconds, flopMilliseconds = p.FlopMilliseconds,
                        turnMilliseconds = p.TurnMilliseconds, riverMilliseconds = p.RiverMilliseconds,
                        revealMilliseconds = p.RevealMilliseconds, origin = (int)p.Origin,
                        neutralDealerPreview = p.NeutralDealerPreview, actionTurnMilliseconds = p.ActionTurnMilliseconds };
                }
            }
            for (int i = 0; i < packet.members.Length; i++)
            {
                var m = source.GetMember(i);
                packet.members[i] = new HoldemRoomMemberPacket
                { seat = m.Seat.Value, name = m.Name, connected = m.Connected, ready = m.Ready, host = m.IsHost,
                    rematchReady = m.RematchReady, rematchRevision = m.RematchRevision };
            }
            if (source.Game != null)
            {
                packet.game = Game(source.Game);
                packet.game.hasStreetActions = true;
                packet.game.streetActions = new HoldemActionPacket[source.StreetActionCount];
                for (int i = 0; i < source.StreetActionCount; i++) packet.game.streetActions[i] = Action(source.GetStreetAction(i));
            }
            if (source.LastAction != null) packet.lastAction = Action(source.LastAction);
            if (source.TurnAccusation != null)
            {
                var accusation = source.TurnAccusation;
                packet.hasTurnAccusation = true;
                packet.turnAccusation = new HoldemTurnAccusationPacket { handId = accusation.HandId.ToString("N"),
                    street = (int)accusation.Street, accuser = accusation.Accuser.Value, target = accusation.Target.Value,
                    caught = accusation.Caught };
            }
            if (source.Chat != null)
            {
                var chat = source.Chat;
                packet.hasChat = true;
                packet.chat = new HoldemChatPacket { maximumTextLength = chat.MaximumTextLength,
                    entries = new HoldemChatEntryPacket[chat.Count] };
                for (int i = 0; i < chat.Count; i++)
                {
                    var entry = chat.GetEntry(i);
                    packet.chat.entries[i] = new HoldemChatEntryPacket
                    { sequence = entry.Sequence, seat = entry.Speaker.Value, text = entry.Text };
                }
            }
            if (source.OwnUtterances != null)
            {
                packet.ownUtterances = OwnSpeech(source.OwnUtterances);
            }
            if (source.PublicUtterances != null) packet.publicUtterances = RawSpeech(source.PublicUtterances);
            if (source.EliminatedSpeech != null)
            {
                packet.hasEliminatedSpeech = true;
                packet.eliminatedSpeech = new HoldemEliminatedSpeechPacket
                {
                    revision = source.EliminatedSpeech.Revision,
                    own = OwnSpeech(source.EliminatedSpeech.Own), messages = RawSpeech(source.EliminatedSpeech.Messages)
                };
            }
            return packet;
        }

        private static HoldemOwnUtterancePacket OwnSpeech(HoldemUtteranceView input)
        {
            var result = new HoldemOwnUtterancePacket
                {
                    windowId = input.WindowId.ToString("N"), canSubmit = input.CanSubmit,
                    canDesignate = input.CanDesignate,
                    backlogged = input.IsBacklogged,
                    unlimitedMessages = input.UnlimitedMessages,
                    maximumTextLength = input.MaximumTextLength, remaining = input.Remaining,
                    entries = new HoldemOwnUtteranceEntryPacket[input.Count]
                };
                for (int i = 0; i < input.Count; i++)
                {
                    var entry = input.GetEntry(i);
                    result.entries[i] = new HoldemOwnUtteranceEntryPacket
                    { commandId = entry.CommandId.ToString("N"), windowId = entry.WindowId.ToString("N"),
                        text = entry.Text, street = (int)entry.Street, designationId = entry.DesignationId.ToString("N") };
                    result.entries[i].designationEligible = entry.DesignationEligible;
                    if (entry.DesignationId != Guid.Empty) result.designationRevision++;
                }
            return result;
        }

        private static HoldemPublicUtterancePacket RawSpeech(HoldemPublicUtterances remarks)
        {
                var result = new HoldemPublicUtterancePacket
                { hasHistoryOrder = remarks.HasHistoryOrder, entries = new HoldemPublicUtteranceEntryPacket[remarks.Count] };
                for (int i = 0; i < remarks.Count; i++)
                {
                    var entry = remarks.GetEntry(i);
                    result.entries[i] = new HoldemPublicUtteranceEntryPacket
                    { seat = entry.Speaker.Value, street = (int)entry.Street, text = entry.Text,
                        historyPosition = entry.HistoryPosition ?? 0 };
                }
            return result;
        }

        private static HoldemActionPacket Action(HoldemActionNotice a) => new HoldemActionPacket
        { seat = a.Seat.Value, kind = (int)a.Kind, street = (int)a.Street, paid = a.Paid, target = a.Target };

        private static HoldemGamePacket Game(HoldemSnapshot source)
        {
            var packet = new HoldemGamePacket
            {
                handId = source.HandId.ToString("N"), version = source.SessionVersion, handVersion = source.HandVersion,
                handNumber = source.HandNumber, roundInMatch = source.RoundInMatch,
                bettingPolicy = (int)source.BettingPolicy, postedAnte = source.PostedAnte,
                pot = source.PotAmount, street = (int)source.Street, settlementState = (int)source.SettlementState,
                button = source.ButtonSeat.Value, smallBlind = source.SmallBlindSeat.Value, bigBlind = source.BigBlindSeat.Value,
                currentSeat = source.CurrentSeat?.Value ?? 0, sessionWinner = source.SessionWinnerSeat?.Value ?? 0,
                canContinue = source.CanContinue, isOver = source.IsOver, revealPending = source.IsRevealPending,
                finalConversationPending = source.IsFinalConversationPending,
                dealPending = source.IsDealPending, dealWindowId = source.PendingDeal?.WindowId.ToString("N") ?? "",
                dealStreet = source.PendingDeal == null ? 0 : (int)source.PendingDeal.Street,
                hasLegal = source.LegalActions != null, hasResult = source.Result != null,
                canAccuseOnTurn = source.CanAccuseOnTurn,
                board = Cards(source.BoardCount, source.GetBoardCard), seats = new HoldemSeatPacket[source.SeatCount],
                accusationOutcomes = new HoldemAccusationOutcomePacket[source.AccusationOutcomeCount]
            };
            for (int i = 0; i < source.AccusationOutcomeCount; i++)
            {
                var outcome = source.GetAccusationOutcome(i);
                packet.accusationOutcomes[i] = new HoldemAccusationOutcomePacket { windowId = outcome.WindowId.ToString("N"),
                    street = (int)outcome.Street, accuser = outcome.Accuser.Value, target = outcome.Target.Value,
                    hit = outcome.Hit, fine = outcome.Fine, potAward = outcome.PotAward };
            }
            if (source.FixedBetting != null)
            {
                var input = source.FixedBetting;
                packet.hasFixedBetting = true;
                packet.fixedBetting = new FixedBettingPacket { windowId = input.WindowId.ToString("N"),
                    street = (int)input.Street, phase = (int)input.Phase, increment = input.Increment,
                    ownChoiceRevision = input.OwnChoiceRevision, ownProposal = (int)(input.OwnProposal ?? 0),
                    ownResponse = (int)(input.OwnResponse ?? 0), canPropose = input.CanPropose,
                    canRespond = input.CanRespond, raisers = new int[input.RaiserCount] };
                for (int i = 0; i < input.RaiserCount; i++) packet.fixedBetting.raisers[i] = input.GetRaiser(i).Value;
            }
            if (source.Accusations != null)
            {
                var a = source.Accusations;
                packet.hasAccusations = true;
                packet.accusations = new HoldemAccusationPacket { windowId = a.WindowId.ToString("N"),
                    phase = (int)a.Phase, eligibleCount = a.EligibleCount, responseCount = a.ResponseCount,
                    canRespond = a.CanRespond, hasResponded = a.HasResponded,
                    hasOwnTarget = a.OwnTarget.HasValue, ownTarget = a.OwnTarget?.Value ?? 0,
                    hasOwnVerdict = a.OwnVerdict.HasValue, ownVerdict = a.OwnVerdict ?? false,
                    ownChoiceRevision = a.OwnChoiceRevision, ownAdmitted = a.OwnAdmitted,
                    targets = new int[a.TargetCount] };
                for (int i = 0; i < a.TargetCount; i++) packet.accusations.targets[i] = a.GetTargetAt(i).Value;
            }
            for (int i = 0; i < packet.seats.Length; i++)
            {
                var s = source.GetSeatAt(i);
                bool publicBest = s.RevealedHandValue.HasValue && s.RevealedBestCardCount == HandEvaluator.HandSize;
                packet.seats[i] = new HoldemSeatPacket
                {
                    seat = s.Seat.Value, tableIndex = s.TableIndex, status = (int)s.Status, viewer = s.IsViewer,
                    dealtIn = s.WasDealtIn, button = s.IsButton, smallBlind = s.IsSmallBlind, bigBlind = s.IsBigBlind,
                    currentActor = s.IsCurrentActor, stack = s.Stack, committed = s.Committed,
                    streetContribution = s.StreetContribution, awarded = s.Awarded,
                    visibleCards = Cards(s.VisibleHoleCardCount, s.GetVisibleHoleCard),
                    revealedBestCards = Cards(publicBest ? HandEvaluator.HandSize : 0, s.GetRevealedBestCard)
                };
            }
            if (source.LegalActions != null)
            {
                var l = source.LegalActions;
                packet.legal = new HoldemLegalPacket
                { fold = l.CanFold, check = l.CanCheck, call = l.CanCall, bet = l.CanBet, raise = l.CanRaise,
                    callAmount = l.CallAmount, minimumTarget = l.MinimumAggressiveTarget ?? 0,
                    maximumTarget = l.MaximumAggressiveTarget ?? 0,
                    hasAllInTarget = l.AllInTarget.HasValue, allInTarget = l.AllInTarget ?? 0 };
            }
            if (source.Result != null) packet.result = Result(source.Result);
            return packet;
        }

        private static HoldemResultPacket Result(HoldemPublicResult source)
        {
            var packet = new HoldemResultPacket { kind = (int)source.Kind, winnerSeat = source.WinnerSeat?.Value ?? 0,
                foldedSeat = source.FoldedSeat?.Value ?? 0, accusedSeat = source.AccusedSeat?.Value ?? 0, pot = source.PotAmount, pots = new HoldemPotPacket[source.PotCount] };
            for (int i = 0; i < packet.pots.Length; i++)
            {
                var p = source.GetPot(i);
                var mapped = new HoldemPotPacket { lowerBound = p.LowerBound, contributionCap = p.ContributionCap,
                    amount = p.Amount, eligibleSeats = new int[p.EligibleSeatCount], payouts = new HoldemPayoutPacket[p.PayoutCount] };
                for (int j = 0; j < mapped.eligibleSeats.Length; j++) mapped.eligibleSeats[j] = p.GetEligibleSeat(j).Value;
                for (int j = 0; j < mapped.payouts.Length; j++)
                {
                    var payout = p.GetPayout(j);
                    mapped.payouts[j] = new HoldemPayoutPacket
                    { seat = payout.Seat.Value, amount = payout.Amount, includesOddChip = payout.HasOddChip };
                }
                packet.pots[i] = mapped;
            }
            return packet;
        }

        private static int[] Cards(int count, Func<int, Card> read)
        {
            var cards = new int[count];
            for (int i = 0; i < count; i++) cards[i] = read(i).Id;
            return cards;
        }
    }
}
