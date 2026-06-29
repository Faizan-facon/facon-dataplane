namespace FaconDataplane.Api.Authorization;

/// <summary>
/// Declares that a controller or action requires a specific feature gate.
/// The <see cref="FeatureGateFilter"/> checks this against the tenant's plan.
/// Multiple attributes on the same target require ALL features (AND logic).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireFeatureAttribute : Attribute
{
    public string Feature { get; }

    /// <summary>
    /// Feature key, e.g. "analytics:view", "reports:export", "products:delete".
    /// </summary>
    public RequireFeatureAttribute(string feature)
    {
        if (string.IsNullOrWhiteSpace(feature))
            throw new ArgumentException("Feature cannot be empty.", nameof(feature));
        Feature = feature;
    }
}
