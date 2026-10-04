using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>A client-side cursor. First connection/restart loads history without replaying old desktop notifications.</summary>
public sealed class DeviceNotificationInbox(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private string? epoch;
    private long baseline;
    private readonly SortedDictionary<long, DeviceNotificationEntry> history = [];
    public long Cursor { get; private set; }
    public long Latest { get; private set; }
    public DeviceNotificationEntry[] History => history.Values.Reverse().ToArray();
    public DeviceNotificationFeed? Feed { get; private set; }
    public bool HistoryGap { get; private set; }
    public bool SameEpoch(string current) => epoch == current;
    public DeviceNotificationEntry[] Accept(string currentEpoch, DeviceNotificationFeed feed)
    {
        if (!DeviceServiceProtocol.Id(currentEpoch) || !DeviceNotificationProtocol.Valid(feed)) throw new IOException("通知の応答が不正です");
        if (epoch != currentEpoch)
        {
            epoch = currentEpoch; history.Clear(); Cursor = Latest = 0; Feed = null; HistoryGap = false; baseline = feed.LatestSequence;
        }
        if (feed.NextSequence < Cursor) return [];
        var now = clock.GetUtcNow();
        var delivered = feed.Events.Where(e => e.Sequence > Cursor && e.Sequence > baseline && e.Desktop && feed.Enabled && feed.Desktop &&
            e.Event.Time <= now.AddSeconds(5) && now - e.Event.Time <= TimeSpan.FromSeconds(30)).ToArray();
        foreach (var entry in feed.Events) history[entry.Sequence] = entry;
        while (history.Count > DeviceNotificationProtocol.HistoryCapacity) history.Remove(history.Keys.First());
        Cursor = feed.NextSequence; Latest = Math.Max(Latest, feed.LatestSequence); HistoryGap |= feed.HistoryGap;
        if (Feed is null || feed.ActiveRevision >= Feed.ActiveRevision) Feed = feed;
        return delivered;
    }
}
