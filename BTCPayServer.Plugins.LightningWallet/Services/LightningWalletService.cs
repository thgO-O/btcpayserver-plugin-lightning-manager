#nullable enable
using System.Collections;
using System.Reflection;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningWallet.ViewModels;
using NBitcoin;

namespace BTCPayServer.Plugins.LightningWallet.Services;

public class SendExecutionResult
{
    public required ActionResultViewModel Result { get; init; }
    public SendResultDetailsViewModel? Payment { get; init; }
}

public interface ILightningWalletService
{
    LightningWalletTabsViewModel CreateTabs(StoreLightningWalletContext context, string activePage);
    Task PopulateOverviewAsync(OverviewViewModel model, StoreLightningWalletContext context, CancellationToken cancellationToken = default);
    bool TryCreateSendPreview(StoreLightningWalletContext context, string? bolt11, out SendPreviewViewModel? preview, out string? error);
    Task<SendExecutionResult> SendAsync(StoreLightningWalletContext context, string bolt11, CancellationToken cancellationToken = default);
    Task<ActionResultViewModel> ConnectPeerAsync(StoreLightningWalletContext context, string? nodeUri, CancellationToken cancellationToken = default);
    Task PopulatePeersAsync(PeersViewModel model, StoreLightningWalletContext context, CancellationToken cancellationToken = default);
    Task PopulateChannelsAsync(ChannelsViewModel model, StoreLightningWalletContext context, CancellationToken cancellationToken = default);
    bool TryCreateOpenChannelPreview(
        StoreLightningWalletContext context,
        string? nodeUri,
        string? channelAmountSats,
        string? feeRateSatsPerByte,
        out OpenChannelPreviewViewModel? preview,
        out string? error);
    bool TryCreateCloseChannelPreview(
        StoreLightningWalletContext context,
        string? channelId,
        string? channelPoint,
        string? remoteNode,
        out CloseChannelPreviewViewModel? preview,
        out string? error);
    Task<ActionResultViewModel> OpenChannelAsync(
        StoreLightningWalletContext context,
        string nodeUri,
        string channelAmountSats,
        string? feeRateSatsPerByte,
        CancellationToken cancellationToken = default);
    Task<ActionResultViewModel> CloseChannelAsync(
        StoreLightningWalletContext context,
        string? channelId,
        string? channelPoint,
        CancellationToken cancellationToken = default);
}

public class LightningWalletService : ILightningWalletService
{
    private const decimal DefaultChannelOpenFeeRate = 1.0m;
    private const long MinimumLndChannelAmountSats = 20_000;
    private const string SharedInternalNodeReadOnlyMessage = "Wallet actions are disabled for stores using the server's shared internal Lightning node.";

    public virtual LightningWalletTabsViewModel CreateTabs(StoreLightningWalletContext context, string activePage)
    {
        return new LightningWalletTabsViewModel
        {
            StoreId = context.StoreId,
            CryptoCode = context.CryptoCode,
            ActivePage = activePage,
            ShowSend = !context.IsReadOnly && context.Capabilities.CanPayBolt11,
            ShowPeers = !context.IsReadOnly && context.Capabilities.CanConnectPeer,
            ShowChannels = !context.IsReadOnly && (context.Capabilities.CanListChannels || context.Capabilities.CanOpenChannel || context.Capabilities.CanCloseChannel)
        };
    }

    public virtual async Task PopulateOverviewAsync(
        OverviewViewModel model,
        StoreLightningWalletContext context,
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
            catch (Exception ex)
            {
                model.Notices.Add($"Could not load node information: {ex.Message}");
            }
        }

        if (context.Capabilities.CanGetBalance)
        {
            try
            {
                var balance = await context.Client.GetBalance(cancellationToken);
                if (balance.OnchainBalance is not null)
                {
                    model.OnchainBalanceRows.Add(new ValueRowViewModel { Label = "Confirmed", Value = FormatMoney(balance.OnchainBalance.Confirmed) });
                    model.OnchainBalanceRows.Add(new ValueRowViewModel { Label = "Unconfirmed", Value = FormatMoney(balance.OnchainBalance.Unconfirmed) });
                    model.OnchainBalanceRows.Add(new ValueRowViewModel { Label = "Reserved", Value = FormatMoney(balance.OnchainBalance.Reserved) });
                }

                if (balance.OffchainBalance is not null)
                {
                    model.OffchainBalanceRows.Add(new ValueRowViewModel { Label = "Opening", Value = FormatLightMoney(balance.OffchainBalance.Opening) });
                    model.OffchainBalanceRows.Add(new ValueRowViewModel { Label = "Local", Value = FormatLightMoney(balance.OffchainBalance.Local) });
                    model.OffchainBalanceRows.Add(new ValueRowViewModel { Label = "Remote", Value = FormatLightMoney(balance.OffchainBalance.Remote) });
                    model.OffchainBalanceRows.Add(new ValueRowViewModel { Label = "Closing", Value = FormatLightMoney(balance.OffchainBalance.Closing) });
                }
            }
            catch (NotSupportedException)
            {
                model.Notices.Add("Balance information is not available for this backend.");
            }
            catch (Exception ex)
            {
                model.Notices.Add($"Could not load balances: {ex.Message}");
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

    public virtual bool TryCreateSendPreview(StoreLightningWalletContext context, string? bolt11, out SendPreviewViewModel? preview, out string? error)
    {
        preview = null;
        error = null;

        if (!context.IsConfigured || context.Client is null || context.Network is null)
        {
            error = "Lightning is not available for this store.";
            return false;
        }

        if (context.IsReadOnly)
        {
            error = SharedInternalNodeReadOnlyMessage;
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

        if (paymentRequest.MinimumAmount is null || paymentRequest.MinimumAmount == LightMoney.Zero)
        {
            error = "Amountless invoices are not supported by this wallet UI.";
            return false;
        }

        if (paymentRequest.ExpiryDate <= DateTimeOffset.UtcNow)
        {
            error = "This invoice has already expired.";
            return false;
        }

        preview = new SendPreviewViewModel
        {
            Bolt11 = bolt11.Trim(),
            AmountDisplay = FormatLightMoney(paymentRequest.MinimumAmount),
            Description = paymentRequest.ShortDescription ?? "No description",
            PaymentHash = paymentRequest.PaymentHash?.ToString() ?? "Unavailable",
            Payee = paymentRequest.GetPayeePubKey().ToString(),
            ExpiresAt = paymentRequest.ExpiryDate
        };
        return true;
    }

    public virtual async Task<SendExecutionResult> SendAsync(
        StoreLightningWalletContext context,
        string bolt11,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateSendPreview(context, bolt11, out _, out var validationError))
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
            var payResponse = await context.Client!.Pay(bolt11, cancellationToken);
            var details = await TryLoadPaymentDetailsAsync(context.Client, bolt11, context.Network!.NBitcoinNetwork, payResponse, cancellationToken);

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
                        IsSuccess = true,
                        Message = "Payment submitted, but the final status is still unknown."
                    },
                    Payment = details
                },
                PayResult.CouldNotFindRoute => new SendExecutionResult
                {
                    Result = new ActionResultViewModel
                    {
                        IsSuccess = false,
                        Message = "No route to the invoice destination was found.",
                        Detail = payResponse.ErrorDetail
                    }
                },
                PayResult.Error => new SendExecutionResult
                {
                    Result = new ActionResultViewModel
                    {
                        IsSuccess = false,
                        Message = NormalizePayError(payResponse.ErrorDetail),
                        Detail = payResponse.ErrorDetail
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
        catch (Exception ex)
        {
            return new SendExecutionResult
            {
                Result = new ActionResultViewModel
                {
                    IsSuccess = false,
                    Message = "Lightning payment failed.",
                    Detail = ex.Message
                }
            };
        }
    }

    public virtual async Task<ActionResultViewModel> ConnectPeerAsync(
        StoreLightningWalletContext context,
        string? nodeUri,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            return Failure("Lightning is not available for this store.");
        }

        if (context.IsReadOnly)
        {
            return Failure(SharedInternalNodeReadOnlyMessage);
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
        catch (Exception ex)
        {
            return Failure("Peer connection failed.", ex.Message);
        }
    }

    public virtual async Task PopulatePeersAsync(
        PeersViewModel model,
        StoreLightningWalletContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            model.PeerListMessage = "Lightning is not available for this store.";
            return;
        }

        try
        {
            var peers = await TryListPeersAsync(context.Client, cancellationToken);
            if (peers is null)
            {
                model.PeerListMessage = "Peer listing is not available for this backend.";
                return;
            }

            foreach (var peer in peers.OrderBy(p => p.NodeId, StringComparer.OrdinalIgnoreCase))
            {
                model.Peers.Add(peer);
            }

            if (model.Peers.Count == 0)
            {
                model.PeerListMessage = "No peers found.";
            }
        }
        catch (NotSupportedException)
        {
            model.PeerListMessage = "Peer listing is not available for this backend.";
        }
        catch (Exception ex)
        {
            model.PeerListMessage = $"Could not load peers: {ex.Message}";
        }
    }

    public virtual async Task PopulateChannelsAsync(
        ChannelsViewModel model,
        StoreLightningWalletContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured || context.Client is null)
        {
            model.ChannelListMessage = "Lightning is not available for this store.";
            return;
        }

        if (context.IsReadOnly)
        {
            model.ChannelListMessage = SharedInternalNodeReadOnlyMessage;
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
                var remoteBalance = channel.Capacity - channel.LocalBalance;
                model.Channels.Add(new LightningChannelItemViewModel
                {
                    ChannelId = channel.ChannelId,
                    RemoteNode = channel.RemoteNode.ToString(),
                    ChannelPoint = channel.ChannelPoint.ToString(),
                    CapacitySats = channel.Capacity.ToUnit(LightMoneyUnit.Satoshi),
                    LocalBalanceSats = channel.LocalBalance.ToUnit(LightMoneyUnit.Satoshi),
                    RemoteBalanceSats = remoteBalance.ToUnit(LightMoneyUnit.Satoshi),
                    CapacityDisplay = FormatLightMoney(channel.Capacity),
                    LocalBalanceDisplay = FormatLightMoney(channel.LocalBalance),
                    RemoteBalanceDisplay = FormatLightMoney(remoteBalance),
                    IsActive = channel.IsActive,
                    IsPublic = channel.IsPublic,
                    CanClose = context.Capabilities.CanCloseChannel && !string.IsNullOrWhiteSpace(channel.ChannelId)
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
        catch (Exception ex)
        {
            model.ChannelListMessage = $"Could not load channels: {ex.Message}";
        }
    }

    public virtual bool TryCreateOpenChannelPreview(
        StoreLightningWalletContext context,
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
            FeeRateDisplay = $"{request.FeeRate.SatoshiPerByte:0.########} sat/vB"
        };
        return true;
    }

    public virtual bool TryCreateCloseChannelPreview(
        StoreLightningWalletContext context,
        string? channelId,
        string? channelPoint,
        string? remoteNode,
        out CloseChannelPreviewViewModel? preview,
        out string? error)
    {
        preview = null;
        error = null;

        if (!TryBuildCloseChannelRequest(context, channelId, channelPoint, out var request, out error))
        {
            return false;
        }

        preview = new CloseChannelPreviewViewModel
        {
            ChannelId = request!.ChannelId ?? string.Empty,
            ChannelPoint = request.ChannelPoint?.ToString() ?? channelPoint?.Trim() ?? string.Empty,
            RemoteNode = remoteNode?.Trim() ?? string.Empty
        };
        return true;
    }

    public virtual async Task<ActionResultViewModel> OpenChannelAsync(
        StoreLightningWalletContext context,
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
            return Failure("Channel opening failed.", ex.Message);
        }
    }

    public virtual async Task<ActionResultViewModel> CloseChannelAsync(
        StoreLightningWalletContext context,
        string? channelId,
        string? channelPoint,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildCloseChannelRequest(context, channelId, channelPoint, out var request, out var error))
        {
            return Failure(error ?? "Invalid close channel request.");
        }

        try
        {
            var response = await context.Client!.CloseChannel(request!, cancellationToken);
            return response.Result switch
            {
                CloseChannelResult.Ok => Success("Channel close request submitted."),
                CloseChannelResult.ChannelNotFound => Failure("The channel could not be found.", response.Details),
                CloseChannelResult.AlreadyClosing => Failure("The channel is already closing.", response.Details),
                _ => Failure(NormalizeCloseError(response.Details), response.Details)
            };
        }
        catch (NotSupportedException)
        {
            return Failure("Channel closing is not supported by this backend.");
        }
        catch (Exception ex)
        {
            return Failure("Channel closing failed.", ex.Message);
        }
    }

    protected virtual bool TryBuildOpenChannelRequest(
        StoreLightningWalletContext context,
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

        if (context.IsReadOnly)
        {
            error = SharedInternalNodeReadOnlyMessage;
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

        if (!long.TryParse(channelAmountSats?.Trim(), out var sats) || sats <= 0)
        {
            error = "Channel amount must be a positive whole number of sats.";
            return false;
        }

        if (IsLndClient(context.Client) && sats < MinimumLndChannelAmountSats)
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
            ChannelAmount = Money.Satoshis(sats),
            FeeRate = feeRate
        };
        return true;
    }

    protected virtual bool TryBuildCloseChannelRequest(
        StoreLightningWalletContext context,
        string? channelId,
        string? channelPoint,
        out CloseChannelRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (!context.IsConfigured || context.Client is null)
        {
            error = "Lightning is not available for this store.";
            return false;
        }

        if (context.IsReadOnly)
        {
            error = SharedInternalNodeReadOnlyMessage;
            return false;
        }

        if (!context.Capabilities.CanCloseChannel)
        {
            error = "Channel closing is not supported by this backend.";
            return false;
        }

        if (!TryParseChannelPoint(channelPoint, out var parsedChannelPoint) && string.IsNullOrWhiteSpace(channelId))
        {
            error = "A valid channel identifier is required to close the channel.";
            return false;
        }

        request = new CloseChannelRequest
        {
            ChannelId = string.IsNullOrWhiteSpace(channelId) ? null : channelId.Trim(),
            ChannelPoint = parsedChannelPoint
        };
        return true;
    }

    private static bool TryParseFeeRate(string? feeRateSatsPerByte, out FeeRate feeRate)
    {
        if (string.IsNullOrWhiteSpace(feeRateSatsPerByte))
        {
            feeRate = new FeeRate(DefaultChannelOpenFeeRate);
            return true;
        }

        if (decimal.TryParse(feeRateSatsPerByte.Trim(), out var satPerByte) && satPerByte > 0)
        {
            feeRate = new FeeRate(satPerByte);
            return true;
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

    private static string NormalizePayError(string? errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorDetail))
        {
            return "Lightning payment failed.";
        }

        if (errorDetail.Contains("route", StringComparison.OrdinalIgnoreCase))
        {
            return "No route to the invoice destination was found.";
        }

        if (errorDetail.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
            errorDetail.Contains("balance", StringComparison.OrdinalIgnoreCase))
        {
            return "Insufficient balance to send the payment.";
        }

        return "Lightning payment failed.";
    }

    private static string NormalizeCloseError(string? errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorDetail))
        {
            return "The channel could not be closed.";
        }

        if (errorDetail.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
            errorDetail.Contains("offline", StringComparison.OrdinalIgnoreCase))
        {
            return "The peer is offline, so the channel could not be closed cleanly.";
        }

        return "The channel could not be closed.";
    }

    private static bool TryParseChannelPoint(string? channelPoint, out OutPoint? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(channelPoint))
        {
            return false;
        }

        var raw = channelPoint.Trim();
        if (OutPoint.TryParse(raw, out var direct))
        {
            parsed = direct;
            return true;
        }

        if (OutPoint.TryParse(raw.Replace(':', '-'), out var normalized))
        {
            parsed = normalized;
            return true;
        }

        return false;
    }

    private static ActionResultViewModel Success(string message)
    {
        return new ActionResultViewModel { IsSuccess = true, Message = message };
    }

    private static ActionResultViewModel Failure(string message, string? detail = null)
    {
        return new ActionResultViewModel { IsSuccess = false, Message = message, Detail = detail };
    }

    private static async Task<List<LightningPeerItemViewModel>?> TryListPeersAsync(ILightningClient client, CancellationToken cancellationToken)
    {
        var methodTarget = FindPeerListingMethod(client);
        if (methodTarget is null)
        {
            return null;
        }

        var response = await InvokeAsync(methodTarget.Value.Method, methodTarget.Value.Target, cancellationToken);
        if (response is null)
        {
            return [];
        }

        var peersEnumerable = GetEnumerableProperty(response, "Peers");
        if (peersEnumerable is null)
        {
            return [];
        }

        var peers = new List<LightningPeerItemViewModel>();
        foreach (var peer in peersEnumerable)
        {
            if (peer is null)
            {
                continue;
            }

            var nodeId = GetStringProperty(peer, "PubKey") ??
                         GetStringProperty(peer, "PeerId") ??
                         GetStringProperty(peer, "NodeId");
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                continue;
            }

            var isInbound = GetBoolProperty(peer, "Inbound");
            peers.Add(new LightningPeerItemViewModel
            {
                NodeId = nodeId,
                Address = GetPeerAddress(peer),
                Direction = isInbound switch
                {
                    true => "Inbound",
                    false => "Outbound",
                    null => null
                },
                BytesSentDisplay = FormatByteCount(GetLongProperty(peer, "BytesSent")),
                BytesReceivedDisplay = FormatByteCount(GetLongProperty(peer, "BytesRecv") ?? GetLongProperty(peer, "BytesReceived"))
            });
        }

        return peers;
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

    private static async Task<object?> InvokeAsync(MethodInfo method, object target, CancellationToken cancellationToken)
    {
        object? invocationResult = method.GetParameters() switch
        {
            [] => method.Invoke(target, []),
            [{ ParameterType: var parameterType }] when parameterType == typeof(CancellationToken) => method.Invoke(target, [cancellationToken]),
            _ => null
        };

        if (invocationResult is not Task task)
        {
            return invocationResult;
        }

        await task;
        return task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);
    }

    private static (object Target, MethodInfo Method)? FindPeerListingMethod(object client)
    {
        foreach (var target in EnumeratePeerListingTargets(client))
        {
            var method = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name.EndsWith("ListPeersAsync", StringComparison.Ordinal) &&
                    HasSupportedPeerListingSignature(m));

            if (method is not null)
            {
                return (target, method);
            }
        }

        return null;
    }

    private static bool HasSupportedPeerListingSignature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 0 ||
               (parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken));
    }

    private static IEnumerable<object> EnumeratePeerListingTargets(object client)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((client, 0));

        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            if (!seen.Add(value))
            {
                continue;
            }

            yield return value;
            if (depth >= 3)
            {
                continue;
            }

            foreach (var nested in GetNestedTargets(value))
            {
                queue.Enqueue((nested, depth + 1));
            }
        }
    }

    private static IEnumerable<object> GetNestedTargets(object source)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (var property in source.GetType().GetProperties(flags))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(source);
            }
            catch
            {
                continue;
            }

            if (IsPeerListingTarget(value))
            {
                yield return value!;
            }
        }

        foreach (var field in source.GetType().GetFields(flags))
        {
            object? value;
            try
            {
                value = field.GetValue(source);
            }
            catch
            {
                continue;
            }

            if (IsPeerListingTarget(value))
            {
                yield return value!;
            }
        }
    }

    private static bool IsPeerListingTarget(object? value)
    {
        if (value is null || value is string)
        {
            return false;
        }

        var type = value.GetType();
        return !type.IsPrimitive &&
               !type.IsEnum &&
               type.Namespace != typeof(string).Namespace;
    }

    private static IEnumerable? GetEnumerableProperty(object source, string propertyName)
    {
        var value = GetPropertyValue(source, propertyName);
        return value is IEnumerable enumerable && value is not string ? enumerable : null;
    }

    private static object? GetPropertyValue(object source, string propertyName)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
        var normalizedName = NormalizeMemberName(propertyName);

        foreach (var property in source.GetType().GetProperties(flags))
        {
            if (NormalizeMemberName(property.Name) != normalizedName || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            try
            {
                return property.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        foreach (var field in source.GetType().GetFields(flags))
        {
            if (NormalizeMemberName(field.Name) != normalizedName)
            {
                continue;
            }

            try
            {
                return field.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? GetStringProperty(object source, string propertyName)
    {
        var value = GetPropertyValue(source, propertyName);
        return value switch
        {
            null => null,
            string text when string.IsNullOrWhiteSpace(text) => null,
            string text => text,
            _ => value.ToString()
        };
    }

    private static bool? GetBoolProperty(object source, string propertyName)
    {
        var value = GetPropertyValue(source, propertyName);
        return value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var boolean) => boolean,
            _ => null
        };
    }

    private static long? GetLongProperty(object source, string propertyName)
    {
        var value = GetPropertyValue(source, propertyName);
        return value switch
        {
            byte number => number,
            ushort number => number,
            short number => number,
            uint number => number,
            int number => number,
            long number => number,
            ulong number when number <= long.MaxValue => (long)number,
            string text when long.TryParse(text, out var number) => number,
            _ => null
        };
    }

    private static string? GetPeerAddress(object peer)
    {
        var address = GetStringProperty(peer, "Address");
        if (!string.IsNullOrWhiteSpace(address))
        {
            return address;
        }

        var addresses = GetEnumerableProperty(peer, "Addresses");
        if (addresses is null)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in addresses)
        {
            if (item is null)
            {
                continue;
            }

            var itemText = GetStringProperty(item, "Addr") ??
                           GetStringProperty(item, "Address") ??
                           item.ToString();
            if (!string.IsNullOrWhiteSpace(itemText))
            {
                values.Add(itemText);
            }
        }

        return values.Count == 0 ? null : string.Join(", ", values);
    }

    private static string? FormatByteCount(long? value)
    {
        return value is null ? null : $"{value.Value} bytes";
    }

    private static string NormalizeMemberName(string value)
    {
        return value.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static string FormatMoney(Money amount)
    {
        return $"{amount.Satoshi} sats ({amount.ToDecimal(MoneyUnit.BTC):0.########} BTC)";
    }

    private static string FormatLightMoney(LightMoney amount)
    {
        return $"{amount.ToUnit(LightMoneyUnit.Satoshi):0.########} sats";
    }
}
