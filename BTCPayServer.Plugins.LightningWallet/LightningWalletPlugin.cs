using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.LightningWallet.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LightningWallet;

public class LightningWalletPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var plugins = (PluginServiceCollection)services;

        plugins.AddSingleton<ILightningCapabilityService, LightningCapabilityService>();
        plugins.AddScoped<IStoreLightningWalletContextFactory, StoreLightningWalletContextFactory>();
        plugins.AddSingleton<ILightningWalletService, LightningWalletService>();
        plugins.AddUIExtension("lightning-nav", "LightningWallet/LightningWalletNav");
    }
}
