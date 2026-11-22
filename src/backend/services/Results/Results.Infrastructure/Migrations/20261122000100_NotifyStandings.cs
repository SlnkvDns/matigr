using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Matigr.Results.Infrastructure.Migrations;

[DbContext(typeof(ResultsDbContext))]
[Migration("20261122000100_NotifyStandings")]
public partial class NotifyStandings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION notify_matigr_standings() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM pg_notify('matigr_standings', NEW.tournament_id::text || ':' || NEW.version::text);
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER matigr_standings_version
        AFTER UPDATE OF version ON tournament_snapshots
        FOR EACH ROW WHEN (OLD.version IS DISTINCT FROM NEW.version)
        EXECUTE FUNCTION notify_matigr_standings();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER matigr_standings_version ON tournament_snapshots;
        DROP FUNCTION notify_matigr_standings();
        """);
}
