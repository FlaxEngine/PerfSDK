using Flax.Build;
using Flax.Build.NativeCpp;

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

    /// <inheritdoc />
    public override bool CanBuild(BuildOptions options)
    {
        return base.CanBuild(options) && PerfSDK.ConditionalImport(options);
    }
}
