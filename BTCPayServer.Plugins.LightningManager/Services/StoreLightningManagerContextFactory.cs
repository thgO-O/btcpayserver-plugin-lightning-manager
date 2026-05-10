#nullable enable
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

public class StoreLightningManagerContext
{
    public required StoreData Store { get; init; }
    public required string StoreId { get; init; }
    public required string CryptoCode { get; init; }
    public BTCPayNetwork? Network { get; init; }
    public LightningPaymentMethodConfig? PaymentMethodConfig { get; init; }
    public ILightningClient? Client { get; init; }
    public string? ConnectionString { get; init; }
    public bool IsInternalNode { get; init; }
    public bool IsSharedBackend { get; init; }
    public bool IsReadOnly { get; init; }
    public required LightningCapabilities Capabilities { get; init; }
    public LightningCapabilities BackendCapabilities { get; init; } = LightningCapabilities.None;
    public string? DisplayName { get; init; }
    public string? NodeHost { get; init; }
    public string? SharedBackendNotice { get; init; }
    public string? ConfigurationError { get; init; }
    public bool IsConfigured => Client is not null && string.IsNullOrEmpty(ConfigurationError);
}

public interface IStoreLightningManagerContextFactory
{
    Task<StoreLightningManagerContext> CreateAsync(
        StoreData store,
        string cryptoCode,
        CancellationToken cancellationToken = default);
}

public class StoreLightningManagerContextFactory : IStoreLightningManagerContextFactory
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly LightningClientFactoryService _lightningClientFactory;
    private readonly ILightningCapabilityService _lightningCapabilityService;
    private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;

    public StoreLightningManagerContextFactory(
        BTCPayNetworkProvider networkProvider,
        PaymentMethodHandlerDictionary handlers,
        LightningClientFactoryService lightningClientFactory,
        ILightningCapabilityService lightningCapabilityService,
        IOptions<LightningNetworkOptions> lightningNetworkOptions)
    {
        _networkProvider = networkProvider;
        _handlers = handlers;
        _lightningClientFactory = lightningClientFactory;
        _lightningCapabilityService = lightningCapabilityService;
        _lightningNetworkOptions = lightningNetworkOptions;
    }

    public virtual Task<StoreLightningManagerContext> CreateAsync(
        StoreData store,
        string cryptoCode,
        CancellationToken cancellationToken = default)
    {
        var requestedCryptoCode = cryptoCode;
        if (!LightningManagerCrypto.TryNormalizeSupported(cryptoCode, out cryptoCode))
        {
            return Task.FromResult(CreateUnavailableContext(
                store,
                requestedCryptoCode?.ToUpperInvariant() ?? string.Empty,
                LightningManagerCrypto.UnsupportedMessage));
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
        if (network is null)
        {
            return Task.FromResult(CreateUnavailableContext(store, cryptoCode, "Unsupported cryptocurrency."));
        }

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode);
        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, _handlers);
        if (config is null)
        {
            return Task.FromResult(CreateUnavailableContext(store, cryptoCode, "Lightning is not configured for this store.", network, config));
        }

        if (config.GetExternalLightningUrl() is { } connectionString)
        {
            try
            {
                var client = _lightningClientFactory.Create(connectionString, network);
                var capabilities = _lightningCapabilityService.GetCapabilities(client, connectionString, false);
                return Task.FromResult(new StoreLightningManagerContext
                {
                    Store = store,
                    StoreId = store.Id,
                    CryptoCode = cryptoCode,
                    Network = network,
                    PaymentMethodConfig = config,
                    Client = client,
                    ConnectionString = connectionString,
                    IsInternalNode = false,
                    Capabilities = capabilities,
                    BackendCapabilities = capabilities,
                    DisplayName = client.GetDisplayName(connectionString),
                    NodeHost = client.GetServerUri(connectionString)?.Host
                });
            }
            catch (Exception)
            {
                return Task.FromResult(CreateUnavailableContext(store, cryptoCode, "Lightning backend unavailable.", network, config, connectionString));
            }
        }

        if (!config.IsInternalNode)
        {
            return Task.FromResult(CreateUnavailableContext(store, cryptoCode, "Lightning configuration is invalid.", network, config));
        }

        if (!_lightningNetworkOptions.Value.InternalLightningByCryptoCode.TryGetValue(cryptoCode.ToUpperInvariant(), out var internalClient))
        {
            return Task.FromResult(CreateUnavailableContext(store, cryptoCode, "The internal Lightning node is not available.", network, config, isInternalNode: true));
        }

        var backendCapabilities = _lightningCapabilityService.GetCapabilities(internalClient, null, true);

        return Task.FromResult(new StoreLightningManagerContext
        {
            Store = store,
            StoreId = store.Id,
            CryptoCode = cryptoCode,
            Network = network,
            PaymentMethodConfig = config,
            Client = internalClient,
            IsInternalNode = true,
            IsSharedBackend = true,
            IsReadOnly = true,
            Capabilities = LightningCapabilities.None,
            BackendCapabilities = backendCapabilities,
            DisplayName = "Internal node"
        });
    }

    private StoreLightningManagerContext CreateUnavailableContext(
        StoreData store,
        string cryptoCode,
        string configurationError,
        BTCPayNetwork? network = null,
        LightningPaymentMethodConfig? config = null,
        string? connectionString = null,
        bool isInternalNode = false)
    {
        return new StoreLightningManagerContext
        {
            Store = store,
            StoreId = store.Id,
            CryptoCode = cryptoCode,
            Network = network,
            PaymentMethodConfig = config,
            ConnectionString = connectionString,
            IsInternalNode = isInternalNode,
            Capabilities = LightningCapabilities.None,
            BackendCapabilities = LightningCapabilities.None,
            ConfigurationError = configurationError
        };
    }

}
