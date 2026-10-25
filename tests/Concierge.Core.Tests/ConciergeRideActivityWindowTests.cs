using Concierge.Contracts;
using Concierge.Web.Services;

namespace Concierge.Core.Tests;

public sealed class ConciergeRideActivityWindowTests
{
    [Fact]
    public void MarkingSnapshotDoesNotDropAnArrivalAlreadyIncludedInTheUpdateResponse()
    {
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 1),
                [Item(0, Guid.NewGuid(), 1)]));
        var nextEntry = Item(1, Guid.NewGuid(), 1);

        // The update response can observe the entry before the stream callback delivers it.
        window.ApplyReadState(new ConciergeRideReadState(
            ReadOffset: 1,
            RetainedFromOffset: 0,
            SnapshotOffset: 2));

        Assert.True(window.Apply(nextEntry));
        Assert.Contains(window.Entries, item => item.Entry.RideId == nextEntry.Entry.RideId);
        Assert.Equal(1, window.UnreadCount);
    }

    [Fact]
    public void InitialAndLiveEntriesKeepOnlyNewest25AndDeduplicateByRideAndSequence()
    {
        var initial = Enumerable.Range(0, 25)
            .Select(offset => Item(offset, Guid.NewGuid(), 1))
            .ToArray();
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 25),
                initial));

        Assert.Equal(25, window.Entries.Count);
        Assert.Equal(25, window.UnreadCount);

        for (var offset = 25; offset < 45; offset++)
        {
            Assert.True(window.Apply(Item(offset, Guid.NewGuid(), 1)));
        }

        Assert.Equal(25, window.Entries.Count);
        Assert.Equal(Enumerable.Range(20, 25), window.Entries.Select(item => checked((int)item.Offset)));
        Assert.Equal(45, window.ReadState.SnapshotOffset);

        var duplicate = window.Entries[^1].Entry;
        Assert.False(window.Apply(Item(45, duplicate.RideId, duplicate.Sequence)));
        Assert.Equal(25, window.Entries.Count);
        Assert.Equal(46, window.ReadState.SnapshotOffset);

        window.ApplyReadState(new ConciergeRideReadState(
            ReadOffset: 45,
            RetainedFromOffset: 0,
            SnapshotOffset: 46));

        // The duplicate consumed offset 45 but is not displayed, so nothing is unread.
        Assert.Equal(0, window.UnreadCount);
    }

    [Fact]
    public void LiveDuplicateAtNewOffsetDoesNotIncreaseUnreadCountButNewEntryDoes()
    {
        var rideId = Guid.NewGuid();
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 1, RetainedFromOffset: 0, SnapshotOffset: 2),
                [Item(0, rideId, 1), Item(1, rideId, 2)]));
        Assert.Equal(1, window.UnreadCount);

        Assert.False(window.Apply(Item(2, rideId, 2)));
        Assert.Equal(2, window.Entries.Count);
        Assert.Single(window.Entries, item => item.Entry.Sequence == 2);
        Assert.DoesNotContain(window.Entries, item => item.Offset == 2);
        Assert.Equal(3, window.ReadState.SnapshotOffset);
        Assert.Equal(1, window.UnreadCount);

        Assert.True(window.Apply(Item(3, rideId, 3)));
        Assert.Equal(2, window.UnreadCount);
    }

    [Fact]
    public void UnreadCountNeverExceedsDisplayedEntries()
    {
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 0),
                []));

        for (var offset = 0; offset < 60; offset++)
        {
            window.Apply(Item(offset, Guid.NewGuid(), 1));
        }

        Assert.Equal(ConciergeRideActivityWindow.MaxEntries, window.Entries.Count);
        Assert.Equal(ConciergeRideActivityWindow.MaxEntries, window.UnreadCount);
    }

    [Fact]
    public void DuplicateKeyMemoryIsBoundedToTheRetainedLog()
    {
        var firstRide = Guid.NewGuid();
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 1),
                [Item(0, firstRide, 1)]));

        // Offset 0 is still within the retained log (newest 100 offsets): a retry is rejected even after leaving the display window.
        for (var offset = 1; offset < RideActivityFeed.MaxRetainedEntries; offset++)
        {
            Assert.True(window.Apply(Item(offset, Guid.NewGuid(), 1)));
        }

        Assert.False(window.Apply(Item(RideActivityFeed.MaxRetainedEntries, firstRide, 1)));

        // Consuming offset 100 moved the retained boundary past offset 0, so the key is forgotten rather than kept for the session.
        Assert.True(window.Apply(Item(RideActivityFeed.MaxRetainedEntries + 1, Guid.NewGuid(), 1)));
        Assert.True(window.Apply(Item(RideActivityFeed.MaxRetainedEntries + 2, firstRide, 1)));
    }

    [Fact]
    public void ReopenMergeKeepsDuplicateMemoryForEntriesEvictedFromDisplay()
    {
        var evictedRide = Guid.NewGuid();
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 1),
                [Item(0, evictedRide, 1)]));
        for (var offset = 1; offset < 30; offset++)
        {
            Assert.True(window.Apply(Item(offset, Guid.NewGuid(), 1)));
        }

        Assert.DoesNotContain(window.Entries, item => item.Entry.RideId == evictedRide);

        // Reopening: the server snapshot holds only the newest 25 and the guest has read them all.
        window.MergeSnapshot(new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(ReadOffset: 30, RetainedFromOffset: 0, SnapshotOffset: 30),
            window.Entries.ToArray()));
        Assert.Equal(0, window.UnreadCount);

        Assert.False(window.Apply(Item(30, evictedRide, 1)));
        Assert.DoesNotContain(window.Entries, item => item.Entry.RideId == evictedRide);
        Assert.Equal(0, window.UnreadCount);
        Assert.Equal(31, window.ReadState.SnapshotOffset);
    }

    [Fact]
    public void DelayedStaleSnapshotDoesNotEvictKeysStillInTheRetainedLog()
    {
        var keys = Enumerable.Range(0, 110).Select(_ => Guid.NewGuid()).ToArray();
        // Snapshot captured when offsets 0..24 were current, but merged only after the live stream moved on.
        var staleSnapshot = new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 25),
            Enumerable.Range(0, 25).Select(offset => Item(offset, keys[offset], 1)).ToArray());
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 25),
                staleSnapshot.Entries));
        for (var offset = 25; offset < 110; offset++)
        {
            Assert.True(window.Apply(Item(offset, keys[offset], 1)));
        }

        window.MergeSnapshot(staleSnapshot);

        // Stale entries outside the retained log (10..109) are neither displayed nor remembered.
        Assert.Equal(Enumerable.Range(85, 25), window.Entries.Select(item => checked((int)item.Offset)));
        window.ApplyReadState(new ConciergeRideReadState(ReadOffset: 110, RetainedFromOffset: 10, SnapshotOffset: 110));
        Assert.Equal(0, window.UnreadCount);

        // A retry of the update originally at offset 25 (still retained) is still rejected.
        Assert.False(window.Apply(Item(110, keys[25], 1)));
        Assert.DoesNotContain(window.Entries, item => item.Offset == 110);
        Assert.Equal(0, window.UnreadCount);

        // The key from offset 9 was not resurrected by the stale merge; the workflow no longer retains it.
        Assert.True(window.Apply(Item(111, keys[9], 1)));
        Assert.Equal(1, window.UnreadCount);
    }

    [Fact]
    public void MergingAnOlderSnapshotKeepsTheConsumedStreamCursor()
    {
        var rideId = Guid.NewGuid();
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 2),
                [Item(0, rideId, 1), Item(1, rideId, 2)]));
        // The live stream consumes a duplicate at offset 2, which is never displayed.
        Assert.False(window.Apply(Item(2, rideId, 2)));

        // A snapshot read before that duplicate arrived is merged afterwards.
        window.MergeSnapshot(new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 2),
            [Item(0, rideId, 1), Item(1, rideId, 2)]));

        Assert.False(window.Apply(Item(2, rideId, 2)));
        Assert.True(window.Apply(Item(3, rideId, 3)));
        Assert.Equal([0L, 1L, 3L], window.Entries.Select(item => item.Offset));
        Assert.Equal(4, window.ReadState.SnapshotOffset);
        Assert.Equal(3, window.UnreadCount);
    }

    [Fact]
    public void ReopenKeepsALiveArrivalBetweenSnapshotReadAndMergeUnread()
    {
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 2),
                [Item(0, Guid.NewGuid(), 1), Item(1, Guid.NewGuid(), 1)]));
        var reopenSnapshot = new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 2),
            window.Entries.ToArray());
        var arrival = Item(2, Guid.NewGuid(), 1);

        Assert.True(window.Apply(arrival));
        window.MergeSnapshot(reopenSnapshot);
        // Marking the reopen snapshot read; the server response may already include the arrival.
        window.ApplyReadState(new ConciergeRideReadState(ReadOffset: 2, RetainedFromOffset: 0, SnapshotOffset: 3));

        Assert.Contains(arrival, window.Entries);
        Assert.Equal(1, window.UnreadCount);
        Assert.True(window.Apply(Item(3, Guid.NewGuid(), 1)));
        Assert.Equal(2, window.UnreadCount);
    }

    [Fact]
    public void MergingANewerSnapshotAdvancesTheCursorPastEntriesItAlreadyContains()
    {
        var window = new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 1),
                [Item(0, Guid.NewGuid(), 1)]));
        var missed = Item(1, Guid.NewGuid(), 1);

        window.MergeSnapshot(new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(ReadOffset: 0, RetainedFromOffset: 0, SnapshotOffset: 2),
            [window.Entries[0], missed]));

        // The old subscription later delivers the same offset; it is ignored, and the next one is contiguous.
        Assert.False(window.Apply(missed));
        Assert.True(window.Apply(Item(2, Guid.NewGuid(), 1)));
        Assert.Equal(3, window.Entries.Count);
        Assert.Equal(3, window.UnreadCount);
    }

    private static ConciergeRideActivityItem Item(long offset, Guid rideId, int sequence) =>
        new(
            offset,
            new RideActivityEntry(
                rideId,
                sequence,
                "Booked",
                DateTimeOffset.UnixEpoch.AddSeconds(offset),
                "Hotel",
                "Beach",
                DateTimeOffset.UnixEpoch.AddHours(1)));
}
