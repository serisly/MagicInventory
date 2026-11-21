using System;

namespace MagicInventory.Services.MagicSort;

/// <summary>
/// Thread-safe, zero-allocation TTL cache for Magic Sort / Bell / Station button states.
/// Replaces scattered DateTime + cached-state field groups across services.
/// </summary>
public sealed class ButtonStateCache<TKey> where TKey : IEquatable<TKey>
{
    private readonly double ttlMs;
    private long cachedTicksUtc;
    private TKey? lastKey;
    private MagicSortButtonState cachedState = MagicSortButtonState.Hidden;
    private bool hasValue;

    public ButtonStateCache(double ttlMs = 250.0)
    {
        this.ttlMs = ttlMs;
    }

    public bool TryGet(TKey key, out MagicSortButtonState state)
    {
        var ticks = System.Threading.Volatile.Read(ref cachedTicksUtc);
        if (hasValue
            && ticks > 0
            && EqualityComparer<TKey>.Default.Equals(lastKey, key)
            && (DateTime.UtcNow.Ticks - ticks) < (long)(ttlMs * TimeSpan.TicksPerMillisecond))
        {
            state = cachedState;
            return true;
        }

        state = MagicSortButtonState.Hidden;
        return false;
    }

    public MagicSortButtonState Set(TKey key, MagicSortButtonState state)
    {
        lastKey = key;
        cachedState = state;
        hasValue = true;
        System.Threading.Volatile.Write(ref cachedTicksUtc, DateTime.UtcNow.Ticks);
        return state;
    }

    public void Invalidate()
    {
        System.Threading.Volatile.Write(ref cachedTicksUtc, 0);
    }
}
