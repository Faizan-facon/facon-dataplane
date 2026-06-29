using System.Collections.Concurrent;

namespace FaconDataplane.Api.Services;

/// <summary>
/// Maps control-plane plan keys to available feature flags.
/// Caches entitlements per tenant for 60 seconds to avoid
/// hitting the control plane on every request.
/// </summary>
public sealed class FeatureGateService
{
    private readonly ControlPlaneService _cp;
    private readonly ConcurrentDictionary<Guid, CachedFeatures> _cache = new();
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(60);

    // ── Plan → Feature mapping ────────────────────────────────────────

    private static readonly Dictionary<string, HashSet<string>> PlanFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Trial"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "products:read",
            "products:create",
            "dashboard:view",
        },
        ["Pro"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "products:read",
            "products:create",
            "products:update",
            "products:delete",
            "dashboard:view",
            "analytics:view",
            "reports:export",
            "api:access",
        },
        ["Enterprise"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "products:read",
            "products:create",
            "products:update",
            "products:delete",
            "dashboard:view",
            "analytics:view",
            "reports:export",
            "api:access",
            "sso:manage",
            "audit:view",
            "custom:branding",
            "priority:support",
        },
    };

    /// <summary>
    /// All known features across all plans. Used for validation.
    /// </summary>
    public static readonly IReadOnlySet<string> AllFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "products:read", "products:create", "products:update", "products:delete",
        "dashboard:view", "analytics:view", "reports:export", "api:access",
        "sso:manage", "audit:view", "custom:branding", "priority:support",
    };

    // ── Constructor ────────────────────────────────────────────────────

    public FeatureGateService(ControlPlaneService cp)
    {
        _cp = cp;
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Check if the tenant has access to a specific feature.
    /// Caches the entitlement grant for 60 seconds per tenant.
    /// </summary>
    public async Task<bool> HasFeatureAsync(
        Guid tenantId, string feature, string bearerJwt, CancellationToken ct = default)
    {
        var features = await GetFeaturesAsync(tenantId, bearerJwt, ct);
        return features?.Contains(feature) == true;
    }

    /// <summary>
    /// Get all features available to the tenant. Returns null if the
    /// control plane is unreachable or the tenant has no subscription.
    /// </summary>
    public async Task<HashSet<string>?> GetFeaturesAsync(
        Guid tenantId, string bearerJwt, CancellationToken ct = default)
    {
        // Return cached if fresh
        if (_cache.TryGetValue(tenantId, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Features;
        }

        var grant = await _cp.GetEntitlementsAsync(tenantId, bearerJwt, ct);
        if (grant is null)
        {
            // Stale cache is better than nothing
            if (_cache.TryGetValue(tenantId, out var stale))
                return stale.Features;
            return null;
        }

        // Map plan → features
        if (!PlanFeatures.TryGetValue(grant.PlanKey, out var features))
        {
            // Unknown plan — grant nothing
            features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        _cache[tenantId] = new CachedFeatures(features, DateTimeOffset.UtcNow + _cacheTtl);
        return features;
    }

    /// <summary>
    /// Invalidate the feature cache for a tenant. Call after plan changes.
    /// </summary>
    public void Invalidate(Guid tenantId)
    {
        _cache.TryRemove(tenantId, out _);
    }

    /// <summary>
    /// Check if a feature string is known. Returns false for unknown features.
    /// </summary>
    public static bool IsKnownFeature(string feature) => AllFeatures.Contains(feature);

    // ── Nested types ───────────────────────────────────────────────────

    private sealed record CachedFeatures(HashSet<string> Features, DateTimeOffset ExpiresAt);
}
