using System.Globalization;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.LND;
using NBitcoin;
using NBitcoin.DataEncoders;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.LightningManager.Wallet;

public sealed record WalletNode(StoreLightningManagerContext Context, string Identity, bool PrivateRouteHints = false);

public sealed class WalletService(
    IStoreLightningManagerContextFactory contexts,
    LightningManagerService manager,
    WalletRepository repository,
    StoreRepository stores,
    ILogger<WalletService> logger,
    IHostApplicationLifetime lifetime)
{
    public static bool Supports(StoreLightningManagerContext context) => context.IsConfigured &&
        context.BackendType is "clightning" or "lnd-rest" or "lnd-grpc";

    public async Task<WalletNode> GetNodeAsync(StoreData store, CancellationToken token)
    {
        var context = contexts.Create(store, "BTC");
        if (!Supports(context)) throw new WalletException("Wallet Mode supports LND and Core Lightning only.");
        // NodeInfoList contains advertised addresses, which private nodes may omit.
        var pubkey = context.Client switch
        {
            CLightningClient cln => (await cln.GetInfoAsync(token)).Id,
            LndClient lnd => (await lnd.SwaggerClient.GetInfoAsync(token)).Identity_pubkey,
            _ => null
        };
        if (string.IsNullOrEmpty(pubkey)) throw new WalletException("The Lightning node identity is unavailable.");
        return new WalletNode(context, $"{context.Network!.NBitcoinNetwork.Name}:{new PubKey(pubkey)}", store.GetStoreBlob().LightningPrivateRouteHints);
    }

    public SendPreviewViewModel Preview(WalletNode node, string? bolt11, string? amount, string? maxFee)
    {
        var invoice = bolt11?.Trim();
        if (invoice?.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase) is true) invoice = invoice[10..];
        if (!manager.TryCreateSendPreview(node.Context, invoice, amount, maxFee, out var preview, out var error))
            throw new WalletException(error ?? "Invalid Lightning invoice.");
        return preview!;
    }

    public async Task<WalletOperation> ReceiveAsync(WalletNode node, string userId, long amountSats, string? description, CancellationToken token)
    {
        repository.RequireReady();
        if (amountSats <= 0 || amountSats > 2_100_000_000_000_000)
            throw new WalletException("Enter a positive whole number of sats within the Bitcoin supply.");
        description = description?.Trim() ?? "";
        if (description.Length > 200) throw new WalletException("Description must be at most 200 characters.");
        var invoice = await node.Context.Client!.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(amountSats), description, TimeSpan.FromHours(1))
            { PrivateRouteHints = node.PrivateRouteHints }, token);
        var parsed = BOLT11PaymentRequest.Parse(invoice.BOLT11, node.Context.Network!.NBitcoinNetwork);
        var operation = new WalletOperation
        {
            StoreId = node.Context.StoreId, UserId = userId, NodeIdentity = node.Identity,
            Direction = "Incoming", PaymentHash = parsed.PaymentHash!.ToString(), InvoiceId = invoice.Id,
            Bolt11 = invoice.BOLT11, Description = description, AmountMsat = checked(amountSats * 1000),
            ExpiresAt = parsed.ExpiryDate
        };
        if (!await repository.InsertAsync(operation, token)) throw new WalletException("This invoice is already recorded.");
        return operation;
    }

    public async Task<WalletOperation> PayAsync(WalletNode node, string userId, SendPreviewViewModel preview)
    {
        var validated = Preview(node, preview.Bolt11, preview.UserAmountSats?.ToString(CultureInfo.InvariantCulture),
            preview.MaxFeeSats?.ToString(CultureInfo.InvariantCulture));
        repository.RequireReady();
        // Do not import a payment already made elsewhere or resubmit an in-flight hash.
        // This lookup is advisory; the durable claim and settlement lookup still cover races.
        using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping))
        {
            lookup.CancelAfter(TimeSpan.FromSeconds(10));
            var existing = await node.Context.Client!.GetPayment(validated.PaymentHash, lookup.Token).WaitAsync(lookup.Token);
            if (existing is not null && existing.Status != LightningPaymentStatus.Failed)
                throw new WalletException("This payment is already recorded or known to the node. Check its status before retrying.");
        }
        var operation = new WalletOperation
        {
            StoreId = node.Context.StoreId, UserId = userId, NodeIdentity = node.Identity,
            Direction = "Outgoing", PaymentHash = validated.PaymentHash, Bolt11 = validated.Bolt11,
            AmountMsat = validated.PaymentAmount.MilliSatoshi, MaxFeeSats = validated.MaxFeeSats,
            Description = validated.Description, State = "Submitting", ExpiresAt = validated.ExpiresAt
        };
        // The unique database insert is the durable claim, including across stores/processes.
        // If commit status is unknown, throw without contacting the Lightning backend.
        if (!await repository.InsertAsync(operation, lifetime.ApplicationStopping))
            throw new WalletException("This payment is already recorded on this node. Check its status before retrying.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await manager.SendAsync(node.Context, validated.Bolt11,
                validated.UserAmountSats?.ToString(CultureInfo.InvariantCulture),
                validated.MaxFeeSats?.ToString(CultureInfo.InvariantCulture), timeout.Token);
            operation.State = result.Payment?.Status switch
            {
                LightningPaymentStatus.Complete => "Settled",
                LightningPaymentStatus.Failed => "Failed",
                LightningPaymentStatus.Pending => "Pending",
                _ => "Unknown"
            };
            // Persist the node's actual amount, separately from the passkey-authorized amount.
            if (operation.State == "Settled")
            {
                try
                {
                    var payment = await node.Context.Client!.GetPayment(operation.PaymentHash, timeout.Token);
                    if (payment?.Status == LightningPaymentStatus.Complete)
                    {
                        operation.SettledAmountMsat = payment.Amount?.MilliSatoshi;
                        operation.FeeMsat = payment.Fee?.MilliSatoshi;
                    }
                }
                catch (Exception ex) { logger.LogWarning("Wallet settlement lookup unavailable ({ErrorType})", ex.GetType().Name); }
            }
        }
        catch (Exception ex)
        {
            operation.State = "Unknown";
            logger.LogWarning("Wallet payment outcome is unknown ({ErrorType})", ex.GetType().Name);
        }
        try { await repository.UpdateAsync(operation, lifetime.ApplicationStopping); }
        catch (Exception ex) { logger.LogWarning("Wallet payment result could not be saved ({ErrorType})", ex.GetType().Name); }
        return operation;
    }

    public static async Task<LightningInvoiceStatus?> InvoiceStatusAsync(WalletNode node, string invoiceId, CancellationToken token)
    {
        if (node.Context.Client is LndClient lnd)
        {
            // The generic adapter drops CANCELED invoices and infers expiry from the clock,
            // including ACCEPTED invoices that may still settle. Use LND's actual state.
            var invoice = await lnd.SwaggerClient.LookupInvoiceAsync(Encoders.Hex.DecodeData(invoiceId), token);
            return invoice.State?.ToUpperInvariant() switch
            {
                "SETTLED" => LightningInvoiceStatus.Paid,
                "CANCELED" => LightningInvoiceStatus.Expired,
                "OPEN" or "ACCEPTED" => LightningInvoiceStatus.Unpaid,
                _ => throw new WalletException("Invoice state is unavailable.")
            };
        }
        return (await node.Context.Client!.GetInvoice(invoiceId, token))?.Status;
    }

    public async Task<WalletOperation> ReconcileAsync(WalletNode node, WalletOperation operation, CancellationToken token)
    {
        if (!operation.RequiresReconciliation || operation.NodeIdentity != node.Identity) return operation;
        if (operation.Direction == "Incoming")
        {
            var status = await InvoiceStatusAsync(node, operation.InvoiceId, token);
            operation.State = status switch
            {
                LightningInvoiceStatus.Paid => "Settled",
                LightningInvoiceStatus.Expired => "Expired",
                _ => operation.State
            };
        }
        else
        {
            var payment = await node.Context.Client!.GetPayment(operation.PaymentHash, token);
            if (operation.State == "Settled" && (payment?.Status != LightningPaymentStatus.Complete || payment.Amount is null))
            {
                await repository.DeferAsync(operation.Id, token);
                return operation;
            }
            // A lookup can see an older failed Manager attempt before this submission
            // reaches the node. Give the 60-second executor time to finish, but still
            // recover a Submitting row after a process crash. Failed hashes stay reconcilable.
            if (operation.State == "Submitting" && payment?.Status == LightningPaymentStatus.Failed &&
                operation.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-2))
            {
                await repository.DeferAsync(operation.Id, token);
                return operation;
            }
            // A settled row may only need its actual amount backfilled. Lookup
            // unavailability must never undo an already confirmed settlement.
            if (operation.State != "Settled")
                operation.State = payment?.Status switch
                {
                    LightningPaymentStatus.Complete => "Settled",
                    LightningPaymentStatus.Failed => "Failed",
                    LightningPaymentStatus.Pending => "Pending",
                    _ => "Unknown"
                };
            if (payment?.Status == LightningPaymentStatus.Complete)
                operation.SettledAmountMsat = payment.Amount?.MilliSatoshi;
            operation.FeeMsat = payment?.Fee?.MilliSatoshi;
        }
        await repository.UpdateAsync(operation, token);
        return operation;
    }

    public async Task ReconcilePendingAsync(CancellationToken token)
    {
        foreach (var operation in await repository.PendingAsync(token))
        {
            try
            {
                var store = await stores.FindStore(operation.StoreId);
                if (store is null)
                {
                    await repository.DeferAsync(operation.Id, token);
                    continue;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var node = await GetNodeAsync(store, timeout.Token);
                if (node.Identity == operation.NodeIdentity) await ReconcileAsync(node, operation, timeout.Token);
                else await repository.DeferAsync(operation.Id, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Wallet reconciliation deferred ({ErrorType})", ex.GetType().Name);
                await repository.DeferAsync(operation.Id, token);
            }
        }
    }
}

public sealed class WalletHostedService(WalletRepository repository, WalletService wallet, ILogger<WalletHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                if (!repository.Ready) await repository.InitializeAsync(stoppingToken);
                await wallet.ReconcilePendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Wallet storage or reconciliation unavailable ({ErrorType})", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
