namespace FaconDataplane.Api.Authorization;

/// <summary>
/// Declares that a controller or action requires a specific permission.
/// Multiple attributes on the same target require ALL permissions (AND logic).
/// Evaluated by <see cref="PermissionFilter"/> against permissions resolved
/// by <see cref="Middleware.PermissionMiddleware"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : Attribute
{
    /// <summary>Permission key, e.g. "users.manage", "products.delete".</summary>
    public string Permission { get; }

    public RequirePermissionAttribute(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
            throw new ArgumentException("Permission cannot be empty.", nameof(permission));
        Permission = permission;
    }
}
