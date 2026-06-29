using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace FaconDataplane.Api.Services;

/// <summary>
/// Creates the correct ADO.NET <see cref="DbConnection"/> based on the
/// database engine returned by the control plane's credential endpoint.
/// </summary>
public static class DbConnectionFactory
{
    /// <summary>
    /// Build a connection string from the control plane's resolved resource descriptor.
    /// Produces the engine-specific format and creates the matching <see cref="DbConnection"/>.
    /// </summary>
    public static DbConnection CreateConnection(ResolvedResourceDescriptor resource)
    {
        var engine = resource.Engine?.ToLowerInvariant();
        var connStr = engine switch
        {
            "postgres" or "postgresql" => BuildNpgsqlString(resource),
            "sqlserver" or "mssql" => BuildSqlServerString(resource),
            _ => throw new NotSupportedException(
                $"Database engine '{resource.Engine}' is not supported. Supported: postgres, sqlserver.")
        };

        return engine switch
        {
            "postgres" or "postgresql" => new NpgsqlConnection(connStr),
            "sqlserver" or "mssql" => new SqlConnection(connStr),
            _ => throw new NotSupportedException($"Engine '{resource.Engine}' not supported.")
        };
    }

    /// <summary>
    /// Build just the connection string for the given engine.
    /// Use when you only need the string, not a connection object.
    /// </summary>
    public static string BuildConnectionString(ResolvedResourceDescriptor resource)
    {
        var engine = resource.Engine?.ToLowerInvariant();
        return engine switch
        {
            "postgres" or "postgresql" => BuildNpgsqlString(resource),
            "sqlserver" or "mssql" => BuildSqlServerString(resource),
            _ => throw new NotSupportedException($"Engine '{resource.Engine}' not supported.")
        };
    }

    // ── Engine-specific builders ───────────────────────────────────────

    private static string BuildNpgsqlString(ResolvedResourceDescriptor r)
    {
        var t = r.Topology;
        var c = r.Auth.Credentials;
        var sb = new System.Text.StringBuilder();
        sb.Append($"Host={t.Endpoint};Port={t.Port};Database={t.Database};");
        sb.Append($"Username={c.GetValueOrDefault("username", "")};Password={c.GetValueOrDefault("password", "")};");
        AppendOptions(sb, r.Options);
        return sb.ToString();
    }

    private static string BuildSqlServerString(ResolvedResourceDescriptor r)
    {
        var t = r.Topology;
        var c = r.Auth.Credentials;
        var sb = new System.Text.StringBuilder();
        sb.Append($"Server={t.Endpoint},{t.Port};Database={t.Database};");
        sb.Append($"User Id={c.GetValueOrDefault("username", "")};Password={c.GetValueOrDefault("password", "")};");
        sb.Append("TrustServerCertificate=True;Encrypt=True;");
        AppendOptions(sb, r.Options);
        return sb.ToString();
    }

    private static void AppendOptions(System.Text.StringBuilder sb, Dictionary<string, string>? options)
    {
        if (options is null) return;
        foreach (var (key, value) in options)
            sb.Append($"{key}={value};");
    }
}
