using RoyaltyFlow.Domain;
using Xunit;

namespace RoyaltyFlow.Tests;

public sealed class RoyaltyCalculatorTests
{
    private static readonly Guid TrackId = Guid.Parse("00000000-0000-0000-0000-000000000010");
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Third = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private readonly RoyaltyCalculator calculator = new();

    [Fact]
    public void CalculatesSeventyThirtySplit()
    {
        var result = Calculate(100, new RoyaltyShare(First, 70m), new RoyaltyShare(Second, 30m));
        Assert.Equal(1.05m, result.Single(item => item.RightsHolderId == First).Amount);
        Assert.Equal(0.45m, result.Single(item => item.RightsHolderId == Second).Amount);
    }

    [Fact]
    public void AllocatesToMultipleRightsHoldersByLargestRemainder()
    {
        var result = Calculate(2, new RoyaltyShare(First, 50m), new RoyaltyShare(Second, 30m),
            new RoyaltyShare(Third, 20m)).ToDictionary(item => item.RightsHolderId, item => item.Amount);

        Assert.Equal(0.01m, result[First]);
        Assert.Equal(0.01m, result[Second]);
        Assert.Equal(0.01m, result[Third]);
    }

    [Fact]
    public void RoundsMidpointAwayFromZero()
    {
        Assert.Equal(0.02m, Assert.Single(Calculate(1, new RoyaltyShare(First, 100m))).Amount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(1_000_000)]
    [InlineData(6148914691236517204L)]
    public void PreservesRoundedTotal(long plays)
    {
        var result = Calculate(plays, new RoyaltyShare(First, 33.3333m),
            new RoyaltyShare(Second, 33.3333m), new RoyaltyShare(Third, 33.3334m));

        Assert.Equal(decimal.Round(plays * RoyaltyCalculator.RatePerPlay, 2,
            MidpointRounding.AwayFromZero), result.Sum(item => item.Amount));
        Assert.All(result, item =>
        {
            Assert.True(item.Amount >= 0m);
            Assert.Equal(decimal.Round(item.Amount, 2), item.Amount);
        });
    }

    [Fact]
    public void BreaksTiesByRightsHolderIdRegardlessOfInputOrder()
    {
        var first = new RoyaltyShare(First, 50m);
        var second = new RoyaltyShare(Second, 50m);
        var forward = Calculate(2, first, second);
        var reverse = Calculate(2, second, first);

        Assert.Equal(forward.Select(item => item.Amount), reverse.Select(item => item.Amount));
        Assert.Equal(0.02m, reverse.Single(item => item.RightsHolderId == First).Amount);
    }

    [Theory]
    [InlineData(60, 30)]
    [InlineData(80, 30)]
    [InlineData(-10, 110)]
    [InlineData(0, 100)]
    public void RejectsInvalidShares(int first, int second)
    {
        Assert.Throws<ArgumentException>(() => Contract(new RoyaltyShare(First, first),
            new RoyaltyShare(Second, second)));
    }

    [Fact]
    public void RejectsDuplicateHoldersAndExcessivePrecision()
    {
        Assert.Throws<ArgumentException>(() => Contract(new RoyaltyShare(First, 50m),
            new RoyaltyShare(First, 50m)));
        Assert.Throws<ArgumentException>(() => Contract(new RoyaltyShare(First, 50.00001m),
            new RoyaltyShare(Second, 49.99999m)));
        Assert.Throws<ArgumentException>(() => Contract());
    }

    [Fact]
    public void ReturnsZeroForZeroPlays()
    {
        var result = Calculate(0, new RoyaltyShare(First, 70m), new RoyaltyShare(Second, 30m));
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal(0m, item.Amount));
    }

    [Fact]
    public void RejectsNegativePlays()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calculate(-1, new RoyaltyShare(First, 100m)));
    }

    [Fact]
    public void ThrowsWhenTotalExceedsSupportedCentRange()
    {
        Assert.Throws<OverflowException>(() => Calculate(6148914691236517205L,
            new RoyaltyShare(First, 100m)));
    }

    private IReadOnlyList<RoyaltyAccrual> Calculate(long plays, params RoyaltyShare[] shares) =>
        calculator.Calculate(new UsageRecord(Guid.NewGuid(), TrackId, "DemoStream", "DE",
            new DateOnly(2025, 1, 1), plays), Contract(shares));

    private static RoyaltyContract Contract(params RoyaltyShare[] shares) =>
        new(Guid.NewGuid(), TrackId, shares);
}
