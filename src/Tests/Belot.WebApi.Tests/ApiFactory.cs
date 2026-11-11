namespace Belot.WebApi.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly bool revealHandsEnabled;

    private readonly string? debugAccessKey;

    public ApiFactory(bool revealHandsEnabled = false, string? debugAccessKey = null)
    {
        this.revealHandsEnabled = revealHandsEnabled;
        this.debugAccessKey = debugAccessKey;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:PermitLimit"] = "10000",
                ["Debug:RevealHandsEnabled"] = this.revealHandsEnabled.ToString(),
                ["Debug:AccessKey"] = this.debugAccessKey,
            }));
    }
}
