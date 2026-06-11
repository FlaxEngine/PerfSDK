using Flax.Build;

public class PerfSDKTarget : GameProjectTarget
{
    /// <inheritdoc />
    public override void Init()
    {
        base.Init();

        Platforms = new[]
        {
            TargetPlatform.Windows,
        };
        Architectures = new[]
        {
            TargetArchitecture.x64,
        };
        Modules.Add("PerfSDK");
    }
}
