#if FLAX_EDITOR && USE_PROFILER

using FlaxEditor;
using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// Mode 1: Stable PerfSDK GPU profiler overlay in the editor (F1 toggle).
    /// Avoids engine Profiler GPU tab flicker while keeping the same metrics workflow.
    /// </summary>
    internal static class PerfSdkGpuProfilerEditor
    {
        private static bool _attached;

        public static void Attach()
        {
            if (_attached)
                return;
            _attached = true;

            PerfSdkGpuProfilerOverlay.Configure(
                GpuProfilerBridge.OpenRegion,
                "press F1 to toggle — double-click a region to inspect chip metrics");

            if (Editor.Instance != null)
                Editor.Instance.EditorUpdate += OnEditorUpdate;
        }

        public static void Detach()
        {
            if (!_attached)
                return;
            _attached = false;

            if (Editor.Instance != null)
                Editor.Instance.EditorUpdate -= OnEditorUpdate;

            PerfSdkGpuProfilerOverlay.Hide();
        }

        public static void PrepareForScriptsReload()
        {
            PerfSdkGpuProfilerOverlay.Hide();
        }

        static void OnEditorUpdate()
        {
            if (!RenderPerfTools.IsCompiled)
                return;

            if (Input.GetKeyDown(KeyboardKeys.F1))
                PerfSdkGpuProfilerOverlay.Toggle();

            if (PerfSdkGpuProfilerOverlay.IsVisible)
                RenderPerfTools.TickSession();
        }
    }
}
#endif
