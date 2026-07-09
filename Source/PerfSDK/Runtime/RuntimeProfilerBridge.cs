#if !FLAX_EDITOR && USE_PROFILER

using FlaxEngine;
using FlaxEngine.GUI;

namespace NVIDIA
{
    /// <summary>
    /// Mode 2: Connects standalone GPU profiler clicks to PerfSDK metrics window.
    /// </summary>
    internal static class RuntimeProfilerBridge
    {
        private static StandaloneMetricsWindow _metricsWindow;

        public static void OpenRegion(string regionName, int regionDepth, float gpuTimeMs, long drawCalls, long triangles, long vertices)
        {
            if (PerfSdkGpuProfilerFilter.IsExcludedRegionName(regionName))
                return;

            if (!RenderPerfTools.IsCompiled)
            {
                Debug.LogWarning("PerfSDK: native module not compiled with NVPERF support.");
                return;
            }

            var rendererType = GPUDevice.Instance != null ? GPUDevice.Instance.RendererType : RendererType.Unknown;
            if (!PerfSDKGraphicsBackend.IsSupportedOnCurrentPlatform(rendererType))
            {
                Debug.LogWarning(PerfSDKGraphicsBackend.GetSetupHint(rendererType));
                return;
            }

            ProfilingTools.Enabled = true;

            var metricsWindow = GetOrCreateMetricsWindow();
            if (metricsWindow == null)
                return;

            metricsWindow.ShowRegion(regionName);

            if (!RenderPerfTools.TryOpenRegionProfiler(regionName, regionDepth, gpuTimeMs, (int)drawCalls, (int)triangles, (int)vertices))
            {
                string error = PerfSDKGraphicsBackend.FormatErrorWithHint(RenderPerfTools.ErrorText, rendererType);
                Debug.LogError(error);
                metricsWindow.UpdateViewPublic();
                return;
            }

            metricsWindow.UpdateViewPublic();
        }

        public static void TickActiveSession()
        {
            if (!RenderPerfTools.IsSessionActive)
                return;

            RenderPerfTools.TickSession();

            if (_metricsWindow != null && !_metricsWindow.IsDisposing && _metricsWindow.IsVisible)
                _metricsWindow.UpdateViewPublic();
        }

        /// <summary>
        /// Tears down metrics UI and active NVPerf session during runtime shutdown.
        /// Closing the GPU profiler window does not call this.
        /// </summary>
        public static void ShutdownMetrics()
        {
            StandaloneAddMetricWindow.CloseAll();
            if (_metricsWindow != null)
            {
                if (RenderPerfTools.IsSessionActive)
                    RenderPerfTools.CloseRegionProfiler();
                _metricsWindow.Hide();
                _metricsWindow = null;
            }
        }

        public static void NotifyMetricsWindowClosed(StandaloneMetricsWindow window)
        {
            if (_metricsWindow == window)
                _metricsWindow = null;
        }

        static StandaloneMetricsWindow GetOrCreateMetricsWindow()
        {
            if (_metricsWindow == null || !_metricsWindow.IsAvailable)
                _metricsWindow = new StandaloneMetricsWindow();

            if (_metricsWindow == null || !_metricsWindow.IsAvailable)
            {
                Debug.LogError("PerfSDK: failed to create metrics window.");
                _metricsWindow = null;
                return null;
            }

            return _metricsWindow;
        }
    }
}

#endif
