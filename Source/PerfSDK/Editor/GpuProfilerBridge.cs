#if FLAX_EDITOR && USE_PROFILER

using FlaxEditor;
using FlaxEditor.GUI;
using FlaxEditor.Windows;
using FlaxEditor.Windows.Profiler;
using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// Mode 1: Connects engine GPU profiler click callbacks to the PerfSDK metrics window.
    /// </summary>
    internal static class GpuProfilerBridge
    {
        private static PerfSdkMetricsWindow _metricsWindow;
        private static bool _editorUpdateRegistered;

        public static void Attach()
        {
            Detach();
            ProfilerGPU.OpenEventEditor = OnOpenEventEditor;
        }

        public static void Detach()
        {
            ProfilerGPU.OpenEventEditor = null;
            UnregisterEditorUpdateHook();
            DisposeMetricsWindow();
        }

        public static void PrepareForScriptsReload()
        {
            ProfilerGPU.OpenEventEditor = null;
            PerfSdkAddMetricWindow.CloseAll();
            UnregisterEditorUpdateHook();
            DisposeAllMetricsWindows();
        }

        static void EnsureEditorUpdateHook()
        {
            if (_editorUpdateRegistered || Editor.Instance == null)
                return;
            Editor.Instance.EditorUpdate += OnEditorUpdate;
            _editorUpdateRegistered = true;
        }

        static void UnregisterEditorUpdateHook()
        {
            if (!_editorUpdateRegistered || Editor.Instance == null)
            {
                _editorUpdateRegistered = false;
                return;
            }
            Editor.Instance.EditorUpdate -= OnEditorUpdate;
            _editorUpdateRegistered = false;
        }

        static void OnEditorUpdate()
        {
            if (!RenderPerfTools.IsSessionActive)
            {
                UnregisterEditorUpdateHook();
                return;
            }

            PerfSdkAddMetricWindow.TickPendingCatalogLoads();

            if (RenderPerfTools.IsSessionActive)
                RenderPerfTools.TickSession();

            if (_metricsWindow == null || _metricsWindow.IsDisposing || _metricsWindow.IsHidden)
                return;

            _metricsWindow.UpdateViewPublic();
        }

        static unsafe void OnOpenEventEditor(ref ProfilerGPU.Event e)
        {
            string name = new string(e.Name);
            if (PerfSdkGpuProfilerFilter.IsExcludedRegionName(name))
                return;

            OpenRegion(name, e.Depth, e.Time, e.Stats.DrawCalls + e.Stats.DispatchCalls, e.Stats.Triangles, e.Stats.Vertices);
        }

        public static void OpenRegion(string regionName, int regionDepth, float gpuTimeMs, long drawCalls, long triangles, long vertices)
        {
            try
            {
                if (PerfSdkGpuProfilerFilter.IsExcludedRegionName(regionName))
                    return;

                if (!RenderPerfTools.IsCompiled)
                {
                    MessageBox.Show(PerfSDKPlatform.GetNotCompiledMessage(), "PerfSDK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var rendererType = GPUDevice.Instance != null ? GPUDevice.Instance.RendererType : RendererType.Unknown;
                if (!PerfSDKGraphicsBackend.IsSupportedOnCurrentPlatform(rendererType))
                {
                    MessageBox.Show(PerfSDKGraphicsBackend.GetSetupHint(rendererType), "PerfSDK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var metricsWindow = GetOrCreateMetricsWindow();
                metricsWindow.ShowRegion(regionName);
                ProfilingTools.Enabled = true;

                if (!RenderPerfTools.TryOpenRegionProfiler(regionName, regionDepth, gpuTimeMs, (int)drawCalls, (int)triangles, (int)vertices))
                {
                    string error = PerfSDKGraphicsBackend.FormatErrorWithHint(RenderPerfTools.ErrorText, rendererType);
                    MessageBox.Show(error, "PerfSDK", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    metricsWindow.UpdateViewPublic();
                    return;
                }

                metricsWindow.UpdateViewPublic();
                EnsureEditorUpdateHook();
            }
            catch (System.Exception ex)
            {
                Editor.LogError("PerfSDK: GPU profiler click failed: " + ex.Message);
                MessageBox.Show(ex.Message, "PerfSDK", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static PerfSdkMetricsWindow GetOrCreateMetricsWindow()
        {
            if (_metricsWindow == null || _metricsWindow.IsDisposing)
                _metricsWindow = new PerfSdkMetricsWindow(Editor.Instance);
            return _metricsWindow;
        }

        static void DisposeMetricsWindow()
        {
            PerfSdkAddMetricWindow.CloseAll();

            if (_metricsWindow == null)
                return;

            if (RenderPerfTools.IsSessionActive)
                _metricsWindow.OnExit();

            _metricsWindow.Dispose();
            _metricsWindow = null;
            UnregisterEditorUpdateHook();
        }

        static void DisposeAllMetricsWindows()
        {
            PerfSdkAddMetricWindow.CloseAll();
            DisposeMetricsWindow();

            var windowsModule = Editor.Instance?.Windows;
            if (windowsModule == null)
                return;

            var windows = windowsModule.Windows;
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                if (windows[i] is not PerfSdkMetricsWindow metricsWindow)
                    continue;

                if (RenderPerfTools.IsSessionActive)
                    metricsWindow.OnExit();

                metricsWindow.Dispose();
            }
        }
    }
}

#endif
