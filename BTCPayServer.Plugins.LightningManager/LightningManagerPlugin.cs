using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.LightningManager.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LightningManager;

public class LightningManagerPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.1" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var plugins = (PluginServiceCollection)services;

        plugins.AddSingleton<ILightningCapabilityService, LightningCapabilityService>();
        plugins.AddScoped<IStoreLightningManagerContextFactory, StoreLightningManagerContextFactory>();
        plugins.AddSingleton<ILightningManagerService, LightningManagerService>();
        plugins.AddUIExtension("lightning-nav", "LightningManager/LightningManagerNav");
    }
}
