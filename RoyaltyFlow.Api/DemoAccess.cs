using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RoyaltyFlow.Api;

internal sealed class DemoAccess(RequestDelegate next, IConfiguration configuration)
{
    private readonly string? demoPassword = configuration["DemoAccess:Password"];
    private readonly string? writeToken = ReadWriteToken(configuration);

    internal static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-RoyaltyFlow-Data"] = "[DEMO] tracks: fictional rights holders, contracts, usage and royalties";
        var read = IsRead(context.Request.Method);
        var header = context.Request.Headers.Authorization.ToString();
        var demo = false;
        if (header.Length <= 1024 && AuthenticationHeaderValue.TryParse(header, out var auth)
            && auth.Parameter is { } credential)
        {
            if (auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                && writeToken is not null
                && Matches(credential, writeToken))
            {
                await next(context);
                return;
            }

            if (auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(demoPassword))
            {
                var decoded = new byte[credential.Length];
                if (Convert.TryFromBase64String(credential, decoded, out var length))
                    demo = Matches(Encoding.UTF8.GetString(decoded, 0, length), $"demo:{demoPassword}");
            }
        }

        if (read && demo)
        {
            await next(context);
            return;
        }
        if (demo)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = read
            ? "Basic realm=\"RoyaltyFlow demo\", charset=\"UTF-8\""
            : "Bearer";
    }

    private static bool Matches(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static string? ReadWriteToken(IConfiguration configuration)
    {
        var token = configuration["DemoAccess:WriteToken"];
        return token is { Length: >= 32 and <= 128 }
            && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            && !string.Equals(token, configuration["DemoAccess:Password"], StringComparison.Ordinal)
            ? token : null;
    }
}

internal sealed class DemoAccessOpenApiFilter : IOperationFilter
{
    internal const string Demo = "DemoBasic";
    internal const string Writer = "OperatorBearer";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) != true)
            return;

        operation.Security = [Require(Writer)];
        if (context.ApiDescription.HttpMethod is { } method && DemoAccess.IsRead(method))
            operation.Security.Add(Require(Demo));
    }

    private static OpenApiSecurityRequirement Require(string id) => new()
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = id } }] = []
    };
}
