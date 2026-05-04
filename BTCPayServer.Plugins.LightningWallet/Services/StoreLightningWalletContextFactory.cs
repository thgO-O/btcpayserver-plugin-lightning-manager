#nullable enable
using System.Security.Claims;
using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Security;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.LightningWallet.Services;

public class StoreLightningWalletContext
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
    public string? DisplayName { get; init; }
    public string? NodeHost { get; init; }
    public string? SharedBackendNotice { get; init; }
    public string? ConfigurationError { get; init; }
    public bool IsConfigured => Client is not null && string.IsNullOrEmpty(ConfigurationError);
}

public interface IStoreLightningWalletContextFactory
{
    Task<StoreLightningWalletContext> CreateAsync(
        StoreData store,
        string cryptoCode,
        ClaimsPrincipal user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
}

public class StoreLightningWalletContextFactory : IStoreLightningWalletContextFactory
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly LightningClientFactoryService _lightningClientFactory;
    private readonly ILightningCapabilityService _lightningCapabilityService;
    private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
    private readonly IAuthorizationService _authorizationService;

    public StoreLightningWalletContextFactory(
        BTCPayNetworkProvider networkProvider,
        PaymentMethodHandlerDictionary handlers,
        LightningClientFactoryService lightningClientFactory,
        ILightningCapabilityService lightningCapabilityService,
        IOptions<LightningNetworkOptions> lightningNetworkOptions,
        IAuthorizationService authorizationService)
    {
        _networkProvider = networkProvider;
        _handlers = handlers;
        _lightningClientFactory = lightningClientFactory;
        _lightningCapabilityService = lightningCapabilityService;
        _lightningNetworkOptions = lightningNetworkOptions;
        _authorizationService = authorizationService;
    }

    public virtual async Task<StoreLightningWalletContext> CreateAsync(
        StoreData store,
        string cryptoCode,
        ClaimsPrincipal user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
        if (network is null)
        {
            return CreateUnavailableContext(store, cryptoCode, "Unsupported cryptocurrency.");
        }

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId(cryptoCode);
        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, _handlers);
        if (config is null)
        {
            return CreateUnavailableContext(store, cryptoCode, "Lightning is not configured for this store.", network, config);
        }

        if (config.GetExternalLightningUrl() is { } connectionString)
        {
            try
            {
                var client = _lightningClientFactory.Create(connectionString, network);
                var capabilities = _lightningCapabilityService.GetCapabilities(client, connectionString, false);
                return new StoreLightningWalletContext
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
                    DisplayName = client.GetDisplayName(connectionString),
                    NodeHost = client.GetServerUri(connectionString)?.Host
                };
            }
            catch (Exception)
            {
                return CreateUnavailableContext(store, cryptoCode, "Lightning backend unavailable.", network, config, connectionString);
            }
        }

        if (!config.IsInternalNode)
        {
            return CreateUnavailableContext(store, cryptoCode, "Lightning configuration is invalid.", network, config);
        }

        var authorizationResult = await _authorizationService.AuthorizeAsync(
            user,
            null,
            new PolicyRequirement(Policies.CanUseInternalLightningNode));

        if (!authorizationResult.Succeeded)
        {
            return CreateUnavailableContext(store, cryptoCode, "You are not allowed to use the internal Lightning node.", network, config, isInternalNode: true);
        }

        if (!_lightningNetworkOptions.Value.InternalLightningByCryptoCode.TryGetValue(cryptoCode.ToUpperInvariant(), out var internalClient))
        {
            return CreateUnavailableContext(store, cryptoCode, "The internal Lightning node is not available.", network, config, isInternalNode: true);
        }

        var isServerAdmin = user.IsInRole(Roles.ServerAdmin);
        var backendCapabilities = _lightningCapabilityService.GetCapabilities(internalClient, null, true);
        var isReadOnly = !isServerAdmin;

        return new StoreLightningWalletContext
        {
            Store = store,
            StoreId = store.Id,
            CryptoCode = cryptoCode,
            Network = network,
            PaymentMethodConfig = config,
            Client = internalClient,
            IsInternalNode = true,
            IsSharedBackend = true,
            IsReadOnly = isReadOnly,
            Capabilities = isReadOnly ? CreateReadOnlyCapabilities(backendCapabilities) : backendCapabilities,
            DisplayName = "Internal node",
            SharedBackendNotice = isReadOnly
                ? "This store uses the server's shared internal Lightning node. Balances shown here are node-wide, not store-specific. Wallet actions are disabled for non-admin users."
                : "This store uses the server's shared internal Lightning node. Balances shown here are node-wide, not store-specific. Wallet actions here affect every store using this backend."
        };
    }

    private StoreLightningWalletContext CreateUnavailableContext(
        StoreData store,
        string cryptoCode,
        string configurationError,
        BTCPayNetwork? network = null,
        LightningPaymentMethodConfig? config = null,
        string? connectionString = null,
        bool isInternalNode = false)
    {
        return new StoreLightningWalletContext
        {
            Store = store,
            StoreId = store.Id,
            CryptoCode = cryptoCode,
            Network = network,
            PaymentMethodConfig = config,
            ConnectionString = connectionString,
            IsInternalNode = isInternalNode,
            Capabilities = LightningCapabilities.None,
            ConfigurationError = configurationError
        };
    }

    private static LightningCapabilities CreateReadOnlyCapabilities(LightningCapabilities capabilities)
    {
        return new LightningCapabilities
        {
            CanGetInfo = capabilities.CanGetInfo,
            CanGetBalance = capabilities.CanGetBalance
        };
    }
}
