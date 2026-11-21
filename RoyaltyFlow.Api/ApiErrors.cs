using Microsoft.EntityFrameworkCore;
using Npgsql;
using RoyaltyFlow.Application;

namespace RoyaltyFlow.Api;

internal static class ApiErrors
{
    public static (int Status, string Title) Describe(Exception error)
    {
        if (error is DbUpdateException { InnerException: { } cause })
            error = cause;
        return error switch
        {
            CsvFormatException csv => (400, csv.Message),
            BadHttpRequestException { StatusCode: 413 } => (413, "CSV exceeds the upload size limit."),
            BadHttpRequestException { StatusCode: 415 } => (415, "Send the CSV as a text/csv request body."),
            BadHttpRequestException => (400, "Invalid request."),
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => (409, "The identifier already exists."),
            PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } => (409, "The contract period overlaps an existing version."),
            PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } => (422, "A referenced catalog entry does not exist."),
            PostgresException { SqlState: "42P01" } => (503, "The PostgreSQL schema is not ready."),
            PostgresException { SqlState: "28P01" or "3D000" or "57P03" } => (503, "PostgreSQL is unavailable."),
            DatabaseUnavailableException or TimeoutException => (503, "PostgreSQL is unavailable."),
            NpgsqlException postgres when postgres.IsTransient => (503, "PostgreSQL is unavailable."),
            _ => (500, "The request could not be completed.")
        };
    }
}
