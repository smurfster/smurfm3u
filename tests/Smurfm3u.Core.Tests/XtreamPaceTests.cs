using Smurfm3u.Core.Xtream;

namespace Smurfm3u.Core.Tests;

public class XtreamPaceTests
{
    private static XtreamPace Pace(int most = 4, int stepUpAfter = 10) => new(most, stepUpAfter);

    private static void Answer(XtreamPace pace, int times)
    {
        for (var i = 0; i < times; i++) pace.Answered();
    }

    [Fact]
    public void StartsOptimistic()
    {
        Assert.Equal(4, Pace().Batch);
    }

    [Fact]
    public void HalvesOnARefusalAndStopsAtOne()
    {
        var pace = Pace();

        Assert.True(pace.Refused());
        Assert.Equal(2, pace.Batch);

        Assert.True(pace.Refused());
        Assert.Equal(1, pace.Batch);

        // Already as slow as it goes, so nothing changed and nothing is worth saying.
        Assert.False(pace.Refused());
        Assert.Equal(1, pace.Batch);
    }

    [Fact]
    public void ClimbsBackAfterEnoughCleanAnswers()
    {
        var pace = Pace(stepUpAfter: 10);
        pace.Refused();

        // One refusal doubles the patience, so ten is no longer enough.
        Answer(pace, 19);
        Assert.Equal(2, pace.Batch);

        Assert.True(pace.Answered());
        Assert.Equal(3, pace.Batch);
    }

    [Fact]
    public void NeverClimbsPastWhatItStartedAt()
    {
        var pace = Pace(most: 4, stepUpAfter: 1);
        pace.Refused();
        pace.Refused();

        Answer(pace, 500);

        Assert.Equal(4, pace.Batch);
        Assert.False(pace.Answered());
    }

    [Fact]
    public void ACleanAnswerAtFullPaceChangesNothing()
    {
        var pace = Pace();

        Assert.False(pace.Answered());
        Assert.Equal(4, pace.Batch);
    }

    [Fact]
    public void GetsSlowerToTrustAPanelThatKeepsRefusing()
    {
        // The point: a panel with a hard limit should settle there rather than spend the
        // whole walk climbing back up and being knocked down again.
        var pace = Pace(stepUpAfter: 10);

        pace.Refused();                  // patience 20
        Answer(pace, 20);
        Assert.Equal(3, pace.Batch);

        pace.Refused();                  // patience 40
        Assert.Equal(1, pace.Batch);

        Answer(pace, 39);
        Assert.Equal(1, pace.Batch);

        Assert.True(pace.Answered());
        Assert.Equal(2, pace.Batch);
    }

    [Fact]
    public void ARunOfCleanAnswersIsBrokenByARefusal()
    {
        var pace = Pace(stepUpAfter: 10);
        pace.Refused();

        Answer(pace, 15);
        pace.Refused();

        // The count starts again rather than carrying its progress across.
        Answer(pace, 15);
        Assert.Equal(1, pace.Batch);
    }

    [Fact]
    public void CopesWithNonsenseSettings()
    {
        Assert.Equal(1, new XtreamPace(0).Batch);
        Assert.Equal(1, new XtreamPace(-3).Batch);
        Assert.Equal(1, new XtreamPace(1).Most);
    }
}
