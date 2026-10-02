using System.Collections.Generic;

public enum MentRoundPhase
{
    Collecting,
    Selecting,
    Result
}

// Host가 만들어 모든 Client에 그대로 보내는 라운드 스냅샷. Client는 표시만 한다.
public sealed class MentRoundState
{
    public const int NoPlayer = -1;

    public int Round = 1;
    public MentRoundPhase Phase = MentRoundPhase.Collecting;
    public List<MentRoundEntry> Entries = new List<MentRoundEntry>();
    public int SelectedPlayerId = NoPlayer;
    public string SelectedSummary;

    public MentRoundEntry Find(int playerId) => Entries.Find(entry => entry.PlayerId == playerId);
}

public sealed class MentRoundEntry
{
    public int PlayerId;
    public string Ment;
    public bool Analyzed;
    public string Status;
    public string Summary;
    public string Interpretation;
}
