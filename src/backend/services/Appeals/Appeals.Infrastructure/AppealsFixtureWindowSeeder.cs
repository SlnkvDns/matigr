using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

public sealed class AppealsFixtureWindowSeeder(AppealsDbContext db)
{
    public async Task EnsureOpenFixtureAsync(string manifestPath, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var tournament = document.RootElement.GetProperty("tournament");
        var tournamentId = tournament.GetProperty("tournamentId").GetGuid();
        var initialWindow = tournament.GetProperty("initialAppealWindow").GetString();
        if (initialWindow != "Open")
            throw new InvalidDataException("The fixture must declare initialAppealWindow=Open.");

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO submission_windows (tournament_id, state, version)
            VALUES ({tournamentId}, 'Open', 1)
            ON CONFLICT (tournament_id) DO NOTHING
            """, cancellationToken);
    }
}
