using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Transport
{
    /// <summary>
    /// Transport-neutral, host-owned command endpoint. A transport allocates connection IDs, binds its
    /// authenticated sender to one ID, and delivers each returned response or projection only to that sender.
    /// This class never accepts a claimed seat or host role from a request.
    /// </summary>
    public sealed class HoldemHostCommandBridge : IDisposable, IHoldemSocialHostPort, IHoldemProgressClockHostPort, IDealerFeatureSourcePort,
        ISequentialProgressHostPort, IHoldemEliminatedSpeechHostPort, IHoldemOpenUtterancePort
    {
        private const int MaximumAdmissionIdentities = 256;
        private readonly object gate = new object();
        private readonly HoldemRoom room;
        private readonly Guid sessionId;
        private readonly string hostKeyHash;
        private readonly bool publishesUtterances;
        private readonly bool accusationsEnabled;
        private readonly bool fixedBettingEnabled;
        private readonly bool turnAccusationEnabled;
        private readonly bool anteProgressionEnabled;
        private readonly HoldemAccusationMode accusationMode;
        private readonly bool sequentialPacingEnabled;
        private readonly bool actionTurnPacingEnabled;
        private readonly bool spectatorDealerRequestsEnabled;
        private readonly Dictionary<Guid, Binding> bindings = new Dictionary<Guid, Binding>();
        private readonly Dictionary<string, Admission> admissions = new Dictionary<string, Admission>();
        private bool disposed;

        private sealed class Binding
        {
            public bool Admitted;
            public int Seat;
            public bool IsHost;
            public string KeyHash;
            public Guid LeftCommandId;
            public string SpeechHand;
            public int SpeechCursor;
            public string EliminatedSpeechHand;
            public int EliminatedSpeechCursor;
        }

        private sealed class Admission
        {
            public string Name, ResumeToken;
            public Guid Connection;
            public int Seat;
            public Guid LeftCommandId;
        }

        public HoldemHostCommandBridge(HoldemClientIdentity hostIdentity, HoldemConfig config, SeatId initialButton,
            IRandomSource deckRandom, HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority,
            HoldemUtterancePolicy utterancePolicy = null, int seatCapacity = HoldemRoom.Capacity,
            HoldemAccusationEvidenceScope? accusationEvidenceScope = null, bool chatEnabled = false, SequentialPacingProfile sequentialPacing = null)
        {
            if (hostIdentity == null) throw new ArgumentNullException(nameof(hostIdentity));
            if (config == null) throw new ArgumentNullException(nameof(config));
            ValidateUtterancePolicy(utterancePolicy, seatCapacity, config.BettingPolicy);
            sessionId = Guid.NewGuid();
            Guid reservedHost = Guid.NewGuid();
            room = HoldemRoom.Create(sessionId, reservedHost, hostIdentity.Name, config, initialButton, deckRandom,
                out var hostAdmission, oddChipRule, utterancePolicy, seatCapacity, accusationEvidenceScope, chatEnabled, sequentialPacing);
            var hostView = room.Read(reservedHost);
            publishesUtterances = hostView.Rules.PublishesUtterances;
            accusationsEnabled = hostView.Rules.AccusationsEnabled;
            fixedBettingEnabled = hostView.Rules.BettingPolicy == HoldemBettingPolicy.SharedAnteFixed;
            turnAccusationEnabled = hostView.Rules.BettingPolicy == HoldemBettingPolicy.SharedAnteSequential;
            anteProgressionEnabled = hostView.Rules.CopyAnteSteps().Length > 0;
            accusationMode = hostView.Rules.AccusationMode;
            sequentialPacingEnabled = hostView.Rules.SequentialPacing != null;
            spectatorDealerRequestsEnabled = config.BettingPolicy == HoldemBettingPolicy.NoLimit
                && utterancePolicy != null && (utterancePolicy.AllowedSeats & HoldemUtteranceSeats.Busted) != 0;
            actionTurnPacingEnabled = hostView.Rules.SequentialPacing?.Origin == SequentialConversationClockOrigin.ActionTurn;
            room.Disconnect(reservedHost);
            hostKeyHash = KeyHash(hostIdentity.AdmissionKey);
            admissions.Add(hostKeyHash, new Admission
            {
                Name = hostIdentity.Name,
                Seat = hostAdmission.Seat.Value,
                ResumeToken = hostAdmission.ResumeToken,
                Connection = reservedHost
            });
        }

        public Guid SessionId => sessionId;
        public int SeatCapacity => room.SeatCapacity;

        private static void ValidateUtterancePolicy(HoldemUtterancePolicy policy, int seatCapacity, HoldemBettingPolicy bettingPolicy)
        {
            if (policy == null) return;
            HoldemRoomRules.ValidateSeatCapacity(seatCapacity);
            if (bettingPolicy == HoldemBettingPolicy.SharedAnteSequential)
            {
                // A single maximum-length own/public pair plus private patches must fit a page.
                if (policy.MaximumTextLength > 2048)
                    throw new ArgumentException("Sequential speech exceeds the transport's per-message bound.", nameof(policy));
                return;
            }
            // Both LAN and Fusion use this projection and the same bounded frame codec.
            // Shared play reserves three request and four ordinary-chat allowances per hand.
            long allowances = bettingPolicy != HoldemBettingPolicy.NoLimit ? 7 : 4;
            long publicCopies = policy.Visibility == HoldemUtteranceVisibility.PublicRaw ? seatCapacity : 0;
            long ownEntryBytes = 360 + 6L * policy.MaximumTextLength;
            long publicEntryBytes = 160 + 6L * policy.MaximumTextLength;
            long historyBytes = checked(512 + allowances * policy.MaximumPerSeatPerStreet * (ownEntryBytes + publicCopies * publicEntryBytes));
            // At settlement all three selected source sentences share this frame with history.
            long selectionBytes = policy.BatchRetention == HoldemUtteranceBatchRetention.UntilHostAcknowledges
                ? 3 * (256 + 6L * policy.MaximumTextLength) : 0;
            // Leave the existing 24 KiB reserve for cards, results and response metadata unchanged.
            if (historyBytes + selectionBytes > HoldemFrameCodec.MaximumBytes - 24 * 1024)
                throw new ArgumentException("Speech history exceeds this transport's bounded frame capacity.", nameof(policy));
        }

        /// <summary>
        /// Execute one already-decoded request for the transport-authenticated sender. The caller must deliver
        /// the response and optional projection only to that sender. An invalid protocol or command ID is a
        /// fatal envelope so transports can apply the same close policy before any room operation.
        /// </summary>
        public HoldemHostDispatchResult Dispatch(Guid senderConnection, HoldemWireRequest request)
        {
            if (senderConnection == Guid.Empty)
                throw new ArgumentException("A transport connection ID is required.", nameof(senderConnection));
            if (request == null || request.protocol != HoldemRoomPacketMapper.ProtocolVersion
                || !TryId(request.id, out _))
                return new HoldemHostDispatchResult(null, null, true);
            var response = DispatchResponse(senderConnection, request);
            HoldemRoomPacket projection = null;
            if (response.type == "admitted" && response.accepted)
                TryCreateProjection(senderConnection, out projection);
            return new HoldemHostDispatchResult(response, projection, false);
        }

        private HoldemWireResponse DispatchResponse(Guid senderConnection, HoldemWireRequest request)
        {
            if (senderConnection == Guid.Empty) throw new ArgumentException("A transport connection ID is required.", nameof(senderConnection));
            lock (gate)
            {
                ThrowIfDisposed();
                if (request == null || request.protocol != HoldemRoomPacketMapper.ProtocolVersion || !TryId(request.id, out var id))
                    return Error(request?.id, "MalformedRequest");
                try
                {
                    if (request.type == "admit") return Admit(senderConnection, request);
                    bindings.TryGetValue(senderConnection, out var binding);
                    if (binding != null && binding.LeftCommandId != Guid.Empty)
                    {
                        return request.type == "leave" && id == binding.LeftCommandId
                            && request.sessionId == sessionId.ToString("N")
                            ? Reply(request.id, HoldemRoomError.None, null)
                            : Error(request.id, "LobbyLeft");
                    }
                    if (binding == null || !binding.Admitted) return Error(request.id, "NotAdmitted");
                    if (!TryId(request.sessionId, out var requestedSession) || requestedSession != sessionId)
                        return Error(request.id, "SessionGone");
                    if (request.type == "ping") return Response("pong", request.id);
                    if (request.type == "sync")
                    {
                        var view = room.Read(senderConnection);
                        if (turnAccusationEnabled && view.Game != null)
                        {
                            if (request.speechCursor < 0) return Error(request.id, "MalformedRequest");
                            int count = view.PublicUtterances?.Count ?? 0;
                            if (request.speechHandId == view.Game.HandId.ToString("N"))
                            {
                                if (request.speechCursor > count) return Error(request.id, "MalformedRequest");
                                binding.SpeechHand = request.speechHandId; binding.SpeechCursor = request.speechCursor;
                            }
                            else { binding.SpeechHand = view.Game.HandId.ToString("N"); binding.SpeechCursor = 0; }
                            if (view.EliminatedSpeech != null)
                            {
                                int privateCount = view.EliminatedSpeech.Messages.Count;
                                if (request.eliminatedSpeechCursor < 0)
                                    return Error(request.id, "MalformedRequest");
                                binding.EliminatedSpeechHand = view.Game.HandId.ToString("N");
                                if (request.eliminatedSpeechHandId == binding.EliminatedSpeechHand
                                    && request.eliminatedSpeechCursor > privateCount)
                                    return Error(request.id, "MalformedRequest");
                                binding.EliminatedSpeechCursor = request.eliminatedSpeechHandId == binding.EliminatedSpeechHand
                                    ? request.eliminatedSpeechCursor : 0;
                            }
                            else
                            {
                                // A rematch can revive this seat while its last sync still names the
                                // previous hand's private cursor. Retire it without reading that history.
                                if (request.eliminatedSpeechCursor < 0
                                    || (request.eliminatedSpeechCursor != 0
                                        && (string.IsNullOrEmpty(request.eliminatedSpeechHandId)
                                            || request.eliminatedSpeechHandId == view.Game.HandId.ToString("N"))))
                                    return Error(request.id, "MalformedRequest");
                                binding.EliminatedSpeechHand = null; binding.EliminatedSpeechCursor = 0;
                            }
                        }
                        return State(request.id, view, binding);
                    }
                    if (request.type == "leave")
                    {
                        var error = room.LeaveLobby(senderConnection);
                        if (error == HoldemRoomError.None)
                        {
                            var retired = admissions[binding.KeyHash];
                            retired.ResumeToken = null; retired.Name = null; retired.Seat = 0; retired.Connection = Guid.Empty;
                            retired.LeftCommandId = id;
                            binding.LeftCommandId = id; binding.Admitted = false;
                        }
                        return Reply(request.id, error, null);
                    }
                    if (request.type == "ready")
                        return Reply(request.id, room.SetReady(senderConnection, request.ready), null);
                    if (request.type == "utterance" || request.type == "designate-utterance"
                        || request.type == "eliminated-utterance" || request.type == "designate-eliminated-utterance")
                    {
                        if (!TryId(request.handId, out var speechHand) || !TryId(request.windowId, out var speechWindow))
                            return Error(request.id, "MalformedRequest");
                        Guid selectedMessage = Guid.Empty;
                        bool privateChannel = request.type == "eliminated-utterance" || request.type == "designate-eliminated-utterance";
                        bool designation = request.type == "designate-utterance" || request.type == "designate-eliminated-utterance";
                        if (designation && (!TryId(request.designatedMessageId, out selectedMessage)
                            || !string.IsNullOrEmpty(request.text))) return Error(request.id, "MalformedRequest");
                        if (!designation && !string.IsNullOrEmpty(request.designatedMessageId))
                            return Error(request.id, "MalformedRequest");
                        var speech = new HoldemRoomUtterance(sessionId,
                            speechHand, speechWindow, id, (HoldemStreet)request.street, request.text ?? "", selectedMessage);
                        var speechResult = privateChannel ? room.SubmitEliminatedSpeech(senderConnection, speech)
                            : room.SubmitUtterance(senderConnection, speech);
                        var response = Response("utterance-receipt", request.id);
                        response.accepted = speechResult.Accepted; response.error = speechResult.Error.ToString();
                        response.hasUtteranceReceipt = speechResult.Utterance != null;
                        response.utteranceError = speechResult.Utterance?.Error.ToString();
                        return response;
                    }
                    if (request.type == "chat")
                    {
                        if (request.text == null) return Error(request.id, "MalformedRequest");
                        HoldemRoomChat chat;
                        if (request.toDealer)
                        {
                            if (!TryId(request.handId, out var chatHand) || !TryId(request.windowId, out var chatWindow))
                                return Error(request.id, "MalformedRequest");
                            chat = new HoldemRoomChat(id, request.text, sessionId, chatHand, chatWindow, (HoldemStreet)request.street);
                        }
                        else chat = new HoldemRoomChat(id, request.text);
                        var chatResult = room.SubmitChat(senderConnection, chat);
                        var response = Response("chat-receipt", request.id);
                        response.accepted = chatResult.Accepted; response.error = chatResult.Error.ToString();
                        response.chatError = chatResult.Chat.ToString();
                        response.hasUtteranceReceipt = chatResult.DealerRequest != null;
                        response.utteranceError = chatResult.DealerRequest?.Error.ToString();
                        return response;
                    }
                    if (request.version < 0 || !TryId(request.handId, out var hand))
                        return Error(request.id, "MalformedRequest");
                    if (request.type == "rematch-ready")
                        return Reply(request.id, room.SetRematchReady(senderConnection, hand, request.version,
                            request.rematchRevision, request.ready), null);

                    HoldemRoomReceipt result;
                    switch (request.type)
                    {
                        case "turn-accusation":
                            if (request.accusationTarget < 1 || request.accusationTarget > room.SeatCapacity
                                || !string.IsNullOrEmpty(request.windowId) || request.choiceRevision != 0)
                                return Error(request.id, "MalformedRequest");
                            result = room.ChatEnabled
                                ? room.SubmitLegacyTurnAccusation(senderConnection, new HoldemRoomLegacyTurnAccusation(sessionId, hand, id, request.version, new SeatId(request.accusationTarget)))
                                : room.SubmitTurnAccusation(senderConnection, new HoldemRoomTurnAccusation(sessionId, hand, id, request.version, (HoldemStreet)request.street, new SeatId(request.accusationTarget)));
                            break;
                        case "fixed-choice":
                            if (!TryId(request.windowId, out var choiceWindow))
                                return Error(request.id, "MalformedRequest");
                            result = room.SubmitFixedBettingChoice(senderConnection, new FixedBettingRoomInput(
                                sessionId, hand, choiceWindow, id, request.version, (HoldemStreet)request.street,
                                request.choiceRevision, (FixedBettingPhase)request.fixedPhase, request.fixedChoice));
                            break;
                        case "fixed-seal":
                            if (!TryId(request.windowId, out var sealWindow))
                                return Error(request.id, "MalformedRequest");
                            result = room.SealFixedBetting(senderConnection, new FixedBettingHostCommand(
                                sessionId, hand, sealWindow, id, request.version, (HoldemStreet)request.street,
                                (FixedBettingPhase)request.fixedPhase));
                            break;
                        case "accusation":
                            if (!TryId(request.windowId, out var accusationWindow)
                                || request.accusationTarget < 0 || request.accusationTarget > room.SeatCapacity
                                || request.choiceRevision < 0
                                || accusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose && request.choiceRevision == 0)
                                return Error(request.id, "MalformedRequest");
                            result = room.SubmitAccusation(senderConnection, new HoldemRoomAccusation(sessionId, hand,
                                accusationWindow, id, request.version, (HoldemStreet)request.street,
                                request.accusationTarget == 0 ? (SeatId?)null : new SeatId(request.accusationTarget),
                                request.choiceRevision == 0 ? 1 : request.choiceRevision));
                            break;
                        case "act":
                            result = room.Submit(senderConnection, new HoldemRoomAction(sessionId, hand, id,
                                request.version, Action(request)));
                            break;
                        case "start":
                            result = room.StartHand(senderConnection,
                                new HoldemStartCommand(sessionId, hand, id, request.version));
                            break;
                        case "rematch":
                            if (!TryId(request.completedHandId, out var completed))
                                return Error(request.id, "MalformedRequest");
                            result = room.RestartMatch(senderConnection,
                                new HoldemStartCommand(sessionId, hand, id, request.version), completed);
                            break;
                        case "deal":
                            if (!TryId(request.windowId, out var window)) return Error(request.id, "MalformedRequest");
                            result = room.DealUnchanged(senderConnection, new HoldemDealCommand(sessionId, hand,
                                window, id, request.version, (HoldemStreet)request.street));
                            break;
                        case "reveal":
                            result = room.ResumeAfterReveal(senderConnection, new HoldemRevealCommand(sessionId,
                                hand, id, request.version, (HoldemStreet)request.street));
                            break;
                        case "settle":
                            // The core settlement command does not carry a hand ID; keep this check in the
                            // serialized bridge operation so a transport cannot settle a different hand.
                            if (room.Read(senderConnection).Game?.HandId != hand) return Error(request.id, "WrongHand");
                            result = room.ResolveSettlement(senderConnection, id, request.version,
                                (HoldemOddChipRule)request.oddChipRule);
                            break;
                        default:
                            return Error(request.id, "UnknownMessage");
                    }
                    return Reply(request.id, result.Error, result.Poker);
                }
                catch (ArgumentException)
                {
                    return Error(request.id, "MalformedRequest");
                }
            }
        }

        /// <summary>In-process presentation test only. Check/call each bound seat until one board street is dealt.</summary>
        public bool AdvanceBoardPreview()
        {
            lock (gate)
            {
                if (!TryReadProgress(out var view) || view.Paused || view.Game == null) return false;
                var initial = view.Game;
                if (initial.Result != null || initial.Street >= HoldemStreet.River) return false;
                for (int i = 0; i < SeatCapacity; i++)
                {
                    var game = room.Read(HostConnection).Game;
                    if (game.Street != initial.Street) return true;
                    if (game.CurrentSeat == null) return false;
                    Guid actor = Guid.Empty;
                    foreach (var entry in bindings)
                        if (entry.Value.Admitted && entry.Value.Seat == game.CurrentSeat.Value.Value)
                        { actor = entry.Key; break; }
                    if (actor == Guid.Empty) return false;
                    var legal = room.Read(actor).Game.LegalActions;
                    var action = legal.CanCheck ? BettingAction.Check() : BettingAction.Call();
                    var receipt = room.Submit(actor, new HoldemRoomAction(game.SessionId, game.HandId,
                        Guid.NewGuid(), game.SessionVersion, action));
                    if (!receipt.Accepted) return false;
                }
                return room.Read(HostConnection).Game.Street != initial.Street;
            }
        }

        /// <summary>Create a detached packet for exactly one admitted connection.</summary>
        public bool TryCreateProjection(Guid senderConnection, out HoldemRoomPacket packet,
            long afterRevision = -1, int afterDesignationRevision = -1, long afterEliminatedSpeechRevision = -1)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (!bindings.TryGetValue(senderConnection, out var binding) || !binding.Admitted)
                { packet = null; return false; }
                room.ReadProjectionStamp(senderConnection, out var revision, out var designationRevision, out var eliminatedRevision);
                if (revision == afterRevision && designationRevision == afterDesignationRevision
                    && (afterEliminatedSpeechRevision < 0 || eliminatedRevision == afterEliminatedSpeechRevision))
                { packet = null; return false; }
                packet = CreateProjection(room.Read(senderConnection), binding);
                return true;
            }
        }

        /// <summary>Transport loss only: reserve the seat and pause authority without folding or advancing.</summary>
        public HoldemRoomError Disconnect(Guid senderConnection)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (!bindings.TryGetValue(senderConnection, out var binding)) return HoldemRoomError.UnknownConnection;
                var error = binding.Admitted ? room.Disconnect(senderConnection) : HoldemRoomError.UnknownConnection;
                bindings.Remove(senderConnection);
                return error;
            }
        }

        public bool TryGetBinding(Guid senderConnection, out HoldemHostBindingView binding)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (!bindings.TryGetValue(senderConnection, out var value))
                { binding = null; return false; }
                binding = new HoldemHostBindingView(value.Seat, value.Admitted, value.IsHost, value.LeftCommandId != Guid.Empty);
                return true;
            }
        }

        private HoldemWireResponse Admit(Guid senderConnection, HoldemWireRequest request)
        {
            if (actionTurnPacingEnabled && !request.supportsActionTurnPacing
                || sequentialPacingEnabled && !request.supportsSequentialPacing)
                return Error(request.id, "ClientUpgradeRequired");
            if (room.SeatCapacity == 2 && !request.supportsTwoPlayerRooms)
                return Error(request.id, "ClientUpgradeRequired");
            if (accusationsEnabled && !request.supportsAccusations)
                return Error(request.id, "ClientUpgradeRequired");
            if (fixedBettingEnabled && !request.supportsFixedBetting)
                return Error(request.id, "ClientUpgradeRequired");
            if (turnAccusationEnabled && (!request.supportsTurnAccusation || !request.supportsSpeechPages
                || !request.supportsEliminatedSpeechPages))
                return Error(request.id, "ClientUpgradeRequired");
            if (spectatorDealerRequestsEnabled && !request.supportsSpectatorDealerRequests)
                return Error(request.id, "ClientUpgradeRequired");
            if (anteProgressionEnabled && !request.supportsAnteProgression)
                return Error(request.id, "ClientUpgradeRequired");
            if (room.SeatCapacity == 3 && !request.supportsThreePlayerRooms)
                return Error(request.id, "ClientUpgradeRequired");
            if (publishesUtterances && !request.supportsPublicUtterances)
                return Error(request.id, "ClientUpgradeRequired");
            if (publishesUtterances && !request.supportsMessageDesignation)
                return Error(request.id, "ClientUpgradeRequired");
            if (!string.IsNullOrEmpty(request.sessionId) && request.sessionId != sessionId.ToString("N"))
                return Error(request.id, "SessionGone");
            string key = KeyHash(request.admissionKey);
            if (key == null) return Error(request.id, "InvalidIdentity");
            bindings.TryGetValue(senderConnection, out var binding);
            if (binding != null && binding.Admitted && binding.KeyHash != key)
                return Error(request.id, "IdentityConflict");
            if (admissions.TryGetValue(key, out var cached))
            {
                if (cached.LeftCommandId != Guid.Empty) return Error(request.id, "LobbyLeft");
                if (cached.Name != request.name) return Error(request.id, "IdentityConflict");
                var resumed = room.Reconnect(senderConnection, cached.ResumeToken,
                    request.supportsRematch, request.supportsAccusations, request.supportsFixedBetting, request.supportsTurnAccusation);
                if (!resumed.Accepted) return Reply(request.id, resumed.Error, null);
                cached.Connection = senderConnection;
            }
            else
            {
                // A known session with a missing capability must never silently create a replacement seat.
                if (!string.IsNullOrEmpty(request.sessionId)) return Error(request.id, "InvalidIdentity");
                if (admissions.Count >= MaximumAdmissionIdentities) return Error(request.id, "AdmissionLimit");
                var joined = room.Join(senderConnection, request.name,
                    request.supportsRematch, request.supportsAccusations, request.supportsFixedBetting, request.supportsTurnAccusation);
                if (!joined.Accepted) return Reply(request.id, joined.Error, null);
                cached = new Admission
                {
                    Name = request.name,
                    Seat = joined.Admission.Seat.Value,
                    ResumeToken = joined.Admission.ResumeToken,
                    Connection = senderConnection
                };
                // Store before returning a response: loss of the first admission response remains recoverable.
                admissions.Add(key, cached);
            }
            if (binding == null)
            {
                binding = new Binding();
                bindings.Add(senderConnection, binding);
            }
            binding.Admitted = true; binding.KeyHash = key; binding.Seat = cached.Seat;
            binding.IsHost = key == hostKeyHash; binding.LeftCommandId = Guid.Empty;
            var response = Response("admitted", request.id);
            response.accepted = true; response.seat = cached.Seat;
            return response;
        }

        private static BettingAction Action(HoldemWireRequest request)
        {
            if (request.action <= 2 && request.target != 0)
                throw new ArgumentException("Passive actions have no target.");
            switch (request.action)
            {
                case 0: return BettingAction.Fold();
                case 1: return BettingAction.Check();
                case 2: return BettingAction.Call();
                case 3: return BettingAction.BetTo(request.target);
                case 4: return BettingAction.RaiseTo(request.target);
                default: throw new ArgumentException("Unknown action.");
            }
        }

        private HoldemWireResponse Reply(string id, HoldemRoomError error, HoldemReceipt receipt)
        {
            var response = Response("receipt", id);
            response.accepted = error == HoldemRoomError.None; response.error = error.ToString();
            response.hasReceipt = receipt != null;
            if (receipt != null)
                response.receipt = new HoldemWireReceipt
                {
                    commandId = receipt.CommandId.ToString("N"), handId = receipt.HandId.ToString("N"),
                    accepted = receipt.Accepted, error = receipt.Error.ToString(),
                    hasVersion = receipt.Version.HasValue, version = receipt.Version ?? 0
                };
            return response;
        }

        private HoldemWireResponse Error(string id, string error)
        {
            var response = Response("rejected", id); response.error = error; return response;
        }

        private static HoldemRoomPacket CreateProjection(HoldemRoomView view, Binding binding)
        {
            var packet = HoldemRoomPacketMapper.Create(view);
            int cursor = packet.game?.handId == binding.SpeechHand ? binding.SpeechCursor : 0;
            int privateCursor = packet.game?.handId == binding.EliminatedSpeechHand ? binding.EliminatedSpeechCursor : 0;
            return SpeechHistoryPages.Create(packet, cursor, privateCursor);
        }

        private HoldemWireResponse State(string id, HoldemRoomView view, Binding binding)
        {
            var response = Response("state", id); response.state = CreateProjection(view, binding); return response;
        }

        private HoldemWireResponse Response(string type, string id) => new HoldemWireResponse
        {
            protocol = HoldemRoomPacketMapper.ProtocolVersion,
            type = type,
            id = id,
            sessionId = sessionId.ToString("N")
        };

        private static bool TryId(string value, out Guid id)
        {
            id = Guid.Empty;
            return value != null && value.Length == 32
                && Guid.TryParseExact(value, "N", out id) && id != Guid.Empty;
        }

        private static string KeyHash(string key)
        {
            if (key == null || key.Length != 44) return null;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(key); }
            catch (FormatException) { return null; }
            if (bytes.Length != 32 || Convert.ToBase64String(bytes) != key) return null;
            using (var hash = SHA256.Create())
            {
                string result = Convert.ToBase64String(hash.ComputeHash(bytes));
                Array.Clear(bytes, 0, bytes.Length);
                return result;
            }
        }

        /// <summary>In-process only. Neither closing nor supplied verdicts are accepted from the wire.</summary>
        public HoldemRoomReceipt CloseAccusations(HoldemRoomAccusationClose input)
        {
            lock (gate) { ThrowIfDisposed(); return room.CloseAccusations(HostConnection, input); }
        }

        public HoldemRoomAccusationResolution ResolveAccusations()
        {
            lock (gate) { ThrowIfDisposed(); return room.ResolveAccusations(HostConnection); }
        }

        public HoldemRoomReceipt SealFixedBetting(FixedBettingHostCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate) { ThrowIfDisposed(); return room.SealFixedBetting(HostConnection, command); }
        }

        public bool TryReadProgress(out HoldemRoomView view)
        {
            lock (gate)
            {
                view = null;
                if (disposed || !bindings.TryGetValue(HostConnection, out var host)
                    || !host.Admitted || !host.IsHost) return false;
                view = room.Read(HostConnection); return true;
            }
        }

        public bool PublishProgressClock(HoldemProgressClock sample)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return room.PublishProgressClock(HostConnection, sample) == HoldemRoomError.None;
            }
        }

        public bool TryReadSequentialProgress(out HoldemSnapshot game, out bool paused)
        {
            bool found = TryReadProgress(out var view);
            game = view?.Game; paused = !found || view.Paused; return found;
        }
        public bool TryExpireTurn(Guid handId, long handVersion, SeatId actor)
        {
            lock (gate) { ThrowIfDisposed(); return room.TryExpireTurn(HostConnection, handId, handVersion, actor); }
        }

        public HoldemRoomReceipt ResumeAfterReveal(HoldemRevealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate) { ThrowIfDisposed(); return room.ResumeAfterReveal(HostConnection, command); }
        }

        public HoldemRoomReceipt CloseFinalConversation(FinalConversationCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate) { ThrowIfDisposed(); return room.CloseFinalConversation(HostConnection, command); }
        }

        public bool CloseDealerInput(Guid dealWindowId)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return room.CloseDealerInput(HostConnection, dealWindowId) == HoldemRoomError.None;
            }
        }

        public HoldemRoomReceipt ResolveSettlement(Guid handId, Guid commandId, long expectedVersion)
        {
            if (handId == Guid.Empty || commandId == Guid.Empty) throw new ArgumentException("Hand and command IDs are required.");
            lock (gate)
            {
                ThrowIfDisposed();
                return room.ResolveSettlement(HostConnection, handId, commandId, expectedVersion,
                    HoldemOddChipRule.ClockwiseFromButton);
            }
        }

        public HoldemDealerTurnRead ReadPendingTurn()
        {
            lock (gate) { ThrowIfDisposed(); return room.ReadPendingDealerTurn(HostConnection); }
        }

        public HoldemRoomReceipt DealUnchanged(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate) { ThrowIfDisposed(); return room.DealUnchanged(HostConnection, command); }
        }

        public HoldemRoomReceipt ApplyDealerDeal(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate) { ThrowIfDisposed(); return room.ApplyDealerDeal(HostConnection, command); }
        }

        public IReadOnlyList<HoldemUtteranceBatch> ReadPendingUtterances()
        {
            lock (gate) { ThrowIfDisposed(); return room.ReadClosedDesignatedBatches(HostConnection); }
        }

        public IReadOnlyList<HoldemUtteranceEntry> ReadDesignatedUtterances()
        {
            lock (gate) { ThrowIfDisposed(); return room.ReadDesignatedUtterances(HostConnection); }
        }

        public DealerFeatureInput ReadFeatureInput()
        { lock (gate) { ThrowIfDisposed(); return room.ReadFeatureInput(HostConnection); } }

        public bool AcknowledgeUtteranceBatch(Guid windowId)
        {
            lock (gate) { ThrowIfDisposed(); return room.AcknowledgeUtteranceBatch(HostConnection, windowId); }
        }

        public IReadOnlyList<HoldemUtteranceEntry> ReadOpenUtterances()
        {
            lock (gate) { ThrowIfDisposed(); return room.ReadOpenUtterances(HostConnection); }
        }
        public IReadOnlyList<HoldemUtteranceEntry> ReadEliminatedDesignations()
        { lock (gate) { ThrowIfDisposed(); return room.ReadEliminatedDesignations(HostConnection); } }
        public IReadOnlyList<HoldemUtteranceBatch> ReadClosedEliminatedBatches()
        { lock (gate) { ThrowIfDisposed(); return room.ReadClosedEliminatedBatches(HostConnection); } }
        public bool AcknowledgeEliminatedBatch(Guid windowId)
        { lock (gate) { ThrowIfDisposed(); return room.AcknowledgeEliminatedBatch(HostConnection, windowId); } }

        private Guid HostConnection => admissions[hostKeyHash].Connection;

        internal string HostKeyHashForDiagnostics => hostKeyHash;
        internal IDictionary AdmissionsForDiagnostics => admissions;

        internal bool TryGetTransportBinding(Guid connection, out bool admitted, out int seat,
            out bool host, out string keyHash, out Guid leftCommandId)
        {
            lock (gate)
            {
                if (!bindings.TryGetValue(connection, out var binding))
                {
                    admitted = false; seat = 0; host = false; keyHash = null; leftCommandId = Guid.Empty;
                    return false;
                }
                admitted = binding.Admitted; seat = binding.Seat; host = binding.IsHost;
                keyHash = binding.KeyHash; leftCommandId = binding.LeftCommandId;
                return true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(HoldemHostCommandBridge));
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                bindings.Clear(); admissions.Clear();
            }
        }
    }

    /// <summary>Non-secret transport binding status. Admission keys and resume tokens are never exposed.</summary>
    public sealed class HoldemHostBindingView
    {
        internal HoldemHostBindingView(int seat, bool admitted, bool host, bool lobbyLeft)
        { Seat = seat; IsAdmitted = admitted; IsHost = host; HasLeftLobby = lobbyLeft; }

        public int Seat { get; }
        public bool IsAdmitted { get; }
        public bool IsHost { get; }
        public bool HasLeftLobby { get; }
    }

    /// <summary>Caller-targeted dispatch output. Fatal envelopes have no response and must be closed.</summary>
    public sealed class HoldemHostDispatchResult
    {
        internal HoldemHostDispatchResult(HoldemWireResponse response, HoldemRoomPacket callerProjection,
            bool closeConnection)
        { Response = response; CallerProjection = callerProjection; CloseConnection = closeConnection; }

        public HoldemWireResponse Response { get; }
        public HoldemRoomPacket CallerProjection { get; }
        public bool CloseConnection { get; }
    }
}
