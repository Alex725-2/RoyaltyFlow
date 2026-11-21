using System.ComponentModel.DataAnnotations;
using RoyaltyFlow.Domain;

namespace RoyaltyFlow.Api;

public sealed record CreateNamedRequest(
    [Required, StringLength(200)] string Name, Guid? Id = null);

public sealed record CreateTrackRequest(
    Guid ArtistId, [Required, StringLength(200)] string Title, Guid? Id = null);

public sealed record ContractShareRequest(Guid RightsHolderId, decimal Percentage);

public sealed record CreateContractRequest(
    Guid TrackId, DateOnly ValidFrom, DateOnly? ValidTo,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<ContractShareRequest> Shares)
{
    public RoyaltyContract ToDomain()
    {
        if (TrackId == Guid.Empty || ValidFrom == default || ValidTo <= ValidFrom)
            throw new ArgumentException("TrackId and ValidFrom are required. ValidTo must be later than ValidFrom.");
        if (Shares is null || Shares.Count is < 1 or > 100 || Shares.Any(share => share is null))
            throw new ArgumentException("Provide 1 to 100 non-null shares.");
        return new RoyaltyContract(Guid.NewGuid(), TrackId,
            Shares.Select(share => new RoyaltyShare(share.RightsHolderId, share.Percentage)));
    }
}

public sealed record ContractResponse(Guid Id, Guid TrackId, DateOnly ValidFrom, DateOnly? ValidTo,
    IReadOnlyList<ContractShareRequest> Shares);

public sealed class PageQuery
{
    [Range(1, 1_000_000)]
    public int Page { get; init; } = 1;

    [Range(1, 200)]
    public int PageSize { get; init; } = 50;

    public int Offset => (Page - 1) * PageSize;
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasNext)
{
    public static PageResult<T> From(IReadOnlyList<T> rows, PageQuery page) =>
        new(rows.Take(page.PageSize).ToArray(), page.Page, page.PageSize, rows.Count > page.PageSize);
}
