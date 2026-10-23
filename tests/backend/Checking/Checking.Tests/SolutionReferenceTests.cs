using Matigr.Checking.Domain;

namespace Matigr.Checking.Tests;

public class SolutionReferenceTests
{
    [Fact]
    public void Same_immutable_snapshot_is_equal_but_changed_material_is_not()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var acceptedAt = DateTimeOffset.Parse("2026-10-23T09:00:00+06:00");
        var first = new SolutionReference(ids[0], ids[1], ids[2], ids[3], ids[4], "prepared-a", acceptedAt, 1);
        var replay = new SolutionReference(ids[0], ids[1], ids[2], ids[3], ids[4], "prepared-a", acceptedAt, 1);
        var changed = new SolutionReference(ids[0], ids[1], ids[2], ids[3], Guid.NewGuid(), "prepared-b", acceptedAt, 1);
        Assert.Equal(first, replay);
        Assert.NotEqual(first, changed);
        Assert.Equal(TimeSpan.Zero, first.AcceptedAt.Offset);
        Assert.All(typeof(SolutionReference).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Source_version_must_be_positive(long version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolutionReference(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "prepared-a", DateTimeOffset.UtcNow, version));
}
