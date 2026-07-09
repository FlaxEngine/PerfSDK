#if USE_PROFILER
using System;
using System.Runtime.InteropServices;
using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// Graphics backend capability matrix and per-API setup hints.
    /// Covers Windows (D3D11, D3D12, Vulkan) and Linux (Vulkan).
    /// All backend-specific guidance is centralized here for Editor and Runtime modes.
    /// </summary>
    internal static class PerfSDKGraphicsBackend
    {
        public enum BackendKind
        {
            Unknown = 0,
            D3D11 = 1,
            D3D12 = 2,
            Vulkan = 3,
        }

        /// <summary>
        /// Maps Flax RendererType to PerfSDK backend kind.
        /// </summary>
        public static BackendKind GetBackendKind(RendererType type)
        {
            switch (type)
            {
            case RendererType.DirectX11: return BackendKind.D3D11;
            case RendererType.DirectX12: return BackendKind.D3D12;
            case RendererType.Vulkan: return BackendKind.Vulkan;
            default: return BackendKind.Unknown;
            }
        }

        /// <summary>
        /// Returns true if the given backend is supported on the current OS.
        /// Windows: D3D11, D3D12, Vulkan. Linux: Vulkan only.
        /// </summary>
        public static bool IsSupportedOnCurrentPlatform(RendererType type)
        {
            BackendKind backend = GetBackendKind(type);
            if (backend == BackendKind.Unknown)
                return false;

            if (PerfSDKPlatform.IsWindows)
                return backend == BackendKind.D3D11 || backend == BackendKind.D3D12 || backend == BackendKind.Vulkan;

            if (PerfSDKPlatform.IsLinux)
                return backend == BackendKind.Vulkan;

            return false;
        }

        /// <summary>
        /// Returns true if the given backend was compiled into the native plugin on this platform.
        /// Mirrors PerfSDK.Build.cs define logic:
        ///   Windows: GRAPHICS_API_DIRECTX11 + GRAPHICS_API_DIRECTX12 + GRAPHICS_API_VULKAN (if SDK found)
        ///   Linux:   GRAPHICS_API_VULKAN only
        /// </summary>
        public static bool IsCompiledForPlatform(BackendKind backend)
        {
            if (PerfSDKPlatform.IsWindows)
                return backend == BackendKind.D3D11 || backend == BackendKind.D3D12 || backend == BackendKind.Vulkan;

            if (PerfSDKPlatform.IsLinux)
                return backend == BackendKind.Vulkan;

            return false;
        }

        /// <summary>
        /// API-specific setup hint appended to error dialogs when profiling fails to initialize.
        /// </summary>
        public static string GetSetupHint(RendererType type)
        {
            BackendKind backend = GetBackendKind(type);

            if (!IsSupportedOnCurrentPlatform(type))
            {
                if (PerfSDKPlatform.IsLinux)
                    return "Linux supports Vulkan only. Switch the project renderer to Vulkan in Graphics Settings.";
                return "Unsupported graphics API. Use DirectX 11, DirectX 12, or Vulkan on Windows.";
            }

            switch (backend)
            {
            case BackendKind.D3D11:
                return GetD3D11SetupHint();
            case BackendKind.D3D12:
                return GetD3D12SetupHint();
            case BackendKind.Vulkan:
                return GetVulkanSetupHint();
            default:
                return string.Empty;
            }
        }

        /// <summary>
        /// Combines a native error string with platform/API setup guidance.
        /// </summary>
        public static string FormatErrorWithHint(string nativeError, RendererType rendererType)
        {
            string hint = GetSetupHint(rendererType);
            if (string.IsNullOrEmpty(nativeError))
                nativeError = "PerfSDK failed to open the region profiler.";

            if (string.IsNullOrEmpty(hint))
                return nativeError;

            return nativeError + "\n\n" + hint;
        }

        static string GetD3D11SetupHint()
        {
            return "DirectX 11 setup (Windows only):\n" +
                   "• Uses immediate-mode Range Profiler (inline during GPU events).\n" +
                   "• Requires an NVIDIA GPU with profiling permissions enabled.\n" +
                   "• No special engine restart needed beyond Recompile Scripts.\n" +
                   "• Do not run with GPU debug layer (-gpudebug) if profiling fails.\n" +
                   "• See https://developer.nvidia.com/ERR_NVGPUCTRPERM if you see permission errors.";
        }

        static string GetD3D12SetupHint()
        {
            return "DirectX 12 setup (Windows only):\n" +
                   "• Uses deferred Range Profiler (queue-level BeginPass/EndPass per frame).\n" +
                   "• Set NVPERF_SDK_PATH before starting the editor or cooked build.\n" +
                   "• Do not run with -gpudebug (PIX/validation layer conflicts with NVPerf).\n" +
                   "• Requires an NVIDIA GPU with profiling permissions enabled.\n" +
                   "• Cook as Debug or Development — Release builds strip all profiler code.\n" +
                   "• See https://developer.nvidia.com/ERR_NVGPUCTRPERM if you see permission errors.";
        }

        static string GetVulkanSetupHint()
        {
            if (PerfSDKPlatform.IsWindows)
            {
                return "Vulkan setup (Windows):\n" +
                       "• Set NVPERF_SDK_PATH before starting the editor or cooked build.\n" +
                       "• Fully restart after changing NVPERF_SDK_PATH (Vulkan device extensions are set at startup).\n" +
                       "• Startup log should show NvPerf Vulkan device extensions enabled.\n" +
                       "• Do not run with -gpudebug (validation layers conflict with NVPerf).\n" +
                       "• Requires VULKAN_SDK for plugin compilation.\n" +
                       "• Cook as Debug or Development — Release builds strip all profiler code.";
            }

            return "Vulkan setup (Linux):\n" +
                   "• Set NVPERF_SDK_PATH before starting the editor or cooked build.\n" +
                   "• Fully restart after changing NVPERF_SDK_PATH.\n" +
                   "• Ensure NvPerf/lib/a64 is on LD_LIBRARY_PATH (plugin prepends it automatically).\n" +
                   "• Do not run with -gpudebug.\n" +
                   "• Requires VULKAN_SDK for plugin compilation.\n" +
                   "• Cook as Debug or Development — Release builds strip all profiler code.\n" +
                   "• Note: engine Vulkan PerfSDK bootstrap may require a Debug/Development engine build.";
        }

        /// <summary>
        /// Human-readable list of supported backends on the current platform.
        /// </summary>
        public static string GetSupportedBackendsDescription()
        {
            if (PerfSDKPlatform.IsWindows)
                return "DirectX 11, DirectX 12, Vulkan";
            if (PerfSDKPlatform.IsLinux)
                return "Vulkan";
            return "none (unsupported platform)";
        }
    }
}
#endif
