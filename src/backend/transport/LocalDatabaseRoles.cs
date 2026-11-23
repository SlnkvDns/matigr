using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Matigr.Transport;

public static class LocalDatabaseRoles
{
    public static async Task GrantRuntimeAsync(IConfiguration config, string service)
    {
        var password = config["Storage:RuntimePassword"];
        if (password is null || password.Length != 64 || !password.All(Uri.IsHexDigit))
            throw new InvalidOperationException("A local runtime database password is required.");
        var role = service switch {
            "Checking" => "checking_runtime",
            "Results" => "results_runtime",
            "Appeals" => "appeals_runtime",
            _ => throw new ArgumentException("Unknown local database service.", nameof(service))
        };
        await using var db = new NpgsqlConnection(config.GetConnectionString(service));
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        await using (var setting = new NpgsqlCommand("SELECT set_config('matigr.runtime_password', $1, true);", db, transaction))
        {
            setting.Parameters.AddWithValue(password);
            await setting.ExecuteNonQueryAsync();
        }
        await using var setup = new NpgsqlCommand($"""
            SELECT pg_advisory_xact_lock(hashtextextended('matigr-local-runtime-role', 0));
            DO $setup$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') THEN
                    EXECUTE format('CREATE ROLE {role} LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT',
                                   current_setting('matigr.runtime_password'));
                END IF;
            END
            $setup$;
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO {role};
            REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA public FROM {role};
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO {role};
            REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM {role};
            """, db, transaction);
        await setup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
