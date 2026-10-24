using Orleans.Runtime;

namespace Fleans.Application.Placement;

/// <summary>
/// The cluster's active hosts for a placement director, reused for <see cref="Lifetime"/>.
/// <c>IManagementGrain.GetDetailedHosts</c> reads the membership table from its storage and is one
/// grain for the whole cluster, so asking it on every activation put a membership-table read and a
/// cluster-wide grain on every instance start (~1.3 reads per started instance in the load stack).
/// </summary>
/// <remarks>
/// A director intersects the cached list with <c>IPlacementContext.GetCompatibleSilos</c>, which
/// tracks live membership, so a silo that left is never picked; a silo that joined is picked once
/// the entry expires, or at once when the cached list has no candidate (<c>forceRefresh</c>).
/// Concurrent callers share one fetch; a failed fetch is not cached.
/// </remarks>
public sealed class CachedSiloHosts
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly Func<Task<MembershipEntry[]>> _fetch;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private MembershipEntry[]? _hosts;
    private long _fetchedAt;
    private Task<MembershipEntry[]>? _pending;

    public CachedSiloHosts(Func<Task<MembershipEntry[]>> fetch, TimeProvider? time = null)
    {
        _fetch = fetch;
        _time = time ?? TimeProvider.System;
    }

    public Task<MembershipEntry[]> GetAsync(bool forceRefresh = false)
    {
        lock (_lock)
        {
            if (!forceRefresh && _hosts is not null && _time.GetElapsedTime(_fetchedAt) < Lifetime)
                return Task.FromResult(_hosts);
            return _pending ??= FetchAsync();
        }
    }

    private async Task<MembershipEntry[]> FetchAsync()
    {
        // Run the fetch after GetAsync has stored this task in _pending, so the finally below
        // always clears it (a synchronously completing fetch would otherwise leave it set).
        await Task.Yield();
        try
        {
            var hosts = await _fetch();
            lock (_lock)
            {
                _hosts = hosts;
                _fetchedAt = _time.GetTimestamp();
            }
            return hosts;
        }
        finally
        {
            lock (_lock) _pending = null;
        }
    }
}
