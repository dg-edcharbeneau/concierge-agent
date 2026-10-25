using Concierge.Contracts;

namespace Concierge.Web.Services;

/// <summary>
/// The Web client's bounded ride-activity view. It keeps only the newest 25 entries while
/// deduplicating ride/sequence keys within the newest 100 stream positions. Once a key's
/// first-seen offset leaves that range, a later duplicate can appear again.
/// </summary>
public sealed class ConciergeRideActivityWindow
{
    public const int MaxEntries = 25;

    private readonly List<ConciergeRideActivityItem> entries;
    // Seen key -> the stream offset it was first seen at. Pruned by offset, not insertion order,
    // so a delayed snapshot cannot push still-retained keys out.
    private readonly Dictionary<(Guid RideId, int Sequence), long> seenEntries;
    private long nextStreamOffset;

    public ConciergeRideActivityWindow(ConciergeRideActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateReadState(snapshot.ReadState);
        ReadState = snapshot.ReadState;
        nextStreamOffset = snapshot.ReadState.SnapshotOffset;
        entries = [];
        seenEntries = [];
        foreach (var item in snapshot.Entries.OrderBy(item => item.Offset))
        {
            AddIfNew(item);
        }

        ForgetKeysOutsideRetainedLog();
    }

    /// <summary>The retained read cursor and latest observed stream boundaries.</summary>
    public ConciergeRideReadState ReadState { get; private set; }

    /// <summary>Entries in ascending stream offset, limited to the newest 25.</summary>
    public IReadOnlyList<ConciergeRideActivityItem> Entries => entries;

    /// <summary>
    /// Displayed entries at or beyond the read cursor. Counted from deduplicated entries, not
    /// offsets, because a retried publish consumes an offset without adding a visible update.
    /// </summary>
    public int UnreadCount => entries.Count(item => item.Offset >= ReadState.ReadOffset);

    /// <summary>
    /// Applies one ordered stream update. Returns false for an already-seen ride/sequence or
    /// an offset already consumed. Gaps are rejected instead of silently losing activity.
    /// </summary>
    public bool Apply(ConciergeRideActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Entry);
        if (item.Offset < nextStreamOffset)
        {
            return false;
        }

        if (item.Offset < 0
            || item.Entry.RideId == Guid.Empty
            || item.Entry.Sequence < 1)
        {
            return false;
        }
        if (item.Offset != nextStreamOffset)
        {
            throw new InvalidOperationException("Ride activity stream offsets must be contiguous.");
        }

        var added = AddIfNew(item);
        var snapshotOffset = checked(item.Offset + 1);
        nextStreamOffset = snapshotOffset;
        var retainedFromOffset = Math.Max(
            ReadState.RetainedFromOffset,
            snapshotOffset - RideActivityFeed.MaxRetainedEntries);
        ReadState = ReadState with
        {
            RetainedFromOffset = retainedFromOffset,
            // A read-state response may already have counted this entry; keep the boundary forward-only.
            SnapshotOffset = Math.Max(ReadState.SnapshotOffset, snapshotOffset),
        };
        ForgetKeysOutsideRetainedLog();
        return added;
    }

    /// <summary>
    /// Merges a refreshed server snapshot (for example, when Activity is reopened) into this window.
    /// Seen keys and the consumed stream cursor survive the merge: rebuilding from the 25-entry
    /// snapshot would forget evicted keys and could rewind the cursor behind offsets the live
    /// stream already consumed for duplicates, which then look like a gap. A delayed snapshot may
    /// carry entries the workflow no longer retains; those are ignored rather than resurrected.
    /// </summary>
    public void MergeSnapshot(ConciergeRideActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ApplyReadState(snapshot.ReadState);
        var retainedFromOffset = RetainedBoundary();
        foreach (var item in snapshot.Entries.OrderBy(item => item.Offset))
        {
            if (item.Offset >= retainedFromOffset)
            {
                AddIfNew(item);
            }
        }

        nextStreamOffset = Math.Max(nextStreamOffset, snapshot.ReadState.SnapshotOffset);
        ForgetKeysOutsideRetainedLog();
    }

    /// <summary>
    /// Applies a server read-state result without moving the local cursor backward. The server's
    /// snapshot boundary may include live entries that arrived after the panel was opened.
    /// </summary>
    public void ApplyReadState(ConciergeRideReadState readState)
    {
        ValidateReadState(readState);
        ReadState = new ConciergeRideReadState(
            Math.Max(ReadState.ReadOffset, readState.ReadOffset),
            Math.Max(
                Math.Max(ReadState.RetainedFromOffset, readState.RetainedFromOffset),
                readState.SnapshotOffset - RideActivityFeed.MaxRetainedEntries),
            Math.Max(ReadState.SnapshotOffset, readState.SnapshotOffset));
    }

    private bool AddIfNew(ConciergeRideActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item.Entry);
        if (item.Offset < 0
            || item.Entry.RideId == Guid.Empty
            || item.Entry.Sequence < 1
            || !seenEntries.TryAdd((item.Entry.RideId, item.Entry.Sequence), item.Offset))
        {
            return false;
        }

        entries.Add(item);
        entries.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        if (entries.Count > MaxEntries)
        {
            entries.RemoveAt(0);
        }

        return true;
    }

    /// <summary>
    /// The oldest offset the workflow can still retain, as far as this client has observed.
    /// </summary>
    private long RetainedBoundary() =>
        Math.Max(ReadState.RetainedFromOffset, nextStreamOffset - RideActivityFeed.MaxRetainedEntries);

    /// <summary>
    /// Deduplication is bounded to the newest 100 stream positions, not the retry deadline.
    /// Forgetting older keys bounds memory; a later duplicate of a forgotten key can appear again.
    /// </summary>
    private void ForgetKeysOutsideRetainedLog()
    {
        var retainedFromOffset = RetainedBoundary();
        foreach (var (key, offset) in seenEntries)
        {
            if (offset < retainedFromOffset)
            {
                seenEntries.Remove(key);
            }
        }
    }

    private static void ValidateReadState(ConciergeRideReadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ReadOffset < 0
            || state.RetainedFromOffset < 0
            || state.SnapshotOffset < state.RetainedFromOffset
            || state.ReadOffset > state.SnapshotOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Ride activity offsets must be nonnegative and ordered.");
        }
    }
}
