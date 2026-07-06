#if FLAX_EDITOR && USE_PROFILER



using FlaxEditor;

using FlaxEditor.GUI;

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



        /// <summary>

        /// Called at ScriptsReloadBegin on the outgoing plugin instance.

        /// </summary>

        public static void PrepareForScriptsReload()

        {

            ProfilerGPU.OpenEventEditor = null;

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

            RenderPerfTools.TickSession();

            if (_metricsWindow == null || _metricsWindow.IsDisposing || _metricsWindow.IsHidden)
                return;

            _metricsWindow.UpdateViewPublic();

        }



        static unsafe void OnOpenEventEditor(ref ProfilerGPU.Event e)

        {

            OpenRegion(new string(e.Name), e.Depth, e.Time, e.Stats.DrawCalls + e.Stats.DispatchCalls, e.Stats.Triangles, e.Stats.Vertices);

        }



        public static void OpenRegion(string regionName, int regionDepth, float gpuTimeMs, long drawCalls, long triangles, long vertices)

        {

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



                var metricsWindow = GetOrCreateMetricsWindow();

                metricsWindow.ShowRegion(regionName);



                ProfilingTools.Enabled = true;



                if (!RenderPerfTools.TryOpenRegionProfiler(regionName, regionDepth, gpuTimeMs, (int)drawCalls, (int)triangles, (int)vertices))

                {

                    string error = RenderPerfTools.ErrorText;

                    MessageBox.Show(

                        string.IsNullOrEmpty(error) ? "PerfSDK failed to open the region profiler." : error,

                        "PerfSDK",

                        MessageBoxButtons.OK,

                        MessageBoxIcon.Error);

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

