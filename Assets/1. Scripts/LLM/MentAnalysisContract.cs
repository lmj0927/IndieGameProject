using System;

public static class MentAnalysisContract
{
    public const string IntentNone = "NONE";
    public const string IntentCardRequest = "CARD_REQUEST";

    // 인덱스 + 1 = Card.Rank
    public static readonly string[] RankTokens = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    public static readonly string[] SuitTokens = { "HEART", "DIAMOND", "CLUB", "SPADE" };
    private static readonly Suit[] SuitValues = { Suit.Heart, Suit.Diamond, Suit.Club, Suit.Spade };

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

    public static bool TryParseRank(string token, out int rank)
    {
        int index = Array.IndexOf(RankTokens, token);
        rank = index + 1;
        return index >= 0;
    }

    public static bool TryParseSuit(string token, out Suit suit)
    {
        int index = Array.IndexOf(SuitTokens, token);
        suit = index >= 0 ? SuitValues[index] : default;
        return index >= 0;
    }

    public static string RankToToken(int rank) => RankTokens[rank - 1];

    public static string SuitToToken(Suit suit) => SuitTokens[Array.IndexOf(SuitValues, suit)];
}
