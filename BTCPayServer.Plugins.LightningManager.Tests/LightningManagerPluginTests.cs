using System.Reflection;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerPluginTests
{
    [Fact]
    public void Execute_RegistersNavigationAndCompilesPluginViews()
    {
        var services = new ServiceCollection();
        new LightningManagerPlugin().Execute(services);

        using var provider = services.BuildServiceProvider();
        var extensions = provider.GetServices<IUIExtension>()
            .Select(extension => (extension.Location, extension.Partial))
            .OrderBy(extension => extension.Location, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                ("store-integrations-nav", "LightningManager/LightningManagerStoreNav")
            ],
            extensions);

        var compiledViews = typeof(LightningManagerPlugin).Assembly
            .GetCustomAttributes<RazorCompiledItemAttribute>()
            .Select(attribute => attribute.Identifier)
            .ToHashSet(StringComparer.Ordinal);

        string[] expectedViews =
        [
            "/Views/LightningManager/Channels.cshtml",
            "/Views/LightningManager/Overview.cshtml",
            "/Views/LightningManager/Peers.cshtml",
            "/Views/LightningManager/Send.cshtml",
            $"/Views/Shared/{extensions[0].Partial}.cshtml"
        ];
        Assert.All(expectedViews, view => Assert.Contains(view, compiledViews));
    }
}
