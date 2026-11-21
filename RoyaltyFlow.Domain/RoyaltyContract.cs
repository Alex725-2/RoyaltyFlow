namespace RoyaltyFlow.Domain;

public sealed class RoyaltyContract
{
    public RoyaltyContract(Guid id, Guid trackId, IEnumerable<RoyaltyShare> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        var copy = shares.ToArray();

        if (copy.Any(share => share is null || share.RightsHolderId == Guid.Empty ||
            share.Percentage <= 0m || share.Percentage > 100m ||
            decimal.Round(share.Percentage, 4) != share.Percentage) ||
            copy.Sum(share => share.Percentage) != 100m ||
            copy.Select(share => share.RightsHolderId).Distinct().Count() != copy.Length)
        {
            throw new ArgumentException(
                "Shares must be positive, have at most four decimal places, total 100%, and belong to distinct rights holders.",
                nameof(shares));
        }

        Id = id;
        TrackId = trackId;
        Shares = Array.AsReadOnly(copy);
    }

    public Guid Id { get; }
    public Guid TrackId { get; }
    public IReadOnlyList<RoyaltyShare> Shares { get; }
}
