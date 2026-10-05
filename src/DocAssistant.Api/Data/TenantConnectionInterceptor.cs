using System.Data.Common;
using DocAssistant.Shared.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DocAssistant.Api.Data;

// Tells PostgreSQL which tenant the connection is working for. Row-Level Security
// policies compare each row's tenant_id with this session setting (docs/decisions.md #15).
public sealed class TenantConnectionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    public const string SettingName = "app.tenant_id";

    // Connections are pooled and reused across requests, so the setting is written on
    // every open, also when there is no tenant: an empty value must replace whatever a
    // previous request left behind.
    private const string Sql = $"SELECT set_config('{SettingName}', @tenant_id, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = tenantContext.TenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);

        return command;
    }
}
