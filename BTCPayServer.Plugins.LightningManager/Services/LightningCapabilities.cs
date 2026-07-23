namespace BTCPayServer.Plugins.LightningManager.Services;

public sealed class LightningCapabilities
{
    public static LightningCapabilities None { get; } = new();
    public static LightningCapabilities Full { get; } = new()
    {
        CanGetInfo = true,
        CanGetBalance = true,
        CanPayBolt11 = true,
        CanPayAmountless = true,
        CanSetMaxFee = true,
        CanConnectPeer = true,
        CanOpenChannel = true,
        CanListChannels = true
    };
    public static LightningCapabilities CoreLightning { get; } = Full;
    public static LightningCapabilities Eclair { get; } = Full;
    public static LightningCapabilities Phoenixd { get; } = PayOnly(canPayAmountless: true);
    public static LightningCapabilities BlinkBitcoin { get; } = PayOnly(canGetInfo: false);
    public static LightningCapabilities BlinkPayOnly { get; } = PayOnly(canGetInfo: false, canGetBalance: false);
    public static LightningCapabilities InfoBalancePay { get; } = Phoenixd;

    private bool _canPayAmountless;

    public bool CanGetInfo { get; init; }
    public bool CanGetBalance { get; init; }
    public bool CanPayBolt11 { get; init; }
    public bool CanPayAmountless
    {
        get => CanPayBolt11 && _canPayAmountless;
        init => _canPayAmountless = value;
    }
    public bool CanSetMaxFee { get; init; }
    public bool CanConnectPeer { get; init; }
    public bool CanOpenChannel { get; init; }
    public bool CanListChannels { get; init; }
    public bool HasAny =>
        CanGetInfo ||
        CanGetBalance ||
        CanPayBolt11 ||
        CanConnectPeer ||
        CanOpenChannel ||
        CanListChannels;

    public static LightningCapabilities PayOnly(
        bool canGetInfo = true,
        bool canGetBalance = true,
        bool canPayAmountless = false)
    {
        return new LightningCapabilities
        {
            CanGetInfo = canGetInfo,
            CanGetBalance = canGetBalance,
            CanPayBolt11 = true,
            CanPayAmountless = canPayAmountless
        };
    }
}
