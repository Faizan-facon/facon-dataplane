using System.Collections.Concurrent;
using System.Data.Common;

namespace FaconDataplane.Api.Services;

/// <summary>
/// Maintains one open <see cref="DbConnection"/> per tenant.
/// Provider-agnostic — works with PostgreSQL (Npgsql) and SQL Server (SqlClient).
/// Refreshes credentials transparently when the lease expires.
/// Thread-safe — uses a semaphore to prevent duplicate credential fetches.
/// </summary>
public sealed class TenantConnectionPool : IDisposable
{
    private readonly ConcurrentDictionary<Guid, PooledConnection> _connections = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Gets or creates a connection for the given tenant.
    /// Automatically refreshes if the credential lease has expired or the connection is broken.
    /// </summary>
    public async Task<DbConnection> GetConnectionAsync(
        Guid tenantId,
        Func<Task<DbCredential>> credentialFactory,
        CancellationToken ct)
    {
        if (_connections.TryGetValue(tenantId, out var pooled))
        {
            var isExpired = pooled.Credential.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30);
            var isOpen = pooled.Connection.State == System.Data.ConnectionState.Open;

            if (!isExpired && isOpen)
                return pooled.Connection;

            // Expired or broken — dispose and refresh
            await pooled.Connection.DisposeAsync();
            _connections.TryRemove(tenantId, out _);
        }

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_connections.TryGetValue(tenantId, out var pooled2))
            {
                var stillGood = pooled2.Credential.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30) &&
                                pooled2.Connection.State == System.Data.ConnectionState.Open;
                if (stillGood)
                    return pooled2.Connection;

                await pooled2.Connection.DisposeAsync();
                _connections.TryRemove(tenantId, out _);
            }

            var cred = await credentialFactory();
            var conn = DbConnectionFactory.CreateConnection(cred.Resource);
            await conn.OpenAsync(ct);

            _connections[tenantId] = new PooledConnection(conn, cred);
            return conn;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Return a connection to the pool. Connections stay open for reuse.
    /// </summary>
    public Task ReturnAsync(Guid tenantId, DbConnection connection)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var (_, pooled) in _connections)
            pooled.Connection.Dispose();
        _connections.Clear();
        _lock.Dispose();
    }

    private sealed record PooledConnection(DbConnection Connection, DbCredential Credential);
}

/// <summary>
/// Resolved credential with the full control-plane resource descriptor
/// so the pool can rebuild connections when credentials rotate.
/// </summary>
public sealed record DbCredential(ResolvedResourceDescriptor Resource)
{
    public DateTimeOffset ExpiresAt => Resource.Lease.ExpiresAt;
}
