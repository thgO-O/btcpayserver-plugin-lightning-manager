using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerChannelConnectionTests
{
    private const string NodeUri =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798@127.0.0.1:9735";

    [Fact]
    public async Task ConfirmationWaitsForConnectionAndBlocksConcurrentOpening()
    {
        var connected = new TaskCompletionSource<ConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionCalls = 0;
        var openingCalls = 0;
        var client = new FakeLightningClient
        {
            ConnectToHandler = (node, _) =>
            {
                Assert.Equal(NodeUri, node.ToString());
                connectionCalls++;
                return connected.Task;
            },
            OpenChannelHandler = (request, _) =>
            {
                Assert.True(connected.Task.IsCompletedSuccessfully);
                Assert.Equal(NodeUri, request.NodeInfo.ToString());
                openingCalls++;
                return Task.FromResult(new OpenChannelResponse(OpenChannelResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var service = TestLightningManagerServiceFactory.Create();
        Assert.True(service.TryCreateOpenChannelPreview(context, NodeUri, "100000", "1", out _, out _));
        Assert.Equal(0, connectionCalls);
        Assert.Equal(0, openingCalls);

        var first = service.OpenChannelAsync(context, NodeUri, "100000", "1");
        Assert.False(first.IsCompleted);
        Assert.Equal(0, openingCalls);
        try
        {
            var duplicate = await service.OpenChannelAsync(context, NodeUri, "100000", "1");
            Assert.False(duplicate.IsSuccess);
            Assert.Equal("Channel opening is already in progress for this peer.", duplicate.Message);
        }
        finally
        {
            connected.TrySetResult(ConnectionResult.Ok);
        }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(1, connectionCalls);
        Assert.Equal(1, openingCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedConnectionNeverOpensChannel(bool throws)
    {
        var openingCalls = 0;
        var client = new FakeLightningClient
        {
            ConnectToHandler = (_, _) => throws
                ? throw new Exception("secret-macaroon")
                : Task.FromResult(ConnectionResult.CouldNotConnect),
            OpenChannelHandler = (_, _) =>
            {
                openingCalls++;
                return Task.FromResult(new OpenChannelResponse(OpenChannelResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var result = await TestLightningManagerServiceFactory.Create().OpenChannelAsync(context, NodeUri, "100000", "1");
        Assert.False(result.IsSuccess);
        Assert.Contains("Could not connect to the peer. No channel was opened.", result.Message);
        Assert.DoesNotContain("secret-macaroon", result.Message);
        Assert.Equal(0, openingCalls);
    }

    [Fact]
    public async Task CancellationDuringConnectionNeverOpensChannel()
    {
        using var cancellation = new CancellationTokenSource();
        var openingCalls = 0;
        var client = new FakeLightningClient
        {
            ConnectToHandler = (_, token) =>
            {
                cancellation.Cancel();
                Assert.True(token.IsCancellationRequested);
                return Task.FromResult(ConnectionResult.Ok);
            },
            OpenChannelHandler = (_, _) =>
            {
                openingCalls++;
                return Task.FromResult(new OpenChannelResponse(OpenChannelResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var result = await TestLightningManagerServiceFactory.Create()
            .OpenChannelAsync(context, NodeUri, "100000", "1", cancellation.Token);
        Assert.False(result.IsSuccess);
        Assert.Contains("No channel was opened.", result.Message);
        Assert.Equal(0, openingCalls);
    }

    [Fact]
    public async Task ConnectionIgnoringTimeoutCannotOpenChannelLaterOrBypassGuard()
    {
        var connected = new TaskCompletionSource<ConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionCalls = 0;
        var openingCalls = 0;
        var connectionToken = CancellationToken.None;
        var client = new FakeLightningClient
        {
            ConnectToHandler = (_, token) =>
            {
                connectionCalls++;
                connectionToken = token;
                return connected.Task;
            },
            OpenChannelHandler = (_, _) =>
            {
                openingCalls++;
                return Task.FromResult(new OpenChannelResponse(OpenChannelResult.Ok));
            }
        };
        var context = TestContextFactory.CreateConfigured(LightningCapabilities.Full, client);
        var service = new LightningManagerService(NullLogger<LightningManagerService>.Instance,
            new LightningManagerOperationGuard(), TimeSpan.FromMilliseconds(50));
        try
        {
            var result = await service.OpenChannelAsync(context, NodeUri, "100000", "1")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.IsSuccess);
            Assert.Contains("No channel was opened.", result.Message);
            Assert.True(connectionToken.IsCancellationRequested);
            var duplicate = await service.OpenChannelAsync(context, NodeUri, "100000", "1")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Channel opening is already in progress for this peer.", duplicate.Message);
        }
        finally
        {
            connected.TrySetResult(ConnectionResult.Ok);
        }
        Assert.Equal(1, connectionCalls);
        Assert.Equal(0, openingCalls);
    }
}
