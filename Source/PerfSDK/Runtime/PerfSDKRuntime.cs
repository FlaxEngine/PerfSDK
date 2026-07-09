#if !FLAX_EDITOR && USE_PROFILER

using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// Mode 2: Runtime initialization for cooked build profiling.
    /// F1 toggles the GPU profiler; double-click a region to open PerfSDK metrics.
    /// </summary>
    internal static class PerfSDKRuntime
    {
        private static bool _initialized;
        private static PerfSDKRuntimeScript _script;

        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;

            _script = new PerfSDKRuntimeScript();
            StandaloneGpuProfiler.Configure();
            Scripting.Update += OnUpdate;

            if (!RenderPerfTools.IsCompiled)
                Debug.LogWarning("PerfSDK Runtime: native module built without NVPERF support. Set NVPERF_SDK_PATH at compile time and recook as Debug/Development.");
            else
                Debug.Log(PerfSDKPlatform.GetRuntimeReadyMessage());
        }

        public static void Deinitialize()
        {
            if (!_initialized)
                return;
            _initialized = false;

            Scripting.Update -= OnUpdate;
            _script = null;
            StandaloneAddMetricWindow.CloseAll();
            StandaloneGpuProfiler.Shutdown();
        }

        static void OnUpdate()
        {
            if (_script == null)
                return;

            StandaloneAddMetricWindow.TickPendingCatalogLoads();

            if (Input.GetKeyDown(KeyboardKeys.F1))
            {
                if (!RenderPerfTools.IsCompiled)
                {
                    Debug.LogWarning("PerfSDK: not compiled. Set NVPERF_SDK_PATH and recook as Debug/Development.");
                    return;
                }
                StandaloneGpuProfiler.Toggle();
            }

            // Keep metrics window live-updating even if GPU profiler is closed (matches editor behavior).
            RuntimeProfilerBridge.TickActiveSession();
        }
    }

    internal class PerfSDKRuntimeScript { }
}

#endif
