#if USE_PROFILER
using System;
using System.Runtime.InteropServices;
using FlaxEngine;
using FlaxEngine.GUI;

namespace NVIDIA
{
    /// <summary>
    /// Cross-platform utilities shared by Editor and Runtime modes.
    /// All OS-specific behavior is isolated here so Linux and Windows builds stay independent.
    /// </summary>
    internal static class PerfSDKPlatform
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        /// Creates a standalone overlay window (Runtime Mode 2). Works on Windows and Linux.
        /// </summary>
        public static Window CreateOverlayWindow(string title, Float2 clientSize, bool topmost = true)
        {
            var desc = CreateWindowSettings.Default;
            desc.Title = title;
            desc.Size = ScaleForDpi(clientSize);
            desc.StartPosition = WindowStartPosition.CenterScreen;
            desc.IsTopmost = topmost;
            desc.ShowInTaskbar = true;
            desc.AllowInput = true;
            return Platform.CreateWindow(ref desc);
        }

        /// <summary>
        /// Applies DPI scaling to a logical client size (matches editor metrics window behavior).
        /// </summary>
        public static Float2 ScaleForDpi(Float2 clientSize)
        {
            var dpi = (float)Platform.Dpi / 96.0f;
            return clientSize * dpi * Platform.CustomDpiScale;
        }

        /// <summary>
        /// Platform-specific message when NVPERF_SDK_PATH was not set at compile time.
        /// </summary>
        public static string GetNotCompiledMessage()
        {
            if (IsWindows)
            {
                return "PerfSDK native module was built without Nsight Perf SDK support.\n\n" +
                       "1. Set user or system env var NVPERF_SDK_PATH to your SDK root.\n" +
                       "   Example: C:\\Downloads\\PerfSDK_2025_5\n" +
                       "2. Fully close and reopen Flax Editor (or rebuild cooked game).\n" +
                       "3. Tools → Recompile Scripts (editor) or recook (runtime).\n\n" +
                       "Windows supports DirectX 11, DirectX 12, and Vulkan.\n" +
                       "Check the Output Log during compile for \"Using PerfSDK v...\".";
            }

            return "PerfSDK native module was built without Nsight Perf SDK support.\n\n" +
                   "1. Set NVPERF_SDK_PATH to your SDK root.\n" +
                   "   Example: /opt/NVIDIA_Nsight_Perf_SDK\n" +
                   "2. Fully close and reopen Flax Editor (or rebuild cooked game).\n" +
                   "3. Tools → Recompile Scripts (editor) or recook (runtime).\n\n" +
                   "Linux supports Vulkan only.\n" +
                   "Ensure NvPerf/lib/a64 is on LD_LIBRARY_PATH if runtime loading fails.\n" +
                   "Check the Output Log during compile for \"Using PerfSDK v...\".";
        }

        /// <summary>
        /// Short startup log line for runtime mode.
        /// </summary>
        public static string GetRuntimeReadyMessage()
        {
            string apis = IsWindows ? "D3D11/D3D12/Vulkan" : "Vulkan";
            return "PerfSDK Runtime: ready — press F1 to toggle GPU profiler (" + apis + ").";
        }

        /// <summary>
        /// Short startup log line for editor mode.
        /// </summary>
        public static string GetEditorReadyMessage()
        {
            string apis = IsWindows ? "D3D11/D3D12/Vulkan" : "Vulkan";
            return "PerfSDK: ready — press F1 for GPU profiler overlay, or double-click a region in the engine Profiler (" + apis + ").";
        }
    }
}
#endif
