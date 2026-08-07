using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.LightningManager.Services;

public sealed class StoreLightningManagerContext
{
    public required string StoreId { get; init; }
    public required string CryptoCode { get; init; }
    public BTCPayNetwork? Network { get; init; }
    public ILightningClient? Client { get; init; }
    public string? BackendType { get; init; }
    public required string BackendFingerprint { get; init; }
    public required string BackendIdentityFingerprint { get; init; }
    public required LightningCapabilities Capabilities { get; init; }
    public string? DisplayName { get; init; }
    public string? NodeHost { get; init; }
    public bool IsInternalNode { get; init; }
    public string? ConfigurationError { get; init; }
    public bool IsConfigured =>
        Client is not null &&
        !string.IsNullOrEmpty(BackendType) &&
        !string.IsNullOrEmpty(BackendFingerprint) &&
        !string.IsNullOrEmpty(BackendIdentityFingerprint) &&
        string.IsNullOrEmpty(ConfigurationError);
}

public interface IStoreLightningManagerContextFactory
{
    StoreLightningManagerContext Create(StoreData store, string cryptoCode);
}

public sealed class StoreLightningManagerContextFactory : IStoreLightningManagerContextFactory
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly LightningClientFactoryService _lightningClientFactory;
    private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;

    public StoreLightningManagerContextFactory(
        BTCPayNetworkProvider networkProvider,
        PaymentMethodHandlerDictionary handlers,
        LightningClientFactoryService lightningClientFactory,
        IOptions<LightningNetworkOptions> lightningNetworkOptions)
    {
        _networkProvider = networkProvider;
        _handlers = handlers;
        _lightningClientFactory = lightningClientFactory;
        _lightningNetworkOptions = lightningNetworkOptions;
    }

    public StoreLightningManagerContext Create(StoreData store, string cryptoCode)
    {
        if (!LightningManagerCrypto.IsSupported(cryptoCode))
        {
            return CreateUnavailableContext(
                store,
                cryptoCode?.ToUpperInvariant() ?? string.Empty,
                LightningManagerCrypto.UnsupportedMessage);
        }

        cryptoCode = LightningManagerCrypto.Bitcoin;
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
        if (network is null)
        {
            return CreateUnavailableContext(store, cryptoCode, "Unsupported cryptocurrency.");
        }

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode);
        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, _handlers);
        if (config is null)
        {
            return CreateUnavailableContext(store, cryptoCode, "Lightning is not configured for this store.");
        }

        if (config.GetExternalLightningUrl() is { } connectionString)
        {
            var capabilities = LightningCapabilityService.GetCapabilities(connectionString);
            if (!capabilities.HasAny)
            {
                return CreateUnavailableContext(
                    store,
                    cryptoCode,
                    "Lightning backend is not supported by Lightning Manager.");
            }

            var backendType = LightningBackendTypes.TryGet(connectionString);
            try
            {
                var client = _lightningClientFactory.Create(connectionString, network);
                return new StoreLightningManagerContext
                {
                    StoreId = store.Id,
                    CryptoCode = cryptoCode,
                    Network = network,
                    Client = client,
                    BackendType = backendType,
                    BackendFingerprint = LightningBackendTypes.GetFingerprint(connectionString),
                    BackendIdentityFingerprint = LightningBackendTypes.GetIdentityFingerprint(connectionString),
                    Capabilities = capabilities,
                    DisplayName = client.GetDisplayName(connectionString),
                    NodeHost = client.GetServerUri(connectionString)?.Host
                };
            }
            catch (Exception)
            {
                return CreateUnavailableContext(store, cryptoCode, "Lightning backend unavailable.");
            }
        }

        if (!config.IsInternalNode)
        {
            return CreateUnavailableContext(store, cryptoCode, "Lightning configuration is invalid.");
        }

        if (!_lightningNetworkOptions.Value.InternalLightningByCryptoCode.TryGetValue(
                cryptoCode,
                out var internalClient))
        {
            return CreateUnavailableContext(
                store,
                cryptoCode,
                "BTCPay Server's internal BTC Lightning node is not configured.",
                isInternalNode: true);
        }

        try
        {
            var canonicalConnection = internalClient.ToString();
            if (string.IsNullOrWhiteSpace(canonicalConnection))
            {
                throw new InvalidOperationException("Internal Lightning client has no canonical connection string.");
            }

            var capabilities = LightningCapabilityService.GetCapabilities(canonicalConnection);
            var backendType = LightningBackendTypes.TryGet(canonicalConnection);
            if (!capabilities.HasAny || string.IsNullOrEmpty(backendType))
            {
                return CreateUnavailableContext(
                    store,
                    cryptoCode,
                    "BTCPay Server's internal Lightning backend is not supported by Lightning Manager.",
                    isInternalNode: true);
            }

            return new StoreLightningManagerContext
            {
                StoreId = store.Id,
                CryptoCode = cryptoCode,
                Network = network,
                Client = internalClient,
                BackendType = backendType,
                BackendFingerprint = LightningBackendTypes.GetFingerprint($"internal:{canonicalConnection}"),
                BackendIdentityFingerprint = LightningBackendTypes.GetIdentityFingerprint(canonicalConnection),
                Capabilities = capabilities,
                DisplayName = $"{internalClient.GetDisplayName(canonicalConnection)} · Internal",
                NodeHost = internalClient.GetServerUri(canonicalConnection)?.Host,
                IsInternalNode = true
            };
        }
        catch (Exception)
        {
            return CreateUnavailableContext(
                store,
                cryptoCode,
                "BTCPay Server's internal Lightning backend is not supported by Lightning Manager.",
                isInternalNode: true);
        }
    }

    private static StoreLightningManagerContext CreateUnavailableContext(
        StoreData store,
        string cryptoCode,
        string configurationError,
        bool isInternalNode = false)
    {
        return new StoreLightningManagerContext
        {
            StoreId = store.Id,
            CryptoCode = cryptoCode,
            BackendFingerprint = string.Empty,
            BackendIdentityFingerprint = string.Empty,
            Capabilities = LightningCapabilities.None,
            IsInternalNode = isInternalNode,
            ConfigurationError = configurationError
        };
    }

}
