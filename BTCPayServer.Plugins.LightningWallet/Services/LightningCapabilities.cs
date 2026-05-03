namespace BTCPayServer.Plugins.LightningWallet.Services;

public sealed class LightningCapabilities
{
    public static LightningCapabilities None { get; } = new();
    public static LightningCapabilities Full { get; } = new()
    {
        CanGetInfo = true,
        CanGetBalance = true,
        CanPayBolt11 = true,
        CanConnectPeer = true,
        CanOpenChannel = true,
        CanListChannels = true
    };
    public static LightningCapabilities InfoBalancePay { get; } = PayOnly();
    public static LightningCapabilities Generic { get; } = PayOnly();

    public bool CanGetInfo { get; init; }
    public bool CanGetBalance { get; init; }
    public bool CanPayBolt11 { get; init; }
    public bool CanConnectPeer { get; init; }
    public bool CanOpenChannel { get; init; }
    public bool CanListChannels { get; init; }

    public bool HasPeerManagement => CanConnectPeer;
    public bool HasChannelManagement => CanOpenChannel || CanListChannels;

    public static LightningCapabilities PayOnly(bool canGetInfo = true, bool canGetBalance = true)
    {
        return new LightningCapabilities
        {
            CanGetInfo = canGetInfo,
            CanGetBalance = canGetBalance,
            CanPayBolt11 = true
        };
    }
}
