using System;

namespace CodexVsix.Services;

// Owns identity for one debugger stop. A new break gets a new opaque ID even
// when a later stop has the same process, thread, and source location.
internal sealed class DebugStopTracker
{
    public string? SnapshotId { get; private set; }
    public long Revision { get; private set; }

    public string EnsureBreak()
    {
        if (SnapshotId == null)
            SnapshotId = Guid.NewGuid().ToString("N");
        return SnapshotId;
    }

    public string EnterBreak()
    {
        Invalidate();
        return EnsureBreak();
    }

    public void Invalidate()
    {
        Revision++;
        SnapshotId = null;
    }

    public bool Matches(string snapshotId, long revision)
        => revision == Revision && string.Equals(snapshotId, SnapshotId, StringComparison.Ordinal);
}
