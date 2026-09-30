public readonly struct MentAnalysis
{
    public const int MinLevel = 0;
    public const int MaxLevel = 4;

    public static readonly MentAnalysis NoIntent = new MentAnalysis(MentIntent.None, null, null, MinLevel, MinLevel);

    public readonly MentIntent Intent;
    // Card.Rank와 같은 1~13 표기. null이면 숫자를 지정하지 않은 요청
    public readonly int? Rank;
    public readonly Suit? Suit;
    // AI가 해석을 얼마나 확신하는지. 후보 선택 기준
    public readonly int Confidence;
    // 사람이 보기에 의도가 얼마나 드러나는지
    public readonly int Explicitness;

    public MentAnalysis(MentIntent intent, int? rank, Suit? suit, int confidence, int explicitness)
    {
        Intent = intent;
        Rank = rank;
        Suit = suit;
        Confidence = confidence;
        Explicitness = explicitness;
    }

    public override string ToString()
    {
        string rank = Rank.HasValue ? MentAnalysisContract.RankToToken(Rank.Value) : "null";
        string suit = Suit.HasValue ? MentAnalysisContract.SuitToToken(Suit.Value) : "null";
        return $"{Intent} rank={rank} suit={suit} confidence={Confidence} explicitness={Explicitness}";
    }
}
