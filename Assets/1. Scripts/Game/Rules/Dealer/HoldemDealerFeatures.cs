using System;
using Poker.Foundation;

namespace Poker.Application
{
    public enum HoldemDealerColor { Red, Black }
    public enum HoldemDealerDirectness { None, FarAssociation, NearAssociation, DirectMention }

    /// <summary>Validated host-only features. The model does not supply Level or a success verdict.</summary>
    public sealed class HoldemDealerFeatures
    {
        public HoldemDealerFeatures(Guid utteranceId, Rank? rank, Suit? suit, HoldemDealerColor? color,
            HoldemDealerDirectness directness, bool hasRequestForm, double confidence)
        {
            if (!Enum.IsDefined(typeof(HoldemDealerDirectness), directness))
                throw new ArgumentOutOfRangeException(nameof(directness));
            bool hasTarget = rank.HasValue || suit.HasValue || color.HasValue;
            if (hasTarget != (directness != HoldemDealerDirectness.None))
                throw new ArgumentException("Directness needs one unambiguous target range.");
            // Reuse the internal card-domain and finite-number validation.
            new HoldemDealerInterpretation(utteranceId, hasTarget ? HoldemDealerIntent.CardPreference
                : HoldemDealerIntent.NoRequest, rank, suit, 0, confidence, color);
            UtteranceId = utteranceId; Rank = rank; Suit = suit; Color = color;
            Directness = directness; HasRequestForm = hasRequestForm; Confidence = confidence;
        }

        public Guid UtteranceId { get; }
        public Rank? Rank { get; }
        public Suit? Suit { get; }
        public HoldemDealerColor? Color { get; }
        public HoldemDealerDirectness Directness { get; }
        // A request/command wording bonus, not whether the player selected this message.
        public bool HasRequestForm { get; }
        public double Confidence { get; }

        public HoldemDealerInterpretation Normalize(double minimumConfidence)
        {
            if (double.IsNaN(minimumConfidence) || double.IsInfinity(minimumConfidence)
                || minimumConfidence < 0 || minimumConfidence > 1)
                throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
            int level = Directness == HoldemDealerDirectness.None || Confidence < minimumConfidence
                ? 0 : (int)Directness + (HasRequestForm ? 1 : 0);
            return new HoldemDealerInterpretation(UtteranceId,
                level == 0 ? HoldemDealerIntent.NoRequest : HoldemDealerIntent.CardPreference,
                level == 0 ? null : Rank, level == 0 ? null : Suit, level / 4.0, Confidence,
                level == 0 ? null : Color, level);
        }
    }
}
