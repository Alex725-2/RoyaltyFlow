using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoyaltyFlow.Domain;
using RoyaltyFlow.Infrastructure;

namespace RoyaltyFlow.Api;

[ApiController]
[Route("api")]
public sealed class CatalogController(RoyaltyDbContext db) : ControllerBase
{
    [HttpGet("artists")]
    public async Task<PageResult<Artist>> Artists([FromQuery(Name = "")] PageQuery page, CancellationToken token) =>
        await FetchAsync(db.Artists.OrderBy(row => row.Id), page, token);

    [HttpPost("artists")]
    [ProducesResponseType<Artist>(StatusCodes.Status201Created)]
    public async Task<ActionResult<Artist>> CreateArtist(CreateNamedRequest request, CancellationToken token)
    {
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name))
            return Problem(statusCode: 400, title: "Provide a valid artist ID and name.");
        var artist = new Artist(request.Id ?? Guid.NewGuid(), request.Name.Trim());
        db.Artists.Add(artist);
        await db.SaveChangesAsync(token);
        return StatusCode(201, artist);
    }

    [HttpGet("tracks")]
    public async Task<PageResult<Track>> Tracks([FromQuery(Name = "")] PageQuery page, CancellationToken token) =>
        await FetchAsync(db.Tracks.OrderBy(row => row.Id), page, token);

    [HttpPost("tracks")]
    [ProducesResponseType<Track>(StatusCodes.Status201Created)]
    public async Task<ActionResult<Track>> CreateTrack(CreateTrackRequest request, CancellationToken token)
    {
        if (request.Id == Guid.Empty || request.ArtistId == Guid.Empty || string.IsNullOrWhiteSpace(request.Title))
            return Problem(statusCode: 400, title: "Provide a valid track ID, artist ID and title.");
        var track = new Track(request.Id ?? Guid.NewGuid(), request.ArtistId, request.Title.Trim());
        db.Tracks.Add(track);
        await db.SaveChangesAsync(token);
        return StatusCode(201, track);
    }

    [HttpGet("rights-holders")]
    public async Task<PageResult<RightsHolder>> RightsHolders([FromQuery(Name = "")] PageQuery page, CancellationToken token) =>
        await FetchAsync(db.RightsHolders.OrderBy(row => row.Id), page, token);

    [HttpPost("rights-holders")]
    [ProducesResponseType<RightsHolder>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RightsHolder>> CreateRightsHolder(CreateNamedRequest request, CancellationToken token)
    {
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name))
            return Problem(statusCode: 400, title: "Provide a valid rights holder ID and name.");
        var holder = new RightsHolder(request.Id ?? Guid.NewGuid(), request.Name.Trim());
        db.RightsHolders.Add(holder);
        await db.SaveChangesAsync(token);
        return StatusCode(201, holder);
    }

    [HttpGet("contracts")]
    public async Task<PageResult<ContractResponse>> Contracts([FromQuery(Name = "")] PageQuery page, CancellationToken token)
    {
        var rows = await db.Contracts.AsNoTracking().Include(row => row.Shares).OrderBy(row => row.Id)
            .Skip(page.Offset).Take(page.PageSize + 1).ToListAsync(token);
        return PageResult<ContractResponse>.From(rows.Select(ToResponse).ToArray(), page);
    }

    [HttpPost("contracts")]
    [ProducesResponseType<ContractResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ContractResponse>> CreateContract(CreateContractRequest request, CancellationToken token)
    {
        RoyaltyContract contract;
        try { contract = request.ToDomain(); }
        catch (ArgumentException) { return Problem(statusCode: 400, title: "Invalid contract period, track or shares."); }

        var row = new ContractRow(contract.Id, contract.TrackId, request.ValidFrom, request.ValidTo);
        row.Shares.AddRange(contract.Shares.Select(share => new ShareRow(row.Id, share.RightsHolderId, share.Percentage)));
        db.Contracts.Add(row);
        await db.SaveChangesAsync(token);
        return StatusCode(201, ToResponse(row));
    }

    private static ContractResponse ToResponse(ContractRow row) => new(row.Id, row.TrackId, row.ValidFrom, row.ValidTo,
        row.Shares.OrderBy(share => share.RightsHolderId)
            .Select(share => new ContractShareRequest(share.RightsHolderId, share.Percentage)).ToArray());

    internal static async Task<PageResult<T>> FetchAsync<T>(IQueryable<T> query, PageQuery page,
        CancellationToken token) where T : class =>
        PageResult<T>.From(await query.AsNoTracking().Skip(page.Offset).Take(page.PageSize + 1).ToListAsync(token), page);
}
