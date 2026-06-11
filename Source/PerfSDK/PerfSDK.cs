#if FLAX_EDITOR && USE_PROFILER

using System;
using FlaxEditor;
using FlaxEditor.Windows;
using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// In-editor NVIDIA PerfSDK integration plugin.
    /// </summary>
    public sealed class PerfSDKEditor : EditorPlugin
    {
        private PerfSdkMetricsWindow _metricsWindow;

        /// <summary>
        /// Initializes a new instance of the <see cref="PerfSDKEditor"/> class.
        /// </summary>
        public PerfSDKEditor()
        {
            _description = new PluginDescription
            {
                Name = "PerfSDK",
                Category = "Profiling",
                Description = "NVIDIA Nsight PerfSDK metrics in the GPU Profiler (click a region to inspect chip counters).",
                Author = "NVIDIA",
                Version = new Version(1, 0),
            };
        }

        /// <inheritdoc />
        public override Type GamePluginType => typeof(PerfSDK);

        /// <inheritdoc />
        public override void InitializeEditor()
        {
            base.InitializeEditor();

            Editor.InitializationEnd += OnEditorInitializationEnd;
            ScriptsBuilder.ScriptsReloadEnd += OnScriptsReloadEnd;
            AttachProfilerBridge();

            if (!RenderPerfTools.IsCompiled)
            {
                Editor.LogWarning("PerfSDK: native module built without NVPERF support. Set NVPERF_SDK_PATH, restart the editor, then Recompile Scripts.");
            }
        }

        /// <inheritdoc />
        public override void DeinitializeEditor()
        {
            Editor.InitializationEnd -= OnEditorInitializationEnd;
            ScriptsBuilder.ScriptsReloadEnd -= OnScriptsReloadEnd;
            GpuProfilerBridge.Detach();
            _metricsWindow?.OnExit();
            _metricsWindow = null;

            base.DeinitializeEditor();
        }

        private void OnEditorInitializationEnd()
        {
            AttachProfilerBridge();
        }

        private void OnScriptsReloadEnd()
        {
            AttachProfilerBridge();
        }

        private void AttachProfilerBridge()
        {
            if (_metricsWindow == null)
                _metricsWindow = new PerfSdkMetricsWindow(Editor);

            GpuProfilerBridge.Attach(_metricsWindow);
        }
    }
}

#endif
