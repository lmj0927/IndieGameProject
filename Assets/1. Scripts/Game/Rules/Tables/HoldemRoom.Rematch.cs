using System;
using Poker.Foundation;

namespace Poker.Application
{
    public sealed partial class HoldemRoom
    {
        private HoldemStartCommand lastRematchCommand;
        private Guid lastRematchBasisHand;
        private HoldemRoomReceipt lastRematchReceipt;

        // Consent has its own per-seat revision: a delayed retry cannot re-enable a cancelled vote,
        // nor can reconnect replay consent that the disconnect invalidated.
        public HoldemRoomError SetRematchReady(Guid connection, Guid completedHandId, long expectedVersion,
            long consentRevision, bool ready)
        {
            lock (gate)
            {
                var error = FindConnected(connection, out var member);
                if (error != HoldemRoomError.None) return error;
                if (!session.IsOver) return HoldemRoomError.MatchNotOver;
                if (session.CurrentHandId != completedHandId || session.Version != expectedVersion)
                    return HoldemRoomError.StaleRematchConsent;
                if (consentRevision < 0) return HoldemRoomError.StaleRematchConsent;
                if (member.LastRematchBasisRevision == consentRevision && member.LastRematchReady == ready)
                    return HoldemRoomError.None;
                if (ready && !SupportsRematch()) return HoldemRoomError.ClientUpgradeRequired;
                if (consentRevision < 0 || consentRevision != member.RematchRevision)
                    return HoldemRoomError.StaleRematchConsent;
                long nextRevision = checked(member.RematchRevision + 1);
                member.LastRematchBasisRevision = consentRevision; member.LastRematchReady = ready;
                member.RematchRevision = nextRevision; member.RematchReady = ready; revision++;
                return HoldemRoomError.None;
            }
        }

        public HoldemRoomReceipt RestartMatch(Guid connection, HoldemStartCommand command, Guid completedHandId)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (completedHandId == Guid.Empty) throw new ArgumentException("A completed hand ID is required.", nameof(completedHandId));
            lock (gate)
            {
                var error = FindConnected(connection, out var member);
                if (error != HoldemRoomError.None) return Reject(error);
                if (member != members[0]) return Reject(HoldemRoomError.HostOnly);
                if (lastRematchCommand?.CommandId == command.CommandId)
                {
                    bool same = lastRematchBasisHand == completedHandId && lastRematchCommand.SessionId == command.SessionId
                        && lastRematchCommand.HandId == command.HandId && lastRematchCommand.ExpectedVersion == command.ExpectedVersion;
                    return same ? lastRematchReceipt : Reject(HoldemRoomError.CommandConflict);
                }
                if (!session.IsOver) return Reject(HoldemRoomError.MatchNotOver);
                if (!SupportsRematch()) return Reject(HoldemRoomError.ClientUpgradeRequired);
                if (members.Count != SeatCapacity) return Reject(HoldemRoomError.WaitingForPlayers);
                foreach (var participant in members)
                {
                    if (!participant.Connected) return Reject(HoldemRoomError.Paused);
                    if (!participant.RematchReady) return Reject(HoldemRoomError.PlayersNotReady);
                }
                // Preserve closed, unacknowledged dealer batches before starting a new hand.
                SynchronizeUtterances();
                var receipt = Wrap(session.RestartMatch(command, completedHandId));
                if (!receipt.Accepted) return receipt;
                lastRematchCommand = command; lastRematchBasisHand = completedHandId; lastRematchReceipt = receipt;
                foreach (var participant in members) ClearRematchConsent(participant);
                lastAction = null; Array.Clear(seatActions, 0, seatActions.Length);
                StartHistory(null); SynchronizeUtterances(); revision++;
                return receipt;
            }
        }

        private bool SupportsRematch()
        {
            foreach (var member in members) if (!member.SupportsRematch) return false;
            return true;
        }

        private static void ClearRematchConsent(Member member)
        {
            member.RematchReady = false;
            member.RematchRevision = checked(member.RematchRevision + 1);
            member.LastRematchBasisRevision = -1;
        }
    }
}
