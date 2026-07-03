#if FLAX_EDITOR && USE_PROFILER

using FlaxEditor;
using FlaxEditor.GUI;
using FlaxEditor.GUI.Docking;
using FlaxEditor.Windows;
using FlaxEditor.Windows.Profiler;
using FlaxEngine;
using FlaxEngine.GUI;

namespace NVIDIA
{
    /// <summary>
    /// Connects engine GPU profiler click callbacks to the PerfSDK metrics window.
    /// </summary>
    internal static class GpuProfilerBridge
    {
        private static PerfSdkMetricsWindow _metricsWindow;
        private static bool _updateHookRegistered;

        public static void Attach(PerfSdkMetricsWindow metricsWindow)
        {
            _metricsWindow = metricsWindow;
            ProfilerGPU.OpenEventEditor = OnOpenEventEditor;
            GpuProfilerClickHook.Enable();
            EnsureUpdateHook();
            Editor.Log("PerfSDK: GPU profiler click handler attached.");
        }

        public static void Detach()
        {
            ProfilerGPU.OpenEventEditor = null;
            GpuProfilerClickHook.Disable();
            _metricsWindow = null;

            if (_updateHookRegistered && Editor.Instance != null)
            {
                Editor.Instance.EditorUpdate -= OnUpdate;
                _updateHookRegistered = false;
            }
        }

        static void EnsureUpdateHook()
        {
            if (_updateHookRegistered || Editor.Instance == null)
                return;

            Editor.Instance.EditorUpdate += OnUpdate;
            _updateHookRegistered = true;
        }

        static void OnUpdate()
        {
            if (_metricsWindow == null || ProfilerGPU.OpenEventEditor != null)
                return;
            ProfilerGPU.OpenEventEditor = OnOpenEventEditor;
        }

        static unsafe void OnOpenEventEditor(ref ProfilerGPU.Event e)
        {
            OpenRegion(new string(e.Name), e.Depth, e.Time, e.Stats.DrawCalls + e.Stats.DispatchCalls, e.Stats.Triangles, e.Stats.Vertices);
        }

        public static void OpenRegion(string regionName, int regionDepth, float gpuTimeMs, long drawCalls, long triangles, long vertices)
        {
            if (_metricsWindow == null)
            {
                Editor.LogWarning("PerfSDK: GPU profiler click ignored because the metrics window is not initialized.");
                return;
            }

            try
            {
                if (!RenderPerfTools.IsCompiled)
                {
                    MessageBox.Show(
                        "PerfSDK native module was built without Nsight Perf SDK support.\n\n" +
                        "1. Set user or system env var NVPERF_SDK_PATH to your SDK root (folder containing NvPerf/).\n" +
                        "   Windows example: C:\\Downloads\\PerfSDK_2025_5\n" +
                        "   Linux example: /opt/NVIDIA_Nsight_Perf_SDK\n" +
                        "2. Fully close and reopen Flax Editor (required so the build tool sees the variable).\n" +
                        "3. Tools → Recompile Scripts (or Build & Run).\n\n" +
                        "On Linux, also ensure NvPerf/lib/a64 is on LD_LIBRARY_PATH if runtime loading fails.\n" +
                        "Check the Output Log during compile for \"Using PerfSDK v...\".",
                        "PerfSDK",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                _metricsWindow.ShowRegion(regionName);

                // Ensure GPU profiler events record correct hierarchy depth for region matching.
                ProfilingTools.Enabled = true;

                if (!RenderPerfTools.TryOpenRegionProfiler(regionName, regionDepth, gpuTimeMs, (int)drawCalls, (int)triangles, (int)vertices))
                {
                    string error = RenderPerfTools.ErrorText;
                    MessageBox.Show(
                        string.IsNullOrEmpty(error) ? "PerfSDK failed to open the region profiler." : error,
                        "PerfSDK",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    _metricsWindow.UpdateViewPublic();
                    return;
                }

                _metricsWindow.UpdateViewPublic();
            }
            catch (System.Exception ex)
            {
                Editor.LogError("PerfSDK: GPU profiler click failed: " + ex.Message);
                MessageBox.Show(ex.Message, "PerfSDK", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

#endif
