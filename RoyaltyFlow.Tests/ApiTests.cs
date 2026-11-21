using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoyaltyFlow.Api;
using RoyaltyFlow.Application;
using Xunit;

namespace RoyaltyFlow.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task RoyaltySummaryBindsPageAndPageSizeWithoutRepeatingTracks()
    {
        var rows = new[]
        {
            new UsageSummary(Guid.Parse("00000000-0001-0000-0200-000000000000"), "First", 2, 3m, 0.05m),
            new UsageSummary(Guid.Parse("00000000-0002-0000-0200-000000000000"), "Second", 1, 1m, 0.02m),
            new UsageSummary(Guid.Parse("00000000-0003-0000-0200-000000000000"), "Third", 1, 2m, 0.03m)
        };
        using var factory = new WebApplicationFactory<Program>();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:RoyaltyFlow"] = "Host=localhost;Database=unused" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRoyaltyAnalytics>();
                services.AddSingleton<IRoyaltyAnalytics>(new InMemoryAnalytics(rows));
            });
        });
        using var client = configured.CreateClient();
        const string url = "/api/royalties/summary?from=2025-01-01&to=2025-01-31&pageSize=2";

        var first = Assert.IsType<PageResult<UsageSummary>>(
            await client.GetFromJsonAsync<PageResult<UsageSummary>>($"{url}&page=1"));
        var second = Assert.IsType<PageResult<UsageSummary>>(
            await client.GetFromJsonAsync<PageResult<UsageSummary>>($"{url}&page=2"));

        Assert.Empty(first.Items.Select(row => row.TrackId).Intersect(second.Items.Select(row => row.TrackId)));
        Assert.Equal((1, 2, true), (first.Page, first.PageSize, first.HasNext));
        Assert.Equal((2, 2, false), (second.Page, second.PageSize, second.HasNext));
        Assert.Equal(rows, first.Items.Concat(second.Items));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task HealthAndSwaggerAreAvailableOnTheRequestOrigin(string environment)
    {
        const string configurationMarker = "SWAGGER_TEST_PRIVATE_CONFIGURATION";
        var origin = new Uri("https://royalty.test");
        using var factory = new WebApplicationFactory<Program>();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:RoyaltyFlow"] = configurationMarker }));
        });
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = origin });

        using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
        Assert.Equal("Healthy", health.RootElement.GetProperty("status").GetString());

        using var ui = await client.GetAsync("/swagger");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal(new Uri(origin, "/swagger/index.html"), ui.RequestMessage!.RequestUri);
        var html = await ui.Content.ReadAsStringAsync();
        Assert.Contains("index.js", html);
        Assert.DoesNotContain(configurationMarker, html);

        var initializer = await client.GetStringAsync("/swagger/index.js");
        Assert.Matches(@"""url""\s*:\s*""v1/swagger\.json""", initializer);
        Assert.DoesNotContain(configurationMarker, initializer);

        var json = await client.GetStringAsync("/swagger/v1/swagger.json");
        Assert.DoesNotContain(configurationMarker, json);
        using var schema = JsonDocument.Parse(json);
        Assert.True(schema.RootElement.GetProperty("paths").GetProperty("/health")
            .TryGetProperty("get", out _));
        if (schema.RootElement.TryGetProperty("servers", out var servers))
            Assert.All(servers.EnumerateArray(), server => Assert.Equal("/", server.GetProperty("url").GetString()));
    }

    private sealed class InMemoryAnalytics(IReadOnlyList<UsageSummary> rows) : IRoyaltyAnalytics
    {
        public Task<IReadOnlyList<UsageSummary>> SummarizeAsync(DateOnly from, DateOnly to,
            int offset, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UsageSummary>>(rows.Skip(offset).Take(limit).ToArray());
    }
}
