using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RoyaltyFlow.Tests;

public sealed class ApiTests
{
    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task HealthIsAvailableAndSwaggerIsDevelopmentOnly(
        string environment, HttpStatusCode swaggerStatus)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = configured.CreateClient();

        using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
        Assert.Equal("Healthy", health.RootElement.GetProperty("status").GetString());

        using var document = await client.GetAsync("/swagger/v1/swagger.json");
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(swaggerStatus, document.StatusCode);
        Assert.Equal(swaggerStatus, ui.StatusCode);

        if (document.IsSuccessStatusCode)
        {
            using var schema = JsonDocument.Parse(await document.Content.ReadAsStringAsync());
            Assert.True(schema.RootElement.GetProperty("paths").GetProperty("/health")
                .TryGetProperty("get", out _));
        }
    }
}
