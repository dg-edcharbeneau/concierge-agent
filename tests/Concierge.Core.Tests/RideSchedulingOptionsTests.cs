using Concierge.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Concierge.Core.Tests;

public sealed class RideSchedulingOptionsTests
{
    [Theory]
    [InlineData(null, 5)]
    [InlineData("12", 12)]
    [InlineData("2147483647", int.MaxValue)]
    public async Task BindsPositiveMinutesAndUsesSharedDefault(string? value, int expected)
    {
        using var host = CreateHost(value);
        await host.StartAsync();
        Assert.Equal(expected, host.Services.GetRequiredService<IOptions<RideSchedulingOptions>>()
            .Value.MinimumAdvanceNoticeMinutes);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task NonPositiveNoticeFailsStartupWithActionableConfigurationKey(string value)
    {
        using var host = CreateHost(value);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(RideSchedulingOptions.MinimumAdvanceNoticeKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("positive whole number of minutes", error.Message, StringComparison.Ordinal);
    }

    private static IHost CreateHost(string? value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        if (value is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RideSchedulingOptions.MinimumAdvanceNoticeKey] = value,
            });
        }
        builder.Services.AddOptions<RideSchedulingOptions>()
            .Bind(builder.Configuration.GetSection(RideSchedulingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return builder.Build();
    }
}
