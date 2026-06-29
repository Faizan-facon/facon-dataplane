using System.Collections.Concurrent;
using Npgsql;

namespace FaconDataplane.Api.Services;

/// <summary>
/// Maintains one open Npgsql connection per tenant.
/// Refreshes credentials transparently when the lease expires.
/// Thread-safe — uses a semaphore to prevent duplicate credential fetches.
/// </summary>
public sealed class TenantConnectionPool : IDisposable
{
    private readonly ConcurrentDictionary<Guid, PooledConnection> _connections = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<NpgsqlConnection> GetConnectionAsync(
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
            var conn = new NpgsqlConnection(cred.ConnectionString);
            await conn.OpenAsync(ct);

            _connections[tenantId] = new PooledConnection(conn, cred);
            return conn;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task ReturnAsync(Guid tenantId, NpgsqlConnection connection)
    {
        // Keep connection open for reuse — expiry check happens on next Get
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var (_, pooled) in _connections)
            pooled.Connection.Dispose();
        _connections.Clear();
        _lock.Dispose();
    }

    private sealed record PooledConnection(NpgsqlConnection Connection, DbCredential Credential);
}

public sealed record DbCredential(string ConnectionString, DateTimeOffset ExpiresAt);
