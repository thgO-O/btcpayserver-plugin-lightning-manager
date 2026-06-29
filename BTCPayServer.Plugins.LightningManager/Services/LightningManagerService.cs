#nullable enable
using System.Globalization;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using NBitcoin;

namespace BTCPayServer.Plugins.LightningManager.Services;

public class SendExecutionResult
{
    public required ActionResultViewModel Result { get; init; }
    public SendResultDetailsViewModel? Payment { get; init; }
}

public interface ILightningManagerService
{
    LightningManagerTabsViewModel CreateTabs(StoreLightningManagerContext context, string activePage);
    Task PopulateOverviewAsync(OverviewViewModel model, StoreLightningManagerContext context, CancellationToken cancellationToken = default);
    bool TryCreateSendPreview(StoreLightningManagerContext context, string? bolt11, string? maxFeeSats, out SendPreviewViewModel? preview, out string? error);
    Task<SendExecutionResult> SendAsync(StoreLightningManagerContext context, string bolt11, string? maxFeeSats, CancellationToken cancellationToken = default);
    Task<ActionResultViewModel> ConnectPeerAsync(StoreLightningManagerContext context, string? nodeUri, CancellationToken cancellationToken = default);
    Task PopulatePeersAsync(PeersViewModel model, StoreLightningManagerContext context, CancellationToken cancellationToken = default);
    Task PopulateChannelsAsync(ChannelsViewModel model, StoreLightningManagerContext context, CancellationToken cancellationToken = default);
    bool TryCreateOpenChannelPreview(
        StoreLightningManagerContext context,
        string? nodeUri,
        string? channelAmountSats,
        string? feeRateSatsPerByte,
        out OpenChannelPreviewViewModel? preview,
        out string? error);
    Task<ActionResultViewModel> OpenChannelAsync(
        StoreLightningManagerContext context,
        string nodeUri,
        string channelAmountSats,
        string? feeRateSatsPerByte,
        CancellationToken cancellationToken = default);
}

public class LightningManagerService : ILightningManagerService
{
    private const decimal DefaultChannelOpenFeeRate = 1.0m;
    private const long MinimumLndChannelAmountSats = 20_000;

    public virtual LightningManagerTabsViewModel CreateTabs(StoreLightningManagerContext context, string activePage)
    {
        return new LightningManagerTabsViewModel
        {
            StoreId = context.StoreId,
            CryptoCode = context.CryptoCode,
            ActivePage = activePage,
            ShowOverview = true,
            ShowSend = context.Capabilities.CanPayBolt11,
            ShowPeers = context.Capabilities.CanConnectPeer,
            ShowChannels = context.Capabilities.CanListChannels || context.Capabilities.CanOpenChannel
        };
    }

    public virtual async Task PopulateOverviewAsync(
        OverviewViewModel model,
        StoreLightningManagerContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            return;
        }

        if (context.Capabilities.CanGetInfo)
        {
            try
            {
                var info = await context.Client.GetInfo(cancellationToken);
                model.Alias = info.Alias;
                model.NodeDisplayName = string.IsNullOrWhiteSpace(model.NodeDisplayName) ? info.Alias : model.NodeDisplayName;
                model.Version = info.Version;
                model.BlockHeight = info.BlockHeight;
                model.PeersCount = info.PeersCount;
                model.ActiveChannelsCount = info.ActiveChannelsCount;
                model.InactiveChannelsCount = info.InactiveChannelsCount;
                model.PendingChannelsCount = info.PendingChannelsCount;
                foreach (var nodeInfo in info.NodeInfoList)
                {
                    model.NodeUris.Add(nodeInfo.ToString());
                }
            }
            catch (NotSupportedException)
            {
                model.Notices.Add("Node information is not available for this backend.");
            }
            catch (Exception)
            {
                model.Notices.Add("Could not load node information.");
            }
        }

        if (context.Capabilities.CanListChannels)
        {
            try
            {
                var channels = await context.Client.ListChannels(cancellationToken);
                model.ActiveChannelsCount = channels.LongCount(channel => channel.IsActive);
                model.InactiveChannelsCount = channels.LongCount(channel => !channel.IsActive);
            }
            catch
            {
                // Keep GetInfo channel counts when ListChannels is unavailable for a backend.
            }
        }

        if (context.Capabilities.CanGetBalance)
        {
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
            }
            catch (NotSupportedException)
            {
                model.Notices.Add("Balance information is not available for this backend.");
            }
            catch (Exception)
            {
                model.Notices.Add("Could not load balances.");
            }
        }

        AddSummaryRow(model.SummaryRows, "Node", model.NodeDisplayName);
        AddSummaryRow(model.SummaryRows, "Host", model.NodeHost);
        AddSummaryRow(model.SummaryRows, "Version", model.Version);
        AddSummaryRow(model.SummaryRows, "Block height", model.BlockHeight?.ToString());
        AddSummaryRow(model.SummaryRows, "Peers", model.PeersCount?.ToString());
        AddSummaryRow(model.SummaryRows, "Active channels", model.ActiveChannelsCount?.ToString());
        AddSummaryRow(model.SummaryRows, "Inactive channels", model.InactiveChannelsCount?.ToString());
        AddSummaryRow(model.SummaryRows, "Pending channels", model.PendingChannelsCount?.ToString());
    }

    public virtual bool TryCreateSendPreview(
        StoreLightningManagerContext context,
        string? bolt11,
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

        if (!TryParseMaxFeeSats(maxFeeSats, out var maxFee))
        {
            error = "Maximum fee must be a non-negative whole number of sats.";
            return false;
        }

        if (!BOLT11PaymentRequest.TryParse(bolt11.Trim(), out var paymentRequest, context.Network.NBitcoinNetwork) || paymentRequest is null)
        {
            error = "The BOLT11 invoice is invalid.";
            return false;
        }

        if (paymentRequest.MinimumAmount is null || paymentRequest.MinimumAmount == LightMoney.Zero)
        {
            error = "Amountless invoices are not supported by this interface.";
            return false;
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
            AmountDisplay = FormatLightMoney(paymentRequest.MinimumAmount),
            MaxFeeSats = maxFee,
            MaxFeeDisplay = FormatMoney(Money.Satoshis(maxFee)),
            Description = paymentRequest.ShortDescription ?? "No description",
            PaymentHash = paymentRequest.PaymentHash.ToString(),
            Payee = paymentRequest.GetPayeePubKey().ToString(),
            ExpiresAt = paymentRequest.ExpiryDate
        };
        return true;
    }

    public virtual async Task<SendExecutionResult> SendAsync(
        StoreLightningManagerContext context,
        string bolt11,
        string? maxFeeSats,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateSendPreview(context, bolt11, maxFeeSats, out var preview, out var validationError))
        {
            return new SendExecutionResult
            {
                Result = new ActionResultViewModel
                {
                    IsSuccess = false,
                    Message = validationError ?? "The invoice is invalid."
                }
            };
        }

        try
        {
            var payResponse = await context.Client!.Pay(
                bolt11,
                new PayInvoiceParams
                {
                    MaxFeeFlat = Money.Satoshis(preview!.MaxFeeSats)
                },
                cancellationToken);
            var details = await TryLoadPaymentDetailsAsync(context.Client, bolt11, context.Network!.NBitcoinNetwork, payResponse, cancellationToken);
            if (payResponse.Result != PayResult.Ok)
            {
                var knownPayment = await TryLoadPaymentAsync(context.Client, preview!.PaymentHash, CancellationToken.None);
                var knownResult = ResolveKnownPaymentResult(preview.PaymentHash, knownPayment);
                if (knownResult is not null)
                {
                    return knownResult;
                }
            }

            return payResponse.Result switch
            {
                PayResult.Ok => new SendExecutionResult
                {
                    Result = new ActionResultViewModel
                    {
                        IsSuccess = true,
                        Message = "Payment sent successfully."
                    },
                    Payment = details
                },
                PayResult.Unknown => new SendExecutionResult
                {
                    Result = new ActionResultViewModel
                    {
                        IsSuccess = false,
                        Message = "Payment status is unknown. Check the Lightning node before retrying."
                    },
                    Payment = details
                },
                PayResult.CouldNotFindRoute => new SendExecutionResult
                {
                    Result = new ActionResultViewModel
                    {
                        IsSuccess = false,
                        Message = "No route to the invoice destination was found."
                    }
                },
                PayResult.Error => new SendExecutionResult
                {
                    Result = new ActionResultViewModel
                    {
                        IsSuccess = false,
                        Message = LightningPaymentErrorMessages.NormalizePayError(payResponse.ErrorDetail)
                    }
                },
                _ => new SendExecutionResult
                {
                    Result = new ActionResultViewModel
                    {
                        IsSuccess = false,
                        Message = "The payment failed."
                    }
                }
            };
        }
        catch (NotSupportedException)
        {
            return new SendExecutionResult
            {
                Result = new ActionResultViewModel
                {
                    IsSuccess = false,
                    Message = "This backend does not support sending payments."
                }
            };
        }
        catch (Exception)
        {
            var payment = await TryLoadPaymentAsync(context.Client!, preview!.PaymentHash, CancellationToken.None);
            return ResolveKnownPaymentResult(preview.PaymentHash, payment) ??
                   new SendExecutionResult
                   {
                       Result = new ActionResultViewModel
                       {
                           IsSuccess = false,
                           Message = "Payment status is unknown. Check the Lightning node before retrying."
                       },
                       Payment = CreatePaymentDetails(preview.PaymentHash, payment)
                   };
        }
    }

    public virtual async Task<ActionResultViewModel> ConnectPeerAsync(
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

        try
        {
            var result = await context.Client.ConnectTo(nodeInfo, cancellationToken);
            return result switch
            {
                ConnectionResult.Ok => Success("Connected to peer successfully."),
                ConnectionResult.CouldNotConnect => Failure("Could not connect to the remote peer."),
                _ => Failure("Could not connect to the remote peer.")
            };
        }
        catch (NotSupportedException)
        {
            return Failure("Peer connections are not supported by this backend.");
        }
        catch (Exception)
        {
            return Failure("Peer connection failed.");
        }
    }

    public virtual Task PopulatePeersAsync(
        PeersViewModel model,
        StoreLightningManagerContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            model.PeerListMessage = "Lightning is not available for this store.";
            return Task.CompletedTask;
        }

        model.PeerListMessage = "Peer listing is not available for this backend.";
        return Task.CompletedTask;
    }

    public virtual async Task PopulateChannelsAsync(
        ChannelsViewModel model,
        StoreLightningManagerContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            model.ChannelListMessage = "Lightning is not available for this store.";
            return;
        }

        if (!context.Capabilities.CanListChannels)
        {
            model.ChannelListMessage = "Channel listing is not supported by this backend.";
            return;
        }

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
                model.Channels.Add(new LightningChannelItemViewModel
                {
                    RemoteNode = channel.RemoteNode.ToString(),
                    ChannelPoint = channel.ChannelPoint.ToString(),
                    CapacitySats = capacity.ToUnit(LightMoneyUnit.Satoshi),
                    LocalBalanceSats = localBalance.ToUnit(LightMoneyUnit.Satoshi),
                    RemoteBalanceSats = remoteBalance.ToUnit(LightMoneyUnit.Satoshi),
                    CapacityDisplay = FormatLightMoney(capacity),
                    LocalBalanceDisplay = FormatLightMoney(localBalance),
                    RemoteBalanceDisplay = FormatLightMoney(remoteBalance),
                    IsActive = channel.IsActive,
                    IsPublic = channel.IsPublic
                });
            }

            if (model.Channels.Count == 0)
            {
                model.ChannelListMessage = "No channels found.";
            }
        }
        catch (NotSupportedException)
        {
            model.ChannelListMessage = "Channel listing is not supported by this backend.";
        }
        catch (Exception)
        {
            model.ChannelListMessage = "Could not load channels.";
        }
    }

    public virtual bool TryCreateOpenChannelPreview(
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

    public virtual async Task<ActionResultViewModel> OpenChannelAsync(
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

        try
        {
            var response = await context.Client!.OpenChannel(request!, cancellationToken);
            return response.Result switch
            {
                OpenChannelResult.Ok => Success("Channel opening request submitted."),
                OpenChannelResult.AlreadyExists => Failure("A channel with that peer already exists."),
                OpenChannelResult.CannotAffordFunding => Failure("Insufficient balance to fund the channel."),
                OpenChannelResult.NeedMoreConf => Failure("More on-chain confirmations are required before opening a channel."),
                OpenChannelResult.PeerNotConnected => Failure("The peer is not connected."),
                _ => Failure("Could not open the channel.")
            };
        }
        catch (NotSupportedException)
        {
            return Failure("Channel opening is not supported by this backend.");
        }
        catch (Exception ex)
        {
            if (IsBenignEclairOpenChannelFollowUpError(context, ex))
            {
                return Success("Channel opening request submitted.");
            }

            return Failure("Channel opening failed.");
        }
    }

    protected virtual bool TryBuildOpenChannelRequest(
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

        if (IsLndClient(context.Client) && channelAmount.Satoshi < MinimumLndChannelAmountSats)
        {
            error = $"Channel amount must be at least {MinimumLndChannelAmountSats} sats for LND backends.";
            return false;
        }

        if (!TryParseFeeRate(feeRateSatsPerByte, out var feeRate))
        {
            error = "Fee rate must be a positive number of sat/vB.";
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
            sats < 0)
        {
            return false;
        }

        try
        {
            Money.Satoshis(sats);
            return true;
        }
        catch
        {
            sats = 0;
            return false;
        }
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

        try
        {
            channelAmount = Money.Satoshis(sats);
            return true;
        }
        catch
        {
            channelAmount = Money.Zero;
            return false;
        }
    }

    private static bool TryParseFeeRate(string? feeRateSatsPerByte, out FeeRate feeRate)
    {
        if (string.IsNullOrWhiteSpace(feeRateSatsPerByte))
        {
            feeRate = new FeeRate(DefaultChannelOpenFeeRate);
            return true;
        }

        if (decimal.TryParse(
                feeRateSatsPerByte.Trim(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var satPerByte) &&
            satPerByte > 0)
        {
            try
            {
                feeRate = new FeeRate(satPerByte);
                return true;
            }
            catch
            {
            }
        }

        feeRate = new FeeRate(DefaultChannelOpenFeeRate);
        return false;
    }

    private static async Task<SendResultDetailsViewModel?> TryLoadPaymentDetailsAsync(
        ILightningClient client,
        string bolt11,
        Network network,
        PayResponse response,
        CancellationToken cancellationToken)
    {
        var totalAmount = response.Details?.TotalAmount;
        var feeAmount = response.Details?.FeeAmount;
        var paymentHash = response.Details?.PaymentHash?.ToString();
        var preimage = response.Details?.Preimage?.ToString();
        var status = response.Result == PayResult.Ok ? LightningPaymentStatus.Complete : LightningPaymentStatus.Unknown;

        if (BOLT11PaymentRequest.TryParse(bolt11, out var paymentRequest, network) &&
            paymentRequest?.PaymentHash is not null)
        {
            try
            {
                var payment = await client.GetPayment(paymentRequest.PaymentHash.ToString()!, cancellationToken);
                if (payment is not null)
                {
                    totalAmount = payment.AmountSent ?? totalAmount;
                    feeAmount = payment.Fee ?? feeAmount;
                    paymentHash = payment.PaymentHash ?? paymentHash;
                    preimage = payment.Preimage ?? preimage;
                    status = payment.Status;
                }
            }
            catch
            {
            }
        }

        return new SendResultDetailsViewModel
        {
            Status = status,
            TotalAmountDisplay = totalAmount is null ? null : FormatLightMoney(totalAmount),
            FeeAmountDisplay = feeAmount is null ? null : FormatLightMoney(feeAmount),
            PaymentHash = paymentHash,
            Preimage = preimage
        };
    }

    private static async Task<LightningPayment?> TryLoadPaymentAsync(
        ILightningClient client,
        string paymentHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetPayment(paymentHash, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static SendResultDetailsViewModel CreatePaymentDetails(string paymentHash, LightningPayment? payment)
    {
        return new SendResultDetailsViewModel
        {
            Status = payment?.Status ?? LightningPaymentStatus.Unknown,
            TotalAmountDisplay = payment?.AmountSent is null ? null : FormatLightMoney(payment.AmountSent),
            FeeAmountDisplay = payment?.Fee is null ? null : FormatLightMoney(payment.Fee),
            PaymentHash = payment?.PaymentHash ?? paymentHash,
            Preimage = payment?.Preimage
        };
    }

    private static SendExecutionResult? ResolveKnownPaymentResult(string paymentHash, LightningPayment? payment)
    {
        return payment?.Status switch
        {
            LightningPaymentStatus.Complete => new SendExecutionResult
            {
                Result = new ActionResultViewModel
                {
                    IsSuccess = true,
                    Message = "Payment sent successfully."
                },
                Payment = CreatePaymentDetails(paymentHash, payment)
            },
            LightningPaymentStatus.Pending or LightningPaymentStatus.Unknown => new SendExecutionResult
            {
                Result = new ActionResultViewModel
                {
                    IsSuccess = false,
                    Message = "Payment status is unknown. Check the Lightning node before retrying."
                },
                Payment = CreatePaymentDetails(paymentHash, payment)
            },
            LightningPaymentStatus.Failed => new SendExecutionResult
            {
                Result = new ActionResultViewModel
                {
                    IsSuccess = false,
                    Message = "Lightning payment failed."
                },
                Payment = CreatePaymentDetails(paymentHash, payment)
            },
            _ => null
        };
    }

    private static bool IsBenignEclairOpenChannelFollowUpError(StoreLightningManagerContext context, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(exception.Message))
        {
            return false;
        }

        var isEclairBackend =
            (context.ConnectionString?.Contains("type=eclair", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (context.Client?.GetType().Name.Contains("Eclair", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (context.Client?.GetType().Namespace?.Contains(".Eclair", StringComparison.OrdinalIgnoreCase) ?? false);

        return isEclairBackend &&
               exception.Message.Contains("form field 'channelId' was malformed", StringComparison.OrdinalIgnoreCase) &&
               exception.Message.Contains("invalid hexadecimal", StringComparison.OrdinalIgnoreCase);
    }

    private static ActionResultViewModel Success(string message)
    {
        return new ActionResultViewModel { IsSuccess = true, Message = message };
    }

    private static ActionResultViewModel Failure(string message, string? detail = null)
    {
        return new ActionResultViewModel { IsSuccess = false, Message = message, Detail = detail };
    }

    private static bool IsLndClient(ILightningClient client)
    {
        var typeName = client.GetType().Name;
        var typeNamespace = client.GetType().Namespace;
        return typeName.Contains("Lnd", StringComparison.OrdinalIgnoreCase) ||
               (typeNamespace?.Contains(".LND", StringComparison.OrdinalIgnoreCase) ?? false);
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
