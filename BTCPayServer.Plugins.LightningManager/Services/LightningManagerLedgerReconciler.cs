#nullable enable
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.LightningManager.Services;

public class LightningManagerLedgerReconciler(IStoreLightningLedgerService ledgerService) : IPeriodicTask
{
    public Task Do(CancellationToken cancellationToken)
    {
        return ledgerService.ReconcilePendingSendsAsync(cancellationToken);
    }
}
