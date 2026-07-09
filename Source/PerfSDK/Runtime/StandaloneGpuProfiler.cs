#if !FLAX_EDITOR && USE_PROFILER

namespace NVIDIA
{
    /// <summary>
    /// Mode 2: Runtime wrapper around the shared GPU profiler overlay (F1 toggle).
    /// </summary>
    internal static class StandaloneGpuProfiler
    {
        public static bool IsVisible => PerfSdkGpuProfilerOverlay.IsVisible;

        public static void Toggle() => PerfSdkGpuProfilerOverlay.Toggle();

        public static void Show() => PerfSdkGpuProfilerOverlay.Show();

        public static void Hide() => PerfSdkGpuProfilerOverlay.Hide();

        public static void Shutdown() => PerfSdkGpuProfilerOverlay.Shutdown();

        public static void Configure()
        {
            PerfSdkGpuProfilerOverlay.Configure(
                RuntimeProfilerBridge.OpenRegion,
                "press F1 to toggle — double-click a region to inspect chip metrics",
                RuntimeProfilerBridge.ShutdownMetrics);
        }
    }
}
#endif
