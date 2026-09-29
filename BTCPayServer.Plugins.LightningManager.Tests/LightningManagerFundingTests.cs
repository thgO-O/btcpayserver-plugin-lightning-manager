using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Plugins.LightningManager.ViewModels;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerFundingTests
{
    [Fact]
    public void AddressGenerationRequiresAntiForgeryAndCannotBeCached()
    {
        var action = typeof(BTCPayServer.Plugins.LightningManager.Controllers.LightningManagerController)
            .GetMethod("GenerateDepositAddress")!;
        Assert.Single(action.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        var cache = Assert.IsType<ResponseCacheAttribute>(Assert.Single(action.GetCustomAttributes(typeof(ResponseCacheAttribute), true)));
        Assert.True(cache.NoStore);
    }

    [Fact]
    public async Task AddressGenerationHonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeLightningClient { GetDepositAddressHandler = token => Task.FromCanceled<BitcoinAddress>(token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestLightningManagerServiceFactory.Create().PopulateDepositAddressAsync(new FundViewModel(),
                TestContextFactory.CreateConfigured(LightningCapabilities.Full, client), cts.Token));
    }

    [Theory]
    [InlineData("lnd-rest", true)]
    [InlineData("lnd-grpc", true)]
    [InlineData("clightning", true)]
    [InlineData("eclair", true)]
    [InlineData("phoenixd", false)]
    [InlineData("blink", false)]
    public void FundingCapabilityMatchesBackend(string backend, bool expected)
    {
        Assert.Equal(expected, LightningCapabilityService.GetCapabilities(
            $"type={backend};server=https://example.test;api-key=test;currency=BTC").CanGetDepositAddress);
    }

    [Fact]
    public async Task FundingAddressComesFromNodeAndUsesBitcoinUri()
    {
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        using var cts = new CancellationTokenSource();
        var client = new FakeLightningClient { GetDepositAddressHandler = token =>
        {
            Assert.Equal(cts.Token, token);
            return Task.FromResult<BitcoinAddress>(address);
        }};
        var controller = TestControllerFactory.CreateController(TestContextFactory.CreateConfigured(LightningCapabilities.Full, client));
        var result = Assert.IsType<ViewResult>(await controller.GenerateDepositAddress("BTC", cts.Token));
        var model = Assert.IsType<FundViewModel>(result.Model);
        Assert.Equal(address.ToString(), model.Address);
        Assert.Equal($"bitcoin:{address}", model.BitcoinUri);
        Assert.Null(model.Error);
    }

    [Fact]
    public void FundingGetDoesNotGenerateAnAddress()
    {
        var client = new FakeLightningClient { GetDepositAddressHandler = _ => throw new Exception("must not run") };
        var controller = TestControllerFactory.CreateController(TestContextFactory.CreateConfigured(LightningCapabilities.Full, client));
        var model = Assert.IsType<FundViewModel>(Assert.IsType<ViewResult>(controller.Fund("BTC")).Model);
        Assert.Null(model.Address);
        Assert.NotEmpty(model.NetworkName!);
    }

    [Fact]
    public async Task UnsupportedBackendDoesNotCallNode()
    {
        var calls = 0;
        var client = new FakeLightningClient { GetDepositAddressHandler = _ => { calls++; throw new Exception(); } };
        var model = new FundViewModel();
        await TestLightningManagerServiceFactory.Create().PopulateDepositAddressAsync(model,
            TestContextFactory.CreateConfigured(LightningCapabilities.Phoenixd, client));
        Assert.Equal(0, calls);
        Assert.Null(model.Address);
        Assert.NotNull(model.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidNetworkOrBackendFailureNeverExposesDestinationOrSecrets(bool wrongNetwork)
    {
        var client = new FakeLightningClient { GetDepositAddressHandler = _ => wrongNetwork
            ? Task.FromResult<BitcoinAddress>(new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main))
            : throw new Exception("secret-macaroon") };
        var model = new FundViewModel();
        await TestLightningManagerServiceFactory.Create().PopulateDepositAddressAsync(model,
            TestContextFactory.CreateConfigured(LightningCapabilities.Full, client));
        Assert.Null(model.Address);
        Assert.Null(model.BitcoinUri);
        Assert.NotNull(model.Error);
        Assert.DoesNotContain("secret-macaroon", model.Error);
    }
}
