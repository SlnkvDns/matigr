using Matigr.Results.Application;

namespace Matigr.Results.Tests;

public class ReadinessStateTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Ready_tracks_the_results_database(bool database, bool ready)
    {
        Assert.Equal(ready, new ResultsReadinessState(database).Ready);
    }
}
