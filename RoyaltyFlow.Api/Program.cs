using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Npgsql;
using RoyaltyFlow.Api;
using RoyaltyFlow.Application;
using RoyaltyFlow.Domain;
using RoyaltyFlow.Infrastructure;

if (args is ["--healthcheck"])
{
    using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    try
    {
        using var result = await client.GetAsync("http://127.0.0.1:8080/health/live");
        Environment.ExitCode = result.StatusCode == System.Net.HttpStatusCode.OK ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
    {
        Environment.ExitCode = 1;
    }
    return;
}

var migrate = args is ["--migrate"];
var checkMigrations = args is ["--check-migrations"];
var builder = WebApplication.CreateBuilder(migrate || checkMigrations ? [] : args);
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.AllowInputFormatterExceptionMessages = false;
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
{
    try
    {
        var settings = DatabaseDeployment.ReadSettings(services.GetRequiredService<IConfiguration>());
        return new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
    }
    catch (ArgumentException) { throw new DatabaseUnavailableException(); }
});
builder.Services.AddDbContext<RoyaltyDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>(), provider => provider.SetPostgresVersion(16, 0)));
builder.Services.AddSingleton<RoyaltyCalculator>();
builder.Services.AddScoped<IUsageImporter, PostgresUsageImporter>();
builder.Services.AddScoped<IRoyaltyAnalytics, PostgresRoyaltyAnalytics>();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<CsvOpenApiFilter>();
    options.OperationFilter<DemoAccessOpenApiFilter>();
    options.AddSecurityDefinition(DemoAccessOpenApiFilter.Demo, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "basic", Description = "Demo account. Read only."
    });
    options.AddSecurityDefinition(DemoAccessOpenApiFilter.Writer, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Separate operator token for writes."
    });
    options.CustomSchemaIds(type => type.IsGenericType
        ? type.Name.Split('`')[0] + "Of" + string.Join("And", type.GetGenericArguments().Select(argument => argument.Name))
        : type.Name);
});

await using var app = builder.Build();
if (migrate || checkMigrations)
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var database = scope.ServiceProvider.GetRequiredService<RoyaltyDbContext>();
        if (checkMigrations)
        {
            var pending = await DatabaseDeployment.HasPendingMigrationsAsync(database, timeout.Token);
            Console.WriteLine(pending ? "RoyaltyFlow has pending migrations." : "RoyaltyFlow schema is up to date.");
            Environment.ExitCode = pending ? 10 : 0;
        }
        else
        {
            await DatabaseDeployment.MigrateAsync(database, timeout.Token);
            Console.WriteLine("RoyaltyFlow migrations applied.");
        }
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Migration failed ({error.GetType().Name}). Check the royaltyflow target, PostgreSQL 16, schema permissions and concurrent migrations.");
        Environment.ExitCode = 1;
    }
    return;
}

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var (status, title) = ApiErrors.Describe(error);
    await Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
}));
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("v1/swagger.json", "RoyaltyFlow API v1"));
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseMiddleware<DemoAccess>());

app.MapGet("/health", () => TypedResults.Ok(new { Status = "Healthy" })).WithName("Health");
app.MapGet("/health/live", () => TypedResults.Ok(new { Status = "Healthy" })).WithName("Liveness");
app.MapGet("/health/ready", async (HttpContext context, CancellationToken token) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        var database = context.RequestServices.GetRequiredService<RoyaltyDbContext>().Database;
        if (await database.CanConnectAsync(timeout.Token) && !(await database.GetPendingMigrationsAsync(timeout.Token)).Any())
            return Results.Ok(new { Status = "Ready" });
    }
    catch (Exception error) when (!token.IsCancellationRequested &&
        error is DatabaseUnavailableException or NpgsqlException or OperationCanceledException or TimeoutException)
    {
        // The readiness response is intentionally independent of connection and schema internals.
    }
    return Results.Problem(statusCode: 503, title: "PostgreSQL is unavailable or the schema is not ready.");
}).WithName("Readiness").Produces(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapControllers();
await app.RunAsync();

public partial class Program { }

internal sealed class DatabaseUnavailableException : Exception;
