using System;
using System.Collections.Generic;
using System.Threading;

namespace MagicInventory.Services.MagicSort;

public sealed class ButtonStateCache<TKey> where TKey : IEquatable<TKey>
{
    private sealed record Snapshot(long TicksUtc, TKey Key, MagicSortButtonState State);

    private readonly long ttlTicks;
    private Snapshot? current;

    public ButtonStateCache(double ttlMs = 250.0)
    {
        ttlTicks = (long)(ttlMs * TimeSpan.TicksPerMillisecond);
    }

    public bool TryGet(TKey key, out MagicSortButtonState state)
    {
        var snap = Volatile.Read(ref current);
        if (snap != null
            && EqualityComparer<TKey>.Default.Equals(snap.Key, key)
            && (DateTime.UtcNow.Ticks - snap.TicksUtc) < ttlTicks)
        {
            state = snap.State;
            return true;
        }

        state = MagicSortButtonState.Hidden;
        return false;
    }

    public MagicSortButtonState Set(TKey key, MagicSortButtonState state)
    {
        Volatile.Write(ref current, new Snapshot(DateTime.UtcNow.Ticks, key, state));
        return state;
    }

    public void Invalidate()
    {
        Volatile.Write(ref current, null);
    }
}
