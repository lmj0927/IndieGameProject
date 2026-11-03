using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public sealed partial class HoldemSession
    {
        private sealed class AcceptedAccusation
        {
            public AcceptedAccusation(HoldemWindowCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemWindowCommand Command { get; }
            public HoldemReceipt Receipt { get; }
            public bool Matches(HoldemWindowCommand other) =>
                Command is HoldemAccusationChoiceCommand choice && other is HoldemAccusationChoiceCommand otherChoice
                    ? choice.HasSamePayload(otherChoice)
                    : Command is HoldemAccusationHostCommand host && other is HoldemAccusationHostCommand otherHost
                        && host.HasSamePayload(otherHost);
        }

        private HoldemAccusationWindow accusations;
        private readonly Dictionary<Guid, AcceptedAccusation> acceptedAccusations = new Dictionary<Guid, AcceptedAccusation>();
        private HoldemAccusationOutcome[] accusationOutcomes = Array.Empty<HoldemAccusationOutcome>();
        private sealed class AcceptedTurnAccusation
        {
            public AcceptedTurnAccusation(TurnAccusationCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public TurnAccusationCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }
        private readonly Dictionary<Guid, AcceptedTurnAccusation> acceptedTurnAccusations = new Dictionary<Guid, AcceptedTurnAccusation>();

        /// <summary>Connection-authenticated first-orbit action; verdict and all consequences commit together.</summary>
        public HoldemReceipt SubmitTurnAccusation(SeatId authorizedSeat, TurnAccusationCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            HoldemReceipt Denied(HoldemCommandError error) => new HoldemReceipt(command.SessionId, command.HandId,
                command.CommandId, command.Seat, null, error);
            if (authorizedSeat != command.Seat || !authorizedSeat.IsValid) return Denied(HoldemCommandError.UnauthorizedSeat);
            if (command.SessionId != SessionId) return Denied(HoldemCommandError.WrongSession);
            if (currentHand == null) return Denied(HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Denied(HoldemCommandError.WrongHand);
            if (processing) return Denied(HoldemCommandError.Busy);
            if (acceptedTurnAccusations.TryGetValue(command.CommandId, out var previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt : Denied(HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId)) return Denied(HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Denied(HoldemCommandError.VersionMismatch);
            if (config.AccusationMode != HoldemAccusationMode.OnTurnFirstOrbit) return Denied(HoldemCommandError.AccusationNotEnabled);
            if (currentHand.IsComplete) return Denied(HoldemCommandError.HandComplete);
            if (command.Street != currentHand.Street) return Denied(HoldemCommandError.WrongRevealWindow);
            if (currentHand.CurrentSeat != command.Seat) return Denied(HoldemCommandError.WrongTurn);
            if (!currentHand.IsFirstOrbitAccusationTurn(command.Seat)) return Denied(HoldemCommandError.AccusationTurnUnavailable);
            HoldemCommandError eligibilityError = currentHand.TurnAccusationEligibilityError(command.Seat);
            if (eligibilityError != HoldemCommandError.None) return Denied(eligibilityError);
            if (command.Seat == command.Target || !currentHand.WasDealtIn(command.Target) || currentHand.IsFolded(command.Target))
                return Denied(HoldemCommandError.InvalidAccusationTarget);
            // Public status only, checked before any hidden dealer evidence. This unresolved path stays blocked.
            if (currentHand.IsAllIn(command.Target)) return Denied(HoldemCommandError.AccusationSettlementRuleRequired);
            HoldemDealRecord currentRecord = null;
            foreach (var record in dealRecords)
                if (record.HandId == command.HandId && record.Street == command.Street) currentRecord = record;
            if (currentRecord == null) return Denied(HoldemCommandError.DealerEvidenceUnavailable);
            bool hit = currentRecord.Mutation?.Speaker == command.Target;
            if (config.SequentialRules.AccusationScope == TurnAccusationScope.WholeHand)
                foreach (var record in dealRecords)
                    if (record.HandId == command.HandId && (int)record.Street <= (int)command.Street && record.Mutation?.Speaker == command.Target) hit = true;

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                bool finePreviouslyPaid = false;
                foreach (var previousOutcome in accusationOutcomes)
                    if (!previousOutcome.Hit && previousOutcome.Target == command.Target && previousOutcome.Fine > 0) finePreviouslyPaid = true;
                HoldemHand candidate = currentHand.ApplyTurnAccusation(command.Seat, command.Target, hit, finePreviouslyPaid, out long fine);
                ChipLedger candidateLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
                var claim = new HoldemAccusationClaim(currentRecord.DealWindowId, command);
                var outcome = new HoldemAccusationOutcome(claim, hit, fine, hit ? candidate.Result.PotAmount : 0);
                var nextOutcomes = new HoldemAccusationOutcome[accusationOutcomes.Length + 1];
                Array.Copy(accusationOutcomes, nextOutcomes, accusationOutcomes.Length);
                nextOutcomes[accusationOutcomes.Length] = outcome;
                HoldemDealWindow nextDeal = DealWindowFor(candidate);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId, command.Seat, nextVersion, HoldemCommandError.None);
                currentHand = candidate; settledLedger = candidateLedger; accusationOutcomes = nextOutcomes;
                pendingDeal = nextDeal; accusations = AccusationsFor(candidate); Version = nextVersion;
                acceptedTurnAccusations.Add(command.CommandId, new AcceptedTurnAccusation(command, receipt));
                usedCommandIds.Add(command.CommandId);
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>Adapter-authenticated seat input. Open-window revisions replace only that seat's private choice.</summary>
        public HoldemReceipt SubmitAccusationChoice(SeatId authorizedSeat, HoldemAccusationChoiceCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat)
                return Reject(command, HoldemCommandError.UnauthorizedSeat, command.Seat);
            HoldemReceipt prior = ValidateIntakeEnvelope(command, command.Seat);
            if (prior != null) return prior;
            if (accusations.Phase != HoldemAccusationPhase.Collecting)
                return Reject(command, HoldemCommandError.AccusationWindowClosed, command.Seat);
            if (!accusations.IsEligible(command.Seat)) return Reject(command, HoldemCommandError.UnauthorizedSeat, command.Seat);
            if (command.Target.HasValue && (command.Target == command.Seat || !accusations.IsEligible(command.Target.Value)))
                return Reject(command, HoldemCommandError.InvalidAccusationTarget, command.Seat);
            if (config.AccusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose
                && command.ChoiceRevision <= accusations.ChoiceRevision(command.Seat))
                return Reject(command, HoldemCommandError.StaleChoice, command.Seat);

            processing = true;
            try { return CommitIntake(command, accusations.Choose(command), command.Seat); }
            finally { processing = false; }
        }

        /// <summary>
        /// Trusted host only. Close freezes the collected choices. The adapter must authenticate the host,
        /// serialize commands and validate verdicts against the actual dealer record before calling this method.
        /// Legacy collection records adapter-validated verdicts only. Single-claim play also validates the
        /// committed causal record in Core; a separate Apply command commits consequences exactly once.
        /// Missing evidence stays pending. Recording a verdict alone never moves chips or folds a seat.
        /// </summary>
        public HoldemReceipt ProcessAccusationHostCommand(HoldemAccusationHostCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            HoldemReceipt prior = ValidateIntakeEnvelope(command, null);
            if (prior != null) return prior;
            HoldemCommandError error = command.Action == HoldemAccusationHostAction.CloseWindow
                ? accusations.CanClose(command.ClaimId) : command.Action == HoldemAccusationHostAction.ApplyConsequences
                    ? accusations.CanApply(command.ClaimId) : accusations.CanRecord(command.ClaimId);
            if (error != HoldemCommandError.None) return Reject(command, error, null);

            if (config.AccusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose
                && command.Action != HoldemAccusationHostAction.CloseWindow)
            {
                var record = FindDealRecord(command.HandId, command.WindowId, command.Street);
                if (record == null) return Reject(command, HoldemCommandError.DealerEvidenceUnavailable, null);
                var claim = FindCurrentClaim(command.ClaimId);
                if (claim == null) return Reject(command, HoldemCommandError.UnknownAccusation, null);
                bool hit = record.Mutation != null && record.Mutation.Speaker == claim.Target;
                if (command.Action == HoldemAccusationHostAction.RecordVerdict && command.WasManipulated != hit)
                    return Reject(command, HoldemCommandError.InvalidAccusationEvidence, null);
            }

            processing = true;
            try
            {
                if (command.Action == HoldemAccusationHostAction.ApplyConsequences)
                    return ApplyAccusationConsequences(command);
                HoldemAccusationWindow candidate = command.Action == HoldemAccusationHostAction.CloseWindow
                    ? accusations.Close(command.ClaimId) : accusations.Record(command.ClaimId, command.WasManipulated);
                return CommitIntake(command, candidate, null);
            }
            finally { processing = false; }
        }

        /// <summary>Host-only integration queue. Never expose this list through a player connection.</summary>
        public IReadOnlyList<HoldemAccusationClaim> GetPendingAccusations()
            => accusations == null ? Array.Empty<HoldemAccusationClaim>() : accusations.PendingClaims();

        /// <summary>Host-only decisions retained through consequence wait for the current window.</summary>
        public IReadOnlyList<HoldemAccusationDecision> GetAccusationDecisions()
            => accusations == null ? Array.Empty<HoldemAccusationDecision>() : accusations.RecordedDecisions();

        /// <summary>Host-only current candidates. Selection is supplied explicitly; reading never selects or closes.</summary>
        public IReadOnlyList<HoldemAccusationClaim> GetSubmittedAccusations()
            => accusations == null ? Array.Empty<HoldemAccusationClaim>() : accusations.SubmittedClaims();

        private HoldemAccusationClaim FindCurrentClaim(Guid claimId)
        {
            foreach (var claim in GetPendingAccusations()) if (claim.ClaimId == claimId) return claim;
            foreach (var decision in GetAccusationDecisions()) if (decision.Claim.ClaimId == claimId) return decision.Claim;
            return null;
        }

        private HoldemReceipt ApplyAccusationConsequences(HoldemAccusationHostCommand command)
        {
            var claim = FindCurrentClaim(command.ClaimId);
            var record = FindDealRecord(command.HandId, command.WindowId, command.Street);
            bool hit = record.Mutation != null && record.Mutation.Speaker == claim.Target;
            long nextVersion = checked(Version + 1);
            HoldemHand candidate = currentHand.ApplyAccusation(claim.Accuser, claim.Target, hit, out long fine);
            ChipLedger nextLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
            var outcome = new HoldemAccusationOutcome(claim, hit, fine, hit ? candidate.Result.PotAmount : 0);
            var nextOutcomes = new HoldemAccusationOutcome[accusationOutcomes.Length + 1];
            Array.Copy(accusationOutcomes, nextOutcomes, accusationOutcomes.Length);
            nextOutcomes[accusationOutcomes.Length] = outcome;
            var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId, null, nextVersion, HoldemCommandError.None);
            var nextAccusations = accusations.Applied();
            var nextDeal = DealWindowFor(candidate);
            var nextBettingWindow = FixedWindowFor(candidate, nextVersion);
            currentHand = candidate; settledLedger = nextLedger; accusations = nextAccusations;
            accusationOutcomes = nextOutcomes; pendingDeal = nextDeal; fixedBettingWindow = nextBettingWindow;
            fixedChoiceRevisions.Clear(); Version = nextVersion;
            acceptedAccusations.Add(command.CommandId, new AcceptedAccusation(command, receipt));
            usedCommandIds.Add(command.CommandId);
            return receipt;
        }

        private HoldemReceipt ValidateIntakeEnvelope(HoldemWindowCommand command, SeatId? seat)
        {
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession, seat);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted, seat);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand, seat);
            if (processing) return Reject(command, HoldemCommandError.Busy, seat);
            if (acceptedAccusations.TryGetValue(command.CommandId, out AcceptedAccusation previous))
                return previous.Matches(command) ? previous.Receipt : Reject(command, HoldemCommandError.CommandConflict, seat);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict, seat);
            bool concurrentChoice = config.AccusationMode == HoldemAccusationMode.SingleClaimSelectedAtClose
                && command is HoldemAccusationChoiceCommand && accusations != null;
            if (concurrentChoice ? command.ExpectedVersion < accusations.OpenedVersion || command.ExpectedVersion > Version
                : command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch, seat);
            if (config.AccusationMode == HoldemAccusationMode.Disabled)
                return Reject(command, HoldemCommandError.AccusationNotEnabled, seat);
            if (!currentHand.IsRevealPending || accusations == null) return Reject(command, HoldemCommandError.RevealNotPending, seat);
            if (command.WindowId != accusations.Id || command.Street != accusations.Street)
                return Reject(command, HoldemCommandError.WrongRevealWindow, seat);
            return null;
        }

        private HoldemReceipt CommitIntake(HoldemWindowCommand command, HoldemAccusationWindow candidate, SeatId? seat)
        {
            long nextVersion = checked(Version + 1);
            var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId, seat, nextVersion, HoldemCommandError.None);
            accusations = candidate;
            Version = nextVersion;
            acceptedAccusations.Add(command.CommandId, new AcceptedAccusation(command, receipt));
            usedCommandIds.Add(command.CommandId);
            return receipt;
        }

        private HoldemAccusationWindow AccusationsFor(HoldemHand candidate)
        {
            if (config.AccusationMode == HoldemAccusationMode.Disabled || config.AccusationMode == HoldemAccusationMode.OnTurnFirstOrbit
                || !candidate.IsRevealPending) return null;
            return accusations != null && accusations.HandId == candidate.HandId && accusations.Street == candidate.Street
                ? accusations : new HoldemAccusationWindow(candidate, checked(Version + 1));
        }

        private HoldemCommandError AccusationResumeError()
        {
            if (config.AccusationMode == HoldemAccusationMode.Disabled || config.AccusationMode == HoldemAccusationMode.OnTurnFirstOrbit)
                return HoldemCommandError.None;
            if (accusations == null) return HoldemCommandError.AccusationWindowNotClosed;
            if (accusations.Phase == HoldemAccusationPhase.ClosedWithoutClaims
                || accusations.Phase == HoldemAccusationPhase.ConsequencesApplied) return HoldemCommandError.None;
            if (accusations.Phase == HoldemAccusationPhase.Collecting) return HoldemCommandError.AccusationWindowNotClosed;
            return accusations.Phase == HoldemAccusationPhase.AwaitingVerdicts
                ? HoldemCommandError.AccusationVerdictsPending : HoldemCommandError.AccusationConsequencesPending;
        }

        private HoldemReceipt Reject(HoldemWindowCommand command, HoldemCommandError error, SeatId? seat)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId, seat, null, error);
    }
}
