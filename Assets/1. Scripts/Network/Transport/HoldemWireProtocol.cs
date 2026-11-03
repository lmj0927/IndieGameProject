using System;
using System.Security.Cryptography;
using Poker.Application;

namespace Poker.Transport
{
    // No seat, connection ID, or host claim is accepted from a player.
    [Serializable]
    public sealed class HoldemWireRequest
    {
        public int protocol;
        public string type, id, sessionId, handId, windowId;
        public string name, admissionKey, text;
        public string designatedMessageId;
        public long version, target;
        public int action, street, oddChipRule;
        public bool ready;
        // Compatibility declaration, not an authority or identity claim.
        public bool supportsThreePlayerRooms;
        public bool supportsTwoPlayerRooms;
        public bool supportsPublicUtterances;
        public bool supportsMessageDesignation;
        public bool supportsRematch;
        public bool supportsAccusations;
        public bool supportsFixedBetting;
        public bool supportsTurnAccusation;
        public bool supportsSpeechPages;
        public bool supportsEliminatedSpeechPages;
        public bool supportsSequentialPacing;
        public bool supportsActionTurnPacing;
        public string speechHandId;
        public int speechCursor;
        public string eliminatedSpeechHandId;
        public int eliminatedSpeechCursor;
        public bool supportsAnteProgression;
        // A player's edit order and choice, never a claimed seat or adjudication result.
        public long choiceRevision;
        public int fixedPhase, fixedChoice;
        // Accusation target only. Zero means pass; sender identity comes from the admitted connection.
        public int accusationTarget;
        public string completedHandId;
        public long rematchRevision;
        // Chat only: also submit the text to the current speech window. Never echoed in public state.
        public bool toDealer;
    }

    [Serializable]
    public sealed class HoldemWireResponse
    {
        public int protocol;
        public string type, id, sessionId, error;
        public int seat;
        public bool accepted, hasReceipt;
        public HoldemWireReceipt receipt;
        public HoldemRoomPacket state;
        public bool hasUtteranceReceipt;
        public string utteranceError;
        public string chatError;
    }

    [Serializable]
    public sealed class HoldemWireReceipt
    {
        public string commandId, handId, error;
        public long version;
        public bool hasVersion, accepted;
    }

    /// <summary>Private, in-memory seat identity. Keep it through reconnects; never put it in public state or logs.</summary>
    public sealed class HoldemClientIdentity : IHoldemRemoteIdentity
    {
        public string Name { get; }
        internal string AdmissionKey { get; }
        public string SessionId { get; internal set; }
        public int Seat { get; internal set; }

        public HoldemClientIdentity(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 24) throw new ArgumentException("Invalid name.", nameof(name));
            if (!HoldemPlayerText.IsValidSingleLine(name)) throw new ArgumentException("Invalid name.", nameof(name));
            Name = name;
            byte[] secret = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
            AdmissionKey = Convert.ToBase64String(secret);
            Array.Clear(secret, 0, secret.Length);
        }

        public HoldemWireRequest AdmissionRequest() => new HoldemWireRequest
        {
            protocol = HoldemRoomPacketMapper.ProtocolVersion, type = "admit", id = Guid.NewGuid().ToString("N"),
            name = Name, admissionKey = AdmissionKey, sessionId = SessionId ?? "", supportsThreePlayerRooms = true,
            supportsTwoPlayerRooms = true,
            supportsPublicUtterances = true, supportsMessageDesignation = true, supportsRematch = true,
            supportsAccusations = true, supportsFixedBetting = true, supportsAnteProgression = true,
            supportsTurnAccusation = true, supportsSpeechPages = true, supportsEliminatedSpeechPages = true,
            supportsSequentialPacing = true, supportsActionTurnPacing = true
        };

        /// <summary>Adopt only the caller-targeted response for this outstanding request and stable identity.</summary>
        public bool TryConfirmAdmission(HoldemWireResponse response, string expectedRequestId)
        {
            if (response == null || response.protocol != HoldemRoomPacketMapper.ProtocolVersion
                || response.type != "admitted" || !response.accepted || response.id != expectedRequestId
                || response.seat < 1 || response.seat > HoldemRoom.Capacity
                || !Guid.TryParseExact(response.sessionId, "N", out var session) || session == Guid.Empty
                || SessionId != null && SessionId != response.sessionId
                || Seat != 0 && Seat != response.seat)
                return false;
            SessionId = response.sessionId; Seat = response.seat;
            return true;
        }
    }
}
