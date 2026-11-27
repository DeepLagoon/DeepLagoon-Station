namespace Content.Shared.GameTicking;

public sealed class RoundEndedEvent : EntityEventArgs
{
    public int RoundId { get; }
    public TimeSpan RoundDuration { get; }
    public bool Forced { get; }

    public RoundEndedEvent(int roundId, TimeSpan roundDuration, bool forced = false)
    {
        RoundId = roundId;
        RoundDuration = roundDuration;
        Forced = forced;
    }
}
