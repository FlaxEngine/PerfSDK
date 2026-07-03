using System;
using System.IO;
using Flax.Build;
using Flax.Build.NativeCpp;

/// <summary>
/// NVIDIA PerfSDK module.
/// </summary>
public class PerfSDK : GameModule
{
    private static bool _logOnce;

    static bool IsSupportedPlatform(BuildOptions options)
    {
        switch (options.Platform.Target)
        {
        case TargetPlatform.Windows:
        case TargetPlatform.Linux:
            return options.Architecture == TargetArchitecture.x64;
        default:
            return false;
        }
    }

    /// <summary>
    /// Conditionally imports module into the build depending on the target platform support.
    /// </summary>
    public static bool ConditionalImport(BuildOptions options, System.Collections.Generic.List<string> dependencies = null)
    {
        bool result = IsSupportedPlatform(options);
        result &= Profiler.Use(options);
        if (result && dependencies != null)
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

        if (!IsSupportedPlatform(options))
            throw new InvalidPlatformException(options.Platform.Target);

        if (options.Architecture != TargetArchitecture.x64)
            throw new InvalidArchitectureException(options.Architecture);

        // Select GPU APIs to support based on platform
        switch (options.Platform.Target)
        {
        case TargetPlatform.Windows:
            options.PrivateDefinitions.Add("GRAPHICS_API_DIRECTX11");
            options.PrivateDefinitions.Add("GRAPHICS_API_DIRECTX12");
            break;
        case TargetPlatform.Linux:
            // Vulkan-only on Linux (Nsight Perf SDK has no D3D backends there).
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

        string perfInclude = ResolvePerfIncludeRoot(perfSdk, options.Platform.Target);
        if (string.IsNullOrEmpty(perfInclude))
        {
            if (options.Platform.Target == TargetPlatform.Windows)
                Log.Warning($"PerfSDK: NVPERF_SDK_PATH is '{perfSdk}' but neither 'redist/include' nor 'NvPerf/include' was found. Point NVPERF_SDK_PATH at the SDK root, restart the editor, then Recompile Scripts.");
            else
                Log.Warning($"PerfSDK: NVPERF_SDK_PATH is '{perfSdk}' but 'NvPerf/include' was not found. Point NVPERF_SDK_PATH at the Linux SDK root, restart the editor, then Recompile Scripts.");
            return;
        }

        string platformInclude = Path.Combine(perfInclude, options.Platform.Target == TargetPlatform.Windows ? "windows-desktop-x64" : "linux-desktop-x64");
        if (!Directory.Exists(platformInclude))
        {
            Log.Warning($"PerfSDK: missing platform include folder '{platformInclude}'. Check NVPERF_SDK_PATH and SDK version.");
            return;
        }

        string perfVersion = "";
        string versionFile = Path.Combine(perfSdk, "NvPerf", "version.txt");
        if (File.Exists(versionFile))
            perfVersion = File.ReadAllText(versionFile);
        Log.InfoOnce($"Using PerfSDK v{perfVersion} ({perfSdk})", ref _logOnce);

        // Include PerfSDK
        options.PublicDefinitions.Add("COMPILE_WITH_RENDER_PERF_NVPERF");
        options.PrivateIncludePaths.Add(perfInclude);
        options.PrivateIncludePaths.Add(platformInclude);

        string utilityInclude = Path.Combine(perfSdk, "Samples", "NvPerfUtility", "include");
        if (!Directory.Exists(utilityInclude))
            utilityInclude = Path.Combine(perfSdk, "redist", "NvPerfUtility", "include");
        if (Directory.Exists(utilityInclude))
            options.PrivateIncludePaths.Add(utilityInclude);

        string rapidYaml = Path.Combine(perfSdk, "Samples", "NvPerfUtility", "imports", "rapidyaml-0.4.0");
        if (Directory.Exists(rapidYaml))
            options.PrivateIncludePaths.Add(rapidYaml);

        AddRuntimeDependencies(options, perfSdk, options.Platform.Target);
    }

    static string ResolvePerfIncludeRoot(string perfSdk, TargetPlatform platform)
    {
        if (platform == TargetPlatform.Windows)
        {
            string redistInclude = Path.Combine(perfSdk, "redist", "include");
            if (Directory.Exists(redistInclude))
                return redistInclude;
        }

        string nvPerfInclude = Path.Combine(perfSdk, "NvPerf", "include");
        if (Directory.Exists(nvPerfInclude))
            return nvPerfInclude;

        return null;
    }

    static void AddRuntimeDependencies(BuildOptions options, string perfSdk, TargetPlatform platform)
    {
        if (platform == TargetPlatform.Windows)
        {
            string perfBin = Path.Combine(perfSdk, "NvPerf", "bin", "x64");
            if (!Directory.Exists(perfBin))
                return;
            foreach (string dll in Directory.GetFiles(perfBin, "nvperf*.dll"))
                options.DependencyFiles.Add(dll);
            return;
        }

        string[] libDirs =
        {
            Path.Combine(perfSdk, "NvPerf", "lib", "a64"),
            Path.Combine(perfSdk, "NvPerf", "lib", "x64"),
        };
        foreach (string libDir in libDirs)
        {
            if (!Directory.Exists(libDir))
                continue;
            foreach (string so in Directory.GetFiles(libDir, "libnvperf*.so"))
                options.DependencyFiles.Add(so);
            break;
        }
    }
}
