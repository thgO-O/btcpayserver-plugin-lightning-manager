using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;

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

    public StoreLightningManagerContextFactory(
        BTCPayNetworkProvider networkProvider,
        PaymentMethodHandlerDictionary handlers,
        LightningClientFactoryService lightningClientFactory)
    {
        _networkProvider = networkProvider;
        _handlers = handlers;
        _lightningClientFactory = lightningClientFactory;
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

        return CreateUnavailableContext(
            store,
            cryptoCode,
            "Lightning Manager supports external BTC Lightning backends only.");
    }

    private static StoreLightningManagerContext CreateUnavailableContext(
        StoreData store,
        string cryptoCode,
        string configurationError)
    {
        return new StoreLightningManagerContext
        {
            StoreId = store.Id,
            CryptoCode = cryptoCode,
            BackendFingerprint = string.Empty,
            BackendIdentityFingerprint = string.Empty,
            Capabilities = LightningCapabilities.None,
            ConfigurationError = configurationError
        };
    }

}
