namespace RoyaltyFlow.Domain;

public sealed record Artist(Guid Id, string Name);
public sealed record Track(Guid Id, Guid ArtistId, string Title);
public sealed record RightsHolder(Guid Id, string Name);
public sealed record RoyaltyShare(Guid RightsHolderId, decimal Percentage);
public sealed record UsageRecord(Guid EventId, Guid TrackId, string Platform, string Country,
    DateOnly UsageDate, long PlayCount);
public sealed record RoyaltyAccrual(Guid EventId, Guid ContractId, Guid RightsHolderId, decimal Amount);
