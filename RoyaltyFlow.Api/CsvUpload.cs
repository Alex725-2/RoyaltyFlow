using System.Buffers;
using System.Security.Cryptography;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RoyaltyFlow.Api;

public sealed class CsvUpload : IAsyncDisposable
{
    public const int MaxBytes = 128 * 1024 * 1024;
    private readonly Stream stream;

    private CsvUpload(Stream stream, string sha256)
    {
        this.stream = stream;
        Sha256 = sha256;
    }

    public Stream Stream => stream;
    public string Sha256 { get; }

    public static async Task<CsvUpload> ReadAsync(Stream body, string directory, CancellationToken token,
        int limit = MaxBytes)
    {
        Directory.CreateDirectory(directory);
        var file = new FileStream(Path.Combine(directory, Path.GetRandomFileName()), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, 65536), token)) != 0)
            {
                size += read;
                if (size > limit)
                    throw new BadHttpRequestException("CSV exceeds the upload size limit.", 413);
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), token);
            }
            file.Position = 0;
            return new(file, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class CsvBodyAttribute : Attribute;

public sealed class CsvOpenApiFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.MethodInfo.IsDefined(typeof(CsvBodyAttribute), false))
            return;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "UTF-8 CSV, up to 128 MiB. Send the file as the request body.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["text/csv"] = new() { Schema = new OpenApiSchema { Type = "string", Format = "binary" } }
            }
        };
    }
}
