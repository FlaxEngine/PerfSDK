# PerfSDK Plugin

NVIDIA Nsight PerfSDK integration for Flax Engine with **dual-mode architecture**:

- **Mode 1 (Editor):** Double-click a GPU profiler region in the editor to open live chip counter metrics
- **Mode 2 (Runtime):** Press **F1** in a cooked Debug/Development build to open a standalone GPU profiler with the same PerfSDK metrics on double-click

## Features

- Live throughput bars and counter values grouped by GPU subsystem (SM, caches, VRAM, raster, etc.)
- **+ Add metric** — browse and add any supported NVPerf counter at runtime
- Custom metrics appear under a **Custom** group
- Dual-mode: editor integration + standalone cooked build profiler
- **Zero impact on Release builds** — completely stripped from shipping configurations

## Requirements

- Windows x64 or Linux x64
- Flax 1.13+
- NVIDIA GPU + Nsight Perf SDK installed
- Environment variable **`NVPERF_SDK_PATH`** pointing at your Perf SDK root

## Setup

1. Set `NVPERF_SDK_PATH` in your user or system environment.
2. On Linux, ensure the NvPerf host library directory is on the loader path.
3. Reference the plugin from your game project:

```json
"References": [
    { "Name": "$(EnginePath)/Flax.flaxproj" },
    { "Name": "$(EnginePath)/../PerfSDK/PerfSDK.flaxproj" }
]
```

4. Open the project and **Build & Run** (or **Tools → Recompile Scripts**). Check the Output Log for `Using PerfSDK v...`.
5. See usage below for editor or cooked build profiling.

## Mode 1: Editor Profiling

1. Play the scene, open **Profiler → GPU**, enable live recording.
2. **Double-click** a GPU region to open the **Nvidia PerfSDK Metrics** window.
3. Click **+ Add metric** to browse and add custom counters.

## Mode 2: Cooked Build Profiling

1. Cook your project as **Debug** or **Development** (never Release).
2. Launch the cooked executable.
3. Press **F1** to toggle the standalone GPU profiler.
4. **Double-click** any region in the timeline or table to open PerfSDK metrics.
5. Same metrics window and **+ Add metric** functionality as editor mode.

## Supported Platforms

| Platform | Graphics APIs | Editor | Cooked Build |
|----------|---------------|--------|--------------|
| Windows x64 | D3D11, D3D12, Vulkan | Mode 1 | Mode 2 |
| Linux x64 | Vulkan | Mode 1 | Mode 2 |

See [WINDOWS_SETUP.md](../docs/WINDOWS_SETUP.md) for Windows-specific setup.

## Source Code Layout

```
Source/PerfSDK/
├── Shared/          ← NVPerf core + shared metrics UI (both modes)
│   ├── Native/      ← RenderPerfTools, PerfSdkHud
│   └── Managed/     ← PerfSdkChipMetricsTable, formatting helpers
├── Editor/          ← Mode 1: editor GPU profiler integration
├── Runtime/         ← Mode 2: standalone cooked build profiler (F1)
├── PerfSDK.cpp/h    ← Native GamePlugin entry
└── PerfSDK.Build.cs ← Build rules + Release gate
```

See `/FlaxTesting/docs/` for full architecture documentation.

## Release Build Guarantee

PerfSDK is **completely absent** from Release builds:

- `PerfSDK.ConditionalImport()` returns false for Release non-editor
- `PerfSDKTarget.CanBuild()` gates on ConditionalImport
- All code wrapped in `#if USE_PROFILER` / `#if COMPILE_WITH_RENDER_PERF_NVPERF`
- No DLL, no NVPerf libraries, no profiling hooks in Release

## API (C# / C++ game code)

```csharp
// Optional — only if calling RenderPerfTools from your game scripts
PerfSDK.ConditionalImport(options, options.PrivateDependencies);
```

Not required for editor metrics UI or runtime F1 profiler.

## Vulkan Note

Set `NVPERF_SDK_PATH` **before starting the editor or cooked build** so Vulkan device creation enables NVPerf extensions. Fully restart after changing the env var.
