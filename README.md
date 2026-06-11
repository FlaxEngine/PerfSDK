![PerfSDK in Flax Engine](screenshot.png)

# PerfSDK Plugin

NVIDIA Nsight PerfSDK integration for the Flax Editor GPU Profiler. Click a GPU profiler timeline bar or table row to open live chip counter metrics.

## Requirements

- Windows x64
- Flax 1.13+
- NVIDIA GPU + Nsight Perf SDK installed
- Environment variable **`NVPERF_SDK_PATH`** pointing at your Perf SDK root (e.g. `C:\Downloads\PerfSDK_2025_5`)

## Setup

1. Set `NVPERF_SDK_PATH` in your user/system environment.
2. Clone plugin via Editor [Plugin Window tool](https://docs.flaxengine.com/manual/scripting/plugins/plugin-project.html#automated-git-cloning), ensure game project references it:

```
...
"References": [
    {
        "Name": "$(EnginePath)/Flax.flaxproj"
    },
    {
        "Name": "$(ProjectPath)/Plugins/PerfSDK/PerfSDK.flaxproj"
    }
]
```

2. Open the project and **Build & Run** (or compile scripts) so the PerfSDK plugin native module links against NVPerf.
3. Play the scene, open **Profiler → GPU**, enable live recording, then **left-click** a region.

## Supported Platforms

Platforms:
* Windows x64

Graphics APIs:
* DirectX 11
* DirectX 12
* Vulkan

## API

In order to access PErfSDK API in C++/C# scripting import it in your code module (modify `Game.Build.cs` file):

```cs
public override void Setup(BuildOptions options)
{
    base.Setup(options);

    // Adds PerfSDF module to PrivateDependencies if supported on current platform (eg. Windows x64)
    PerfSDF.ConditionalImport(options, options.PrivateDependencies);
}
```

## Architecture

| Layer | Location |
|-------|----------|
| Native NVPerf + HUD | `Plugins/PerfSDK/Source/PerfSDK/*.cpp` (plugin DLL) |
| Metrics editor window | `Plugins/PerfSDK/Source/PerfSDK/PerfSdkMetricsWindow.cs` |
| Editor plugin wiring | `Plugins/PerfSDK/Source/PerfSDK/PerfSDK.cs`, `GpuProfilerBridge.cs` |
| GPU frame sampling hooks | `PerfSDK.cpp` via `RenderTask` delegates (no game C++) |
| Profiler click plumbing | Engine `GpuProfilerRegionCallbacks` + `Timeline` / `GPU.cs` |

**gearsDLSS game code has zero PerfSDK C++.** All native code lives in the plugin.

The engine does **not** define `COMPILE_WITH_RENDER_PERF` or link NVPerf. Only the plugin does when `NVPERF_SDK_PATH` is set at build time.

## Vulkan note

PerfSDK needs extra Vulkan device extensions (`VK_EXT_buffer_device_address`, `VK_KHR_device_group`, plus NVPerf profiler extensions). The engine enables these at **device creation** when `NVPERF_SDK_PATH` is set **before starting the editor**.

1. Set `NVPERF_SDK_PATH` (and `VULKAN_SDK` for plugin builds).
2. **Fully close and reopen Flax Editor** so Vulkan device creation picks up the extensions.
3. Rebuild the engine (after engine changes) and Recompile Scripts.
