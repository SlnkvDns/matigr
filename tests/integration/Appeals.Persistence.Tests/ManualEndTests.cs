using Matigr.Appeals.Application;
using Matigr.Appeals.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Appeals.Persistence.Tests;

public partial class StorageTests
{
    [Fact]
    public async Task Manual_end_is_idempotent_by_key_and_fact_and_cannot_reopen_or_replace_first_end()
    {
        var a = await NewAppeal(); await using var db = fixture.Open();
        var actor = new AppealActor(Guid.NewGuid(), ["Organizer"], [a.TournamentId], null);
        var fact = Guid.NewGuid(); var end = new ManualEnd(db);
        var first = await end.RecordAsync(actor, a.TournamentId, fact, "key", default);
        void Same(AppealReply other) {
            Assert.Equal(first.Status, other.Status); Assert.Equal(first.ETag, other.ETag); Assert.Equal(first.Location, other.Location);
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(first.Json).RootElement, JsonDocument.Parse(other.Json).RootElement));
        }
        Same(await end.RecordAsync(actor, a.TournamentId, fact, "key", default));
        Same(await end.RecordAsync(actor, a.TournamentId, fact, "new-key", default));
        Assert.Equal(409, (await Assert.ThrowsAsync<AppealOperationException>(() => end.RecordAsync(actor, a.TournamentId, Guid.NewGuid(), "key", default))).Status);
        Same(await end.RecordAsync(actor, a.TournamentId, Guid.NewGuid(), "new-fact", default));
        var window = await db.SubmissionWindows.AsNoTracking().SingleAsync(x => x.TournamentId == a.TournamentId);
        Assert.Equal("Closed", window.State); Assert.Equal(2, window.Version); Assert.Equal(fact, window.EndFactId); Assert.Equal(actor.SubjectId, window.EndedBy);
        Assert.Equal(2, await db.EndFacts.CountAsync(x => x.TournamentId == a.TournamentId));
    }
    [Theory]
    [InlineData("Team")]
    [InlineData("Jury")]
    [InlineData("Senior")]
    public async Task Only_scoped_organizer_can_record_manual_end(string role)
    {
        var a = await NewAppeal(); await using var db = fixture.Open(); var end = new ManualEnd(db);
        var actor = new AppealActor(Guid.NewGuid(), [role], [a.TournamentId], role == "Team" ? Guid.NewGuid() : null);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppealOperationException>(() => end.RecordAsync(actor, a.TournamentId, Guid.NewGuid(), "key", default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppealOperationException>(() => end.RecordAsync(actor with { Roles = ["Organizer"], Tournaments = [] }, a.TournamentId, Guid.NewGuid(), "key", default))).Status);
        Assert.Equal("Open", (await db.SubmissionWindows.SingleAsync(x => x.TournamentId == a.TournamentId)).State);
        Assert.False(await db.EndFacts.AnyAsync(x => x.TournamentId == a.TournamentId));
    }
}
