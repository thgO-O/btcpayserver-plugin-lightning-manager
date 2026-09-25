using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.LightningManager.Filters;
using BTCPayServer.Plugins.LightningManager.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LightningManager;

public class LightningManagerPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.4" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<LightningManagerResultStore>();
        services.AddSingleton<LightningManagerChannelConfirmationStore>();
        services.AddSingleton<LightningManagerPaymentConfirmationStore>();
        services.AddSingleton<LightningManagerOperationGuard>();
        services.AddSingleton<IStoreLightningManagerContextFactory, StoreLightningManagerContextFactory>();
        services.AddSingleton<LightningManagerService>();
        services.AddScoped<LightningManagerInternalNodeAuthorizationFilter>();
        services.AddUIExtension("store-integrations-nav", "LightningManager/LightningManagerStoreNav");
    }
}
