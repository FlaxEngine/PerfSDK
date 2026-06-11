using System;
using System.IO;
using Flax.Build;
using Flax.Build.NativeCpp;

/// <summary>
/// NVIDIA PerfSDK module.
/// </summary>
public class PerfSDK : GameModule
{
    /// <summary>
    /// Conditionally imports module into the build depending on the target platform support.
    /// </summary>
    public static bool ConditionalImport(BuildOptions options, System.Collections.Generic.List<string> dependencies)
    {
        bool result = false;
        switch (options.Platform.Target)
        {
        case TargetPlatform.Windows:
            switch (options.Architecture)
            {
            case TargetArchitecture.x64:
                result = true;
                break;
            }
            break;
        }
        if (result)
            dependencies.Add("PerfSDK");
        return result;
    }

    /// <inheritdoc />
    public override void Init()
    {
        base.Init();

        BuildNativeCode = true;
    }

    /// <inheritdoc />
    public override void Setup(BuildOptions options)
    {
        base.Setup(options);

        options.PublicDependencies.Add("Engine");
        options.ScriptingAPI.IgnoreMissingDocumentationWarnings = true;
        if (Profiler.Use(options))
            options.ScriptingAPI.Defines.Add("USE_PROFILER");

        // Check platform and architecture support
        switch (options.Platform.Target)
        {
        case TargetPlatform.Windows:
            switch (options.Architecture)
            {
            case TargetArchitecture.x64:
                break;
            default:
                throw new InvalidArchitectureException(options.Architecture);
            }
            break;
        default:
            throw new InvalidPlatformException(options.Platform.Target);
        }

        // Select GPU APIs to support based on platform
        switch (options.Platform.Target)
        {
        case TargetPlatform.Windows:
            options.PrivateDefinitions.Add("GRAPHICS_API_DIRECTX11");
            options.PrivateDefinitions.Add("GRAPHICS_API_DIRECTX12");
            break;
        }
        if (VulkanSdk.Instance.IsValid && VulkanSdk.Instance.TryGetIncludePath(options.Platform.Target, out string vulkanInclude))
        {
            options.PrivateDefinitions.Add("GRAPHICS_API_VULKAN");
            options.PrivateIncludePaths.Add(vulkanInclude);
        }
        else
            Log.Warning("PerfSDK: Vulkan SDK headers not found. Set VULKAN_SDK if the plugin fails to compile Vulkan code.");

        // Find Perf SDK
        string perfSdk = Environment.GetEnvironmentVariable("NVPERF_SDK_PATH");
        if (!string.IsNullOrEmpty(perfSdk))
            perfSdk = perfSdk.Trim();
        if (string.IsNullOrEmpty(perfSdk))
        {
            Log.Warning("PerfSDK: NVPERF_SDK_PATH is not set. Native PerfSDK support will be disabled. Set it to your Nsight Perf SDK root folder, restart the editor, then Recompile Scripts.");
            return;
        }
        string perfInclude = Path.Combine(perfSdk, "redist", "include");
        if (!Directory.Exists(perfInclude))
        {
            Log.Warning($"PerfSDK: NVPERF_SDK_PATH is '{perfSdk}' but '{perfInclude}' was not found. Point NVPERF_SDK_PATH at the SDK root (the folder that contains redist\\ and NvPerf\\), restart the editor, then Recompile Scripts.");
            return;
        }
        string perfVersion = "";
        string versionFile = Path.Combine(perfSdk, "NvPerf", "version.txt");
        if (File.Exists(versionFile))
        {
            perfVersion = File.ReadAllText(versionFile);
        }
        Log.Info($"Using PerfSDK v{perfVersion} ({perfSdk})");

        // Include PerfSDK
        options.PublicDefinitions.Add("COMPILE_WITH_RENDER_PERF_NVPERF");
        options.PrivateIncludePaths.Add(perfInclude);
        options.PrivateIncludePaths.Add(Path.Combine(perfInclude, "windows-desktop-x64"));
        options.PrivateIncludePaths.Add(Path.Combine(perfSdk, "redist", "NvPerfUtility", "include"));
        options.PrivateIncludePaths.Add(Path.Combine(perfSdk, "Samples", "NvPerfUtility", "imports", "rapidyaml-0.4.0"));
        string perfBin = Path.Combine(perfSdk, "NvPerf", "bin", "x64");
        if (Directory.Exists(perfBin))
        {
            foreach (string dll in Directory.GetFiles(perfBin, "nvperf*.dll"))
                options.DependencyFiles.Add(dll);
        }
    }
}
