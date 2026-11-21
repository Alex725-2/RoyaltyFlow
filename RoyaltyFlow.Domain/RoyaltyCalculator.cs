namespace RoyaltyFlow.Domain;

public sealed class RoyaltyCalculator
{
    public const decimal RatePerPlay = 0.015m;

    public IReadOnlyList<RoyaltyAccrual> Calculate(UsageRecord usage, RoyaltyContract contract)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.PlayCount);

        if (usage.TrackId != contract.TrackId)
            throw new ArgumentException("Usage and contract must refer to the same track.", nameof(usage));

        var total = decimal.Round(usage.PlayCount * RatePerPlay, 2, MidpointRounding.AwayFromZero);
        var totalCents = decimal.ToInt64(total * 100m);
        var quotas = contract.Shares.Select(share =>
        {
            var exactCents = totalCents * share.Percentage / 100m;
            var baseCents = decimal.ToInt64(exactCents);
            return (share.RightsHolderId, BaseCents: baseCents, Remainder: exactCents - baseCents);
        })
        .OrderByDescending(quota => quota.Remainder)
        .ThenBy(quota => quota.RightsHolderId)
        .ToArray();

        var remainingCents = totalCents - quotas.Sum(quota => quota.BaseCents);
        return Array.AsReadOnly(quotas.Select((quota, index) => new RoyaltyAccrual(
            usage.EventId, contract.Id, quota.RightsHolderId,
            (quota.BaseCents + (index < remainingCents ? 1 : 0)) / 100m)).ToArray());
    }
}
