namespace Csmfd.Protocol;

public sealed record ActivityEvent(long Seq, string Kind, DateTimeOffset At, string Text, Guid? MissionId, string? ObjectiveId, long? Amount, string? Currency, string? ChannelId = null);

/// <summary>A bounded page of observed log events, independent of coalesced ship state.</summary>
public sealed record ActivityPage(Guid StreamId, DateTimeOffset StartedAt, long FirstSeq, long LatestSeq, bool Ended, IReadOnlyList<ActivityEvent> Events);

public static class ActivityLimits
{
    public const int History = 256;
    public const int PageSize = 4;
    public const int TextScalars = 160;
    public const int ObjectiveScalars = 96;
}
