using System;
using Poker.Foundation;

namespace Poker.Llm
{
    public static class MentAnalysisContract
    {
        public const string IntentNone = "NONE";
        public const string IntentCardRequest = "CARD_REQUEST";

        public static readonly string[] RankTokens = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };
        private static readonly Rank[] RankValues =
        {
            Rank.Ace, Rank.Two, Rank.Three, Rank.Four, Rank.Five, Rank.Six, Rank.Seven,
            Rank.Eight, Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King
        };

        public static readonly string[] SuitTokens = { "HEART", "DIAMOND", "CLUB", "SPADE" };
        private static readonly Suit[] SuitValues = { Suit.Hearts, Suit.Diamonds, Suit.Clubs, Suit.Spades };

        public static bool TryParseIntent(string token, out MentIntent intent)
        {
            switch (token)
            {
                case IntentNone:
                    intent = MentIntent.None;
                    return true;
                case IntentCardRequest:
                    intent = MentIntent.CardRequest;
                    return true;
                default:
                    intent = default;
                    return false;
            }
        }

        public static bool TryParseRank(string token, out Rank rank)
        {
            int index = Array.IndexOf(RankTokens, token);
            rank = index >= 0 ? RankValues[index] : default;
            return index >= 0;
        }

        public static bool TryParseSuit(string token, out Suit suit)
        {
            int index = Array.IndexOf(SuitTokens, token);
            suit = index >= 0 ? SuitValues[index] : default;
            return index >= 0;
        }

        public static string RankToToken(Rank rank) => RankTokens[Array.IndexOf(RankValues, rank)];

        public static string SuitToToken(Suit suit) => SuitTokens[Array.IndexOf(SuitValues, suit)];
    }
}
