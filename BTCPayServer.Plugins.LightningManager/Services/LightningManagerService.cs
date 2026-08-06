using System.Globalization;
using System.Diagnostics;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.LightningManager.Services;

public class SendExecutionResult
{
    public required ActionResultViewModel Result { get; init; }
    public SendResultDetailsViewModel? Payment { get; init; }
}

public sealed class LightningManagerService
{
    private const decimal DefaultChannelOpenFeeRate = 1.0m;
    private const long MinimumLndChannelAmountSats = 20_000;
    private const string PhoenixdAdapterAssemblyName = "BTCPayServer.Lightning.Phoenixd";
    private const string UnknownPaymentStatusMessage =
        "Payment status is unknown. Check the Lightning node before retrying.";
    private static readonly Version AffectedPhoenixdAdapterVersion = new(1, 7, 1, 0);
    private static readonly TimeSpan PaymentLookupTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger<LightningManagerService> _logger;
    private readonly LightningManagerOperationGuard _operationGuard;

    public LightningManagerService(
        ILogger<LightningManagerService> logger,
        LightningManagerOperationGuard operationGuard)
    {
        _logger = logger;
        _operationGuard = operationGuard;
    }

    public async Task PopulateOverviewAsync(
        OverviewViewModel model,
        StoreLightningManagerContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            return;
        }

        var nodeDisplayName = context.DisplayName;
        var nodeHost = context.NodeHost;
        string? version = null;
        int? blockHeight = null;
        long? peersCount = null;
        long? activeChannelsCount = null;
        long? inactiveChannelsCount = null;
        long? pendingChannelsCount = null;
        var phoenixdNetworkValidated = context.BackendType != LightningBackendTypes.Phoenixd;

        if (context.Capabilities.CanGetInfo)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var info = await context.Client.GetInfo(cancellationToken);
                nodeDisplayName = string.IsNullOrWhiteSpace(nodeDisplayName) ? info.Alias : nodeDisplayName;
                version = info.Version;
                blockHeight = info.BlockHeight;
                peersCount = info.PeersCount;
                activeChannelsCount = info.ActiveChannelsCount;
                inactiveChannelsCount = info.InactiveChannelsCount;
                pendingChannelsCount = info.PendingChannelsCount;
                if (context.BackendType == LightningBackendTypes.Phoenixd &&
                    IsAffectedPhoenixdAdapter(context.Client) &&
                    inactiveChannelsCount is not null &&
                    pendingChannelsCount > 0 &&
                    inactiveChannelsCount == pendingChannelsCount)
                {
                    // Phoenixd 1.7.1 reports every non-normal channel in both buckets.
                    // Neither classification is reliable, so do not present either bucket.
                    inactiveChannelsCount = null;
                    pendingChannelsCount = null;
                }
                foreach (var nodeInfo in info.NodeInfoList)
                {
                    model.NodeUris.Add(nodeInfo.ToString());
                }
                phoenixdNetworkValidated = true;
                LogOperation(context, "get-info", stopwatch, "success", null);
            }
            catch (NotSupportedException)
            {
                model.Notices.Add("Node information is not available for this backend.");
                LogOperation(context, "get-info", stopwatch, "unsupported", nameof(NotSupportedException));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                model.Warnings.Add("Could not load node information.");
                LogOperation(context, "get-info", stopwatch, "failed", exception.GetType().Name);
            }
        }

        if (context.Capabilities.CanListChannels)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var channels = await context.Client.ListChannels(cancellationToken);
                var channelStates = channels
                    .Select(channel =>
                    {
                        var hasPendingState = TryGetExplicitPendingState(channel, out var isPending);
                        return (Channel: channel, HasPendingState: hasPendingState, IsPending: isPending);
                    })
                    .ToArray();

                var hasExplicitStateForEveryChannel =
                    channelStates.All(channel => channel.HasPendingState);
                if (context.BackendType == LightningBackendTypes.CLightning &&
                    hasExplicitStateForEveryChannel)
                {
                    pendingChannelsCount = channelStates.LongCount(channel => channel.IsPending is true);
                    activeChannelsCount = channelStates.LongCount(channel =>
                        channel.IsPending is false && channel.Channel.IsActive);
                    inactiveChannelsCount = channelStates.LongCount(channel =>
                        channel.IsPending is false && !channel.Channel.IsActive);
                }
                else if (activeChannelsCount is null &&
                         inactiveChannelsCount is null &&
                         pendingChannelsCount is null)
                {
                    // No backend snapshot is available. Older adapters can still provide
                    // active rows. Only adapters with explicit per-row state can safely
                    // distinguish inactive rows from pending ones.
                    activeChannelsCount = channels.LongCount(channel => channel.IsActive);
                    if (hasExplicitStateForEveryChannel)
                    {
                        inactiveChannelsCount = channelStates.LongCount(channel =>
                            channel.IsPending is false && !channel.Channel.IsActive);
                    }
                }
                LogOperation(context, "list-channels-summary", stopwatch, "success", null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Keep GetInfo channel counts when ListChannels is unavailable for a backend.
                model.Warnings.Add("Could not load channels.");
                LogOperation(context, "list-channels-summary", stopwatch, "failed", exception.GetType().Name);
            }
        }

        if (context.Capabilities.CanGetBalance && phoenixdNetworkValidated)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var balance = await context.Client.GetBalance(cancellationToken);
                if (balance.OnchainBalance is not null)
                {
                    AddMoneyRow(model.OnchainBalanceRows, "Confirmed", balance.OnchainBalance.Confirmed);
                    AddMoneyRow(model.OnchainBalanceRows, "Unconfirmed", balance.OnchainBalance.Unconfirmed);
                    AddMoneyRow(model.OnchainBalanceRows, "Reserved", balance.OnchainBalance.Reserved);
                }

                if (balance.OffchainBalance is not null)
                {
                    AddLightMoneyRow(model.OffchainBalanceRows, "Opening", balance.OffchainBalance.Opening);
                    AddLightMoneyRow(model.OffchainBalanceRows, "Local", balance.OffchainBalance.Local);
                    AddLightMoneyRow(model.OffchainBalanceRows, "Remote", balance.OffchainBalance.Remote);
                    AddLightMoneyRow(model.OffchainBalanceRows, "Closing", balance.OffchainBalance.Closing);
                }
                LogOperation(context, "get-balance", stopwatch, "success", null);
            }
            catch (NotSupportedException)
            {
                model.Notices.Add("Balance information is not available for this backend.");
                LogOperation(context, "get-balance", stopwatch, "unsupported", nameof(NotSupportedException));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                model.Warnings.Add("Could not load balances.");
                LogOperation(context, "get-balance", stopwatch, "failed", exception.GetType().Name);
            }
        }

        AddSummaryRow(model.SummaryRows, "Node", nodeDisplayName);
        AddSummaryRow(model.SummaryRows, "Host", nodeHost);
        AddSummaryRow(model.SummaryRows, "Version", version);
        AddSummaryRow(model.SummaryRows, "Block height", blockHeight?.ToString());
        AddSummaryRow(model.SummaryRows, "Peers", peersCount?.ToString());
        AddSummaryRow(model.SummaryRows, "Active channels", activeChannelsCount?.ToString());
        AddSummaryRow(model.SummaryRows, "Inactive channels", inactiveChannelsCount?.ToString());
        AddSummaryRow(model.SummaryRows, "Pending channels", pendingChannelsCount?.ToString());
    }

    internal static bool IsAffectedPhoenixdAdapter(ILightningClient? client)
    {
        var assemblyName = client?.GetType().Assembly.GetName();
        return IsAffectedPhoenixdAdapter(assemblyName?.Name, assemblyName?.Version);
    }

    internal static bool IsAffectedPhoenixdAdapter(string? assemblyName, Version? version)
    {
        return string.Equals(
                   assemblyName,
                   PhoenixdAdapterAssemblyName,
                   StringComparison.Ordinal) &&
               version == AffectedPhoenixdAdapterVersion;
    }

    public bool TryCreateSendPreview(
        StoreLightningManagerContext context,
        string? bolt11,
        string? amountSats,
        string? maxFeeSats,
        out SendPreviewViewModel? preview,
        out string? error)
    {
        preview = null;
        error = null;

        if (!context.IsConfigured || context.Client is null || context.Network is null)
        {
            error = "Lightning is not available for this store.";
            return false;
        }

        if (!context.Capabilities.CanPayBolt11)
        {
            error = "BOLT11 payments are not supported by this backend.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bolt11))
        {
            error = "A BOLT11 invoice is required.";
            return false;
        }

        if (!BOLT11PaymentRequest.TryParse(bolt11.Trim(), out var paymentRequest, context.Network.NBitcoinNetwork) || paymentRequest is null)
        {
            error = "The BOLT11 invoice is invalid.";
            return false;
        }

        var isAmountless = paymentRequest.MinimumAmount is null || paymentRequest.MinimumAmount == LightMoney.Zero;
        if (isAmountless && !context.Capabilities.CanPayAmountless)
        {
            error = "Amountless invoices are not supported by this backend.";
            return false;
        }

        LightMoney paymentAmount;
        long? userAmountSats = null;
        if (isAmountless)
        {
            if (!TryParsePaymentAmount(amountSats, out paymentAmount, out var parsedAmountSats))
            {
                error = "Amount must be a positive whole number of sats for an amountless invoice.";
                return false;
            }

            userAmountSats = parsedAmountSats;
        }
        else
        {
            paymentAmount = paymentRequest.MinimumAmount!;
        }

        long? maxFee = null;
        if (context.Capabilities.CanSetMaxFee)
        {
            if (!TryParseMaxFeeSats(maxFeeSats, out var parsedMaxFee))
            {
                error = "Maximum fee must be a positive whole number of sats.";
                return false;
            }

            maxFee = parsedMaxFee;
        }

        if (paymentRequest.ExpiryDate <= DateTimeOffset.UtcNow)
        {
            error = "This invoice has already expired.";
            return false;
        }

        if (paymentRequest.PaymentHash is null)
        {
            error = "The BOLT11 invoice is missing a payment hash.";
            return false;
        }

        preview = new SendPreviewViewModel
        {
            Bolt11 = bolt11.Trim(),
            PaymentAmount = paymentAmount,
            UserAmountSats = userAmountSats,
            AmountDisplay = FormatLightMoney(paymentAmount),
            MaxFeeSats = maxFee,
            MaxFeeDisplay = maxFee is long fee ? FormatMoney(Money.Satoshis(fee)) : null,
            Description = paymentRequest.ShortDescription ?? "No description",
            PaymentHash = paymentRequest.PaymentHash.ToString(),
            Payee = paymentRequest.GetPayeePubKey().ToString(),
            ExpiresAt = paymentRequest.ExpiryDate
        };
        return true;
    }

    public async Task<SendExecutionResult> SendAsync(
        StoreLightningManagerContext context,
        string bolt11,
        string? amountSats,
        string? maxFeeSats,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!TryCreateSendPreview(context, bolt11, amountSats, maxFeeSats, out var preview, out var validationError))
        {
            return CompleteSend(context, stopwatch, "invalid", new SendExecutionResult
            {
                Result = Failure(validationError ?? "The invoice is invalid.")
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_operationGuard.TryBeginPayment(
                context.BackendIdentityFingerprint,
                context.CryptoCode,
                preview!.PaymentHash,
                out var operationLease))
        {
            return CompleteSend(context, stopwatch, "duplicate", new SendExecutionResult
            {
                Result = Failure("Payment is already in progress.")
            });
        }

        using var lease = operationLease;
        try
        {
            var payParams = new PayInvoiceParams();
            if (preview.IsAmountless)
            {
                payParams.Amount = preview.PaymentAmount;
            }

            if (preview.MaxFeeSats is long maxFee)
            {
                payParams.MaxFeeFlat = Money.Satoshis(maxFee);
            }

            var payResponse = await context.Client!.Pay(
                preview.Bolt11,
                payParams,
                cancellationToken);
            LightningPayment? knownPayment = null;
            if (payResponse.Result != PayResult.Ok || payResponse.Details is null)
            {
                using var paymentLookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                paymentLookupTimeout.CancelAfter(PaymentLookupTimeout);
                knownPayment = await TryLoadPaymentAsync(
                    context.Client,
                    preview.PaymentHash,
                    paymentLookupTimeout.Token);
                if (payResponse.Result != PayResult.Ok && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            var details = CreatePaymentDetails(
                preview.PaymentHash,
                knownPayment,
                payResponse,
                preview.PaymentAmount);
            if (payResponse.Result != PayResult.Ok)
            {
                var knownResult = ResolveKnownPaymentResult(
                    context,
                    preview.PaymentHash,
                    knownPayment,
                    payResponse);
                if (knownResult is not null)
                {
                    return CompleteSend(
                        context,
                        stopwatch,
                        knownResult.Result.IsSuccess ? "success" : GetPaymentOutcome(knownResult.Payment),
                        knownResult);
                }
            }

            var result = payResponse.Result switch
            {
                PayResult.Ok => new SendExecutionResult
                {
                    Result = Success("Payment sent successfully."),
                    Payment = details
                },
                PayResult.Unknown => new SendExecutionResult
                {
                    Result = Failure(UnknownPaymentStatusMessage),
                    Payment = details
                },
                PayResult.CouldNotFindRoute => new SendExecutionResult
                {
                    Result = Failure("No route to the invoice destination was found.")
                },
                PayResult.Error when details.Status == LightningPaymentStatus.Failed => new SendExecutionResult
                {
                    Result = Failure("Lightning payment failed."),
                    Payment = details
                },
                _ => new SendExecutionResult
                {
                    Result = Failure(UnknownPaymentStatusMessage),
                    Payment = details
                }
            };
            return CompleteSend(
                context,
                stopwatch,
                result.Result.IsSuccess ? "success" : GetPaymentOutcome(result.Payment),
                result);
        }
        catch (NotSupportedException)
        {
            return CompleteSend(context, stopwatch, "unsupported", new SendExecutionResult
            {
                Result = Failure("This backend does not support sending payments.")
            });
        }
        catch (Exception exception)
        {
            var result = await ReconcileAmbiguousPaymentAsync(context, preview.PaymentHash);
            return CompleteSend(
                context,
                stopwatch,
                result.Result.IsSuccess ? "success" : GetPaymentOutcome(result.Payment),
                result,
                exception.GetType().Name);
        }
    }

    public async Task<ActionResultViewModel> ConnectPeerAsync(
        StoreLightningManagerContext context,
        string? nodeUri,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            return Failure("Lightning is not available for this store.");
        }

        if (!context.Capabilities.CanConnectPeer)
        {
            return Failure("Peer connections are not supported by this backend.");
        }

        if (!NodeInfo.TryParse(nodeUri?.Trim() ?? string.Empty, out var nodeInfo) || nodeInfo is null)
        {
            return Failure("The node URI is invalid. Use pubkey@host[:port].");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await context.Client.ConnectTo(nodeInfo, cancellationToken);
            var actionResult = result == ConnectionResult.Ok
                ? Success("Connected to peer successfully.")
                : Failure("Could not connect to the remote peer.");
            return CompleteAction(
                context,
                "connect-peer",
                stopwatch,
                actionResult.IsSuccess ? "success" : "failed",
                actionResult);
        }
        catch (NotSupportedException)
        {
            return CompleteAction(
                context,
                "connect-peer",
                stopwatch,
                "unsupported",
                Failure("Peer connections are not supported by this backend."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CompleteAction(
                context,
                "connect-peer",
                stopwatch,
                "unknown",
                Failure("Peer connection status is unknown. Check the Lightning node before retrying."),
                nameof(OperationCanceledException));
        }
        catch (Exception exception)
        {
            return CompleteAction(
                context,
                "connect-peer",
                stopwatch,
                "failed",
                Failure("Peer connection failed."),
                exception.GetType().Name);
        }
    }

    public async Task PopulateChannelsAsync(
        ChannelsViewModel model,
        StoreLightningManagerContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            return;
        }

        if (!context.Capabilities.CanListChannels)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var channels = await context.Client.ListChannels(cancellationToken);
            foreach (var channel in channels
                         .OrderByDescending(c => c.IsActive)
                         .ThenByDescending(c => c.Capacity))
            {
                var capacity = new LightMoney(Math.Max(0, channel.Capacity.MilliSatoshi));
                var localBalance = new LightMoney(Math.Clamp(channel.LocalBalance.MilliSatoshi, 0, capacity.MilliSatoshi));
                var remoteBalance = capacity - localBalance;
                TryGetExplicitPendingState(channel, out var isPending);
                model.Channels.Add(new LightningChannelItemViewModel
                {
                    RemoteNode = channel.RemoteNode?.ToString() ?? "Unknown",
                    ChannelPoint = channel.ChannelPoint?.ToString(),
                    CapacitySats = capacity.ToUnit(LightMoneyUnit.Satoshi),
                    LocalBalanceSats = localBalance.ToUnit(LightMoneyUnit.Satoshi),
                    CapacityDisplay = FormatLightMoney(capacity),
                    LocalBalanceDisplay = FormatLightMoney(localBalance),
                    RemoteBalanceDisplay = FormatLightMoney(remoteBalance),
                    IsPending = isPending,
                    IsActive = channel.IsActive,
                    IsPublic = channel.IsPublic
                });
            }

            LogOperation(context, "list-channels", stopwatch, "success", null);
        }
        catch (NotSupportedException)
        {
            model.ChannelListMessage = "Channel listing is not supported by this backend.";
            LogOperation(context, "list-channels", stopwatch, "unsupported", nameof(NotSupportedException));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            model.ChannelListMessage = "Could not load channels.";
            LogOperation(context, "list-channels", stopwatch, "failed", exception.GetType().Name);
        }
    }

    internal static bool TryGetExplicitPendingState(LightningChannel channel, out bool? isPending)
    {
        // IsPending is being added upstream. Reflection keeps the plugin compatible with
        // BTCPay 2.4.1 while allowing the new adapter state to flow through once released.
        var property = channel.GetType().GetProperty("IsPending");
        if (property is null ||
            !property.CanRead ||
            (property.PropertyType != typeof(bool) && property.PropertyType != typeof(bool?)))
        {
            isPending = null;
            return false;
        }

        if (property.GetValue(channel) is bool value)
        {
            isPending = value;
            return true;
        }

        isPending = null;
        return false;
    }

    public bool TryCreateOpenChannelPreview(
        StoreLightningManagerContext context,
        string? nodeUri,
        string? channelAmountSats,
        string? feeRateSatsPerByte,
        out OpenChannelPreviewViewModel? preview,
        out string? error)
    {
        preview = null;
        error = null;

        if (!TryBuildOpenChannelRequest(context, nodeUri, channelAmountSats, feeRateSatsPerByte, out var request, out error))
        {
            return false;
        }

        preview = new OpenChannelPreviewViewModel
        {
            NodeUri = request!.NodeInfo.ToString(),
            ChannelAmountDisplay = FormatMoney(request.ChannelAmount),
            FeeRateDisplay = $"{request.FeeRate.SatoshiPerByte.ToString("0.########", CultureInfo.InvariantCulture)} sat/vB"
        };
        return true;
    }

    public async Task<ActionResultViewModel> OpenChannelAsync(
        StoreLightningManagerContext context,
        string nodeUri,
        string channelAmountSats,
        string? feeRateSatsPerByte,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildOpenChannelRequest(context, nodeUri, channelAmountSats, feeRateSatsPerByte, out var request, out var error))
        {
            return Failure(error ?? "Invalid channel request.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!_operationGuard.TryBeginChannel(
                context.BackendIdentityFingerprint,
                context.CryptoCode,
                request!.NodeInfo.NodeId.ToString(),
                out var operationLease))
        {
            return CompleteAction(
                context,
                "open-channel",
                stopwatch,
                "duplicate",
                Failure("Channel opening is already in progress for this peer."));
        }

        using var lease = operationLease;
        try
        {
            var response = await context.Client!.OpenChannel(request, cancellationToken);
            var actionResult = response.Result switch
            {
                OpenChannelResult.Ok => Success("Channel opening request submitted."),
                OpenChannelResult.AlreadyExists => Failure("A channel with that peer already exists."),
                OpenChannelResult.CannotAffordFunding => Failure("Insufficient balance to fund the channel."),
                OpenChannelResult.NeedMoreConf => Failure(
                    "Channel opening may already be pending. Check the Lightning node before retrying."),
                OpenChannelResult.PeerNotConnected => Failure("The peer is not connected."),
                _ => Failure("Channel opening status is unknown. Check the Lightning node before retrying.")
            };
            var outcome = response.Result == OpenChannelResult.Ok
                ? "success"
                : response.Result is OpenChannelResult.NeedMoreConf
                    ? "unknown"
                    : "failed";
            return CompleteAction(context, "open-channel", stopwatch, outcome, actionResult);
        }
        catch (NotSupportedException)
        {
            return CompleteAction(
                context,
                "open-channel",
                stopwatch,
                "unsupported",
                Failure("Channel opening is not supported by this backend."));
        }
        catch (Exception exception)
        {
            return CompleteAction(
                context,
                "open-channel",
                stopwatch,
                "unknown",
                Failure("Channel opening status is unknown. Check the Lightning node before retrying."),
                exception.GetType().Name);
        }
    }

    private static bool TryBuildOpenChannelRequest(
        StoreLightningManagerContext context,
        string? nodeUri,
        string? channelAmountSats,
        string? feeRateSatsPerByte,
        out OpenChannelRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (!context.IsConfigured || context.Client is null)
        {
            error = "Lightning is not available for this store.";
            return false;
        }

        if (!context.Capabilities.CanOpenChannel)
        {
            error = "Channel opening is not supported by this backend.";
            return false;
        }

        if (!NodeInfo.TryParse(nodeUri?.Trim() ?? string.Empty, out var nodeInfo) || nodeInfo is null)
        {
            error = "The node URI is invalid. Use pubkey@host[:port].";
            return false;
        }

        if (!TryParseChannelAmount(channelAmountSats, out var channelAmount))
        {
            error = "Channel amount must be a positive whole number of sats.";
            return false;
        }

        if (IsLndBackend(context) && channelAmount.Satoshi < MinimumLndChannelAmountSats)
        {
            error = $"Channel amount must be at least {MinimumLndChannelAmountSats} sats for LND backends.";
            return false;
        }

        if (!TryParseFeeRate(feeRateSatsPerByte, out var feeRate))
        {
            error = $"Fee rate must be a whole number between 1 and {int.MaxValue} sat/vB.";
            return false;
        }

        request = new OpenChannelRequest
        {
            NodeInfo = nodeInfo,
            ChannelAmount = channelAmount,
            FeeRate = feeRate
        };
        return true;
    }

    private static bool TryParseMaxFeeSats(string? maxFeeSats, out long sats)
    {
        if (string.IsNullOrWhiteSpace(maxFeeSats))
        {
            sats = LightningManagerDefaults.SendMaxFeeSats;
            return true;
        }

        if (!long.TryParse(
                maxFeeSats.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sats) ||
            sats <= 0 ||
            sats > long.MaxValue / 1_000)
        {
            return false;
        }

        return true;
    }

    private static bool TryParsePaymentAmount(
        string? amountSats,
        out LightMoney amount,
        out long sats)
    {
        amount = LightMoney.Zero;
        if (!long.TryParse(
                amountSats?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sats) ||
            sats <= 0)
        {
            return false;
        }

        if (sats > long.MaxValue / 1_000)
        {
            sats = 0;
            return false;
        }

        amount = LightMoney.Satoshis(sats);
        return true;
    }

    private static bool TryParseChannelAmount(string? channelAmountSats, out Money channelAmount)
    {
        channelAmount = Money.Zero;
        if (!long.TryParse(
                channelAmountSats?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sats) ||
            sats <= 0)
        {
            return false;
        }

        channelAmount = Money.Satoshis(sats);
        return true;
    }

    private static bool TryParseFeeRate(string? feeRateSatsPerByte, out FeeRate feeRate)
    {
        if (string.IsNullOrWhiteSpace(feeRateSatsPerByte))
        {
            feeRate = new FeeRate(DefaultChannelOpenFeeRate);
            return true;
        }

        if (!int.TryParse(
                feeRateSatsPerByte.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var satPerByte) ||
            satPerByte <= 0)
        {
            feeRate = new FeeRate(DefaultChannelOpenFeeRate);
            return false;
        }

        feeRate = new FeeRate((decimal)satPerByte);
        return true;
    }

    private static async Task<LightningPayment?> TryLoadPaymentAsync(
        ILightningClient client,
        string paymentHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetPayment(paymentHash, cancellationToken).WaitAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<SendExecutionResult> ReconcileAmbiguousPaymentAsync(
        StoreLightningManagerContext context,
        string paymentHash)
    {
        using var reconciliationTimeout = new CancellationTokenSource(PaymentLookupTimeout);
        var payment = await TryLoadPaymentAsync(
            context.Client!,
            paymentHash,
            reconciliationTimeout.Token);
        return ResolveKnownPaymentResult(context, paymentHash, payment) ??
               new SendExecutionResult
               {
                   Result = Failure(UnknownPaymentStatusMessage),
                   Payment = CreatePaymentDetails(paymentHash, payment)
               };
    }

    private static SendResultDetailsViewModel CreatePaymentDetails(
        string paymentHash,
        LightningPayment? payment,
        PayResponse? response = null,
        LightMoney? canonicalPaymentAmount = null,
        LightningPaymentStatus? statusOverride = null)
    {
        var responseDetails = response?.Details;
        var totalAmount = payment?.AmountSent ??
                          (response?.Result == PayResult.Ok &&
                           canonicalPaymentAmount is not null &&
                           responseDetails?.FeeAmount is { } responseFee
                              ? canonicalPaymentAmount + responseFee
                              : responseDetails?.TotalAmount);
        var feeAmount = payment?.Fee ?? responseDetails?.FeeAmount;
        return new SendResultDetailsViewModel
        {
            Status = statusOverride ??
                     (response?.Result == PayResult.Ok
                         ? LightningPaymentStatus.Complete
                         : payment?.Status ??
                           response?.Result switch
                           {
                               PayResult.Error
                                   when responseDetails?.Status == LightningPaymentStatus.Failed =>
                                   LightningPaymentStatus.Failed,
                               PayResult.Unknown
                                   when responseDetails?.Status == LightningPaymentStatus.Pending =>
                                   LightningPaymentStatus.Pending,
                               _ => LightningPaymentStatus.Unknown
                           }),
            TotalAmountDisplay = totalAmount is null ? null : FormatLightMoney(totalAmount),
            FeeAmountDisplay = feeAmount is null ? null : FormatLightMoney(feeAmount),
            PaymentHash = payment?.PaymentHash ?? responseDetails?.PaymentHash?.ToString() ?? paymentHash,
            Preimage = payment?.Preimage ?? responseDetails?.Preimage?.ToString()
        };
    }

    private static SendExecutionResult? ResolveKnownPaymentResult(
        StoreLightningManagerContext context,
        string paymentHash,
        LightningPayment? payment,
        PayResponse? response = null)
    {
        var isBlink = context.BackendType == LightningBackendTypes.Blink;
        if (payment?.Status == LightningPaymentStatus.Failed &&
            (context.BackendType == LightningBackendTypes.Eclair ||
             (isBlink &&
              (response?.Result != PayResult.Error ||
               response.Details?.Status != LightningPaymentStatus.Failed))))
        {
            var reconciledStatus =
                isBlink &&
                response?.Result == PayResult.Unknown &&
                response.Details?.Status == LightningPaymentStatus.Pending
                    ? LightningPaymentStatus.Pending
                    : LightningPaymentStatus.Unknown;
            return new SendExecutionResult
            {
                Result = Failure(UnknownPaymentStatusMessage),
                Payment = CreatePaymentDetails(
                    paymentHash,
                    payment,
                    statusOverride: reconciledStatus)
            };
        }

        return payment?.Status switch
        {
            LightningPaymentStatus.Complete => new SendExecutionResult
            {
                Result = Success("Payment sent successfully."),
                Payment = CreatePaymentDetails(paymentHash, payment)
            },
            LightningPaymentStatus.Pending or LightningPaymentStatus.Unknown => new SendExecutionResult
            {
                Result = Failure(UnknownPaymentStatusMessage),
                Payment = CreatePaymentDetails(paymentHash, payment)
            },
            LightningPaymentStatus.Failed => new SendExecutionResult
            {
                Result = Failure("Lightning payment failed."),
                Payment = CreatePaymentDetails(paymentHash, payment)
            },
            _ => null
        };
    }

    private static ActionResultViewModel Success(string message)
    {
        return new ActionResultViewModel { IsSuccess = true, Message = message };
    }

    private static ActionResultViewModel Failure(string message)
    {
        return new ActionResultViewModel { IsSuccess = false, Message = message };
    }

    private SendExecutionResult CompleteSend(
        StoreLightningManagerContext context,
        Stopwatch stopwatch,
        string outcome,
        SendExecutionResult result,
        string? exceptionType = null)
    {
        LogOperation(context, "pay", stopwatch, outcome, exceptionType);
        return result;
    }

    private ActionResultViewModel CompleteAction(
        StoreLightningManagerContext context,
        string operation,
        Stopwatch stopwatch,
        string outcome,
        ActionResultViewModel result,
        string? exceptionType = null)
    {
        LogOperation(context, operation, stopwatch, outcome, exceptionType);
        return result;
    }

    private void LogOperation(
        StoreLightningManagerContext context,
        string operation,
        Stopwatch stopwatch,
        string outcome,
        string? exceptionType)
    {
        stopwatch.Stop();
        var backend = context.BackendType ?? "unknown";
        if (outcome == "success")
        {
            _logger.LogInformation(
                "Lightning manager operation {Operation} for store {StoreId} and crypto {CryptoCode} using backend {Backend} completed in {DurationMs} ms with result {Result}",
                operation,
                context.StoreId,
                context.CryptoCode,
                backend,
                stopwatch.ElapsedMilliseconds,
                outcome);
            return;
        }

        _logger.LogWarning(
            "Lightning manager operation {Operation} for store {StoreId} and crypto {CryptoCode} using backend {Backend} completed in {DurationMs} ms with result {Result} and exception type {ExceptionType}",
            operation,
            context.StoreId,
            context.CryptoCode,
            backend,
            stopwatch.ElapsedMilliseconds,
            outcome,
            exceptionType ?? "none");
    }

    private static string GetPaymentOutcome(SendResultDetailsViewModel? payment)
    {
        return payment?.Status is LightningPaymentStatus.Pending or LightningPaymentStatus.Unknown
            ? "unknown"
            : "failed";
    }

    private static bool IsLndBackend(StoreLightningManagerContext context)
    {
        return context.BackendType is LightningBackendTypes.LndRest or LightningBackendTypes.LndGrpc;
    }

    private static void AddSummaryRow(List<ValueRowViewModel> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new ValueRowViewModel { Label = label, Value = value });
        }
    }

    private static void AddMoneyRow(List<ValueRowViewModel> rows, string label, Money? value)
    {
        if (value is not null)
        {
            rows.Add(new ValueRowViewModel { Label = label, Value = FormatMoney(value) });
        }
    }

    private static void AddLightMoneyRow(List<ValueRowViewModel> rows, string label, LightMoney? value)
    {
        if (value is not null)
        {
            rows.Add(new ValueRowViewModel { Label = label, Value = FormatLightMoney(value) });
        }
    }

    private static string FormatMoney(Money amount)
    {
        return $"{amount.Satoshi.ToString("#,0", CultureInfo.InvariantCulture)} sats ({amount.ToDecimal(MoneyUnit.BTC).ToString("#,0.########", CultureInfo.InvariantCulture)} BTC)";
    }

    private static string FormatLightMoney(LightMoney amount)
    {
        return $"{amount.ToUnit(LightMoneyUnit.Satoshi).ToString("#,0.########", CultureInfo.InvariantCulture)} sats";
    }
}
