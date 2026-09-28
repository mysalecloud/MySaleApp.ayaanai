using MySale.AI.Application.Stores;

namespace MySale.AI.Application.AIDashboard;

/// <summary>
/// Store (branch) access of the MySaleBooks user, from the same MySaleBooks permission lookup the AI Dashboard uses:
/// system role or no branch mappings = all stores; branch mappings = only those stores; lookup unavailable = unknown.
/// </summary>
public sealed class MySaleBooksStoreAccessProvider : IStoreAccessProvider
{
    private readonly AIDashboardAccessService _access;
    public MySaleBooksStoreAccessProvider(AIDashboardAccessService access) => _access = access;

    public async Task<StoreAccess> GetAsync(CancellationToken ct)
    {
        var lookup = await _access.TryLookupAsync(ct);
        if (lookup is null) return StoreAccess.Unknown;
        if (lookup.IsSystemRole) return new StoreAccess { Verified = true, Unrestricted = true };
        if (lookup.BranchIds is null) return StoreAccess.Unknown; // the response does not describe branch access
        return lookup.BranchIds.Count == 0
            ? new StoreAccess { Verified = true, Unrestricted = true }
            : new StoreAccess { Verified = true, AllowedStoreIds = lookup.BranchIds.ToHashSet(StringComparer.OrdinalIgnoreCase) };
    }
}
