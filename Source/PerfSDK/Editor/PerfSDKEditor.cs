#if FLAX_EDITOR && USE_PROFILER

using System;
using FlaxEditor;
using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// Mode 1: In-editor NVIDIA PerfSDK integration plugin.
    /// Hooks into the engine GPU Profiler double-click to open chip counter metrics.
    /// </summary>
    public sealed class PerfSDKEditor : EditorPlugin
    {
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

        public override Type GamePluginType => typeof(PerfSDK);

        public override void InitializeEditor()
        {
            base.InitializeEditor();

            ScriptsBuilder.ScriptsReloadBegin += OnScriptsReloadBegin;
            GpuProfilerBridge.Attach();
            PerfSdkGpuProfilerEditor.Attach();

            if (!RenderPerfTools.IsCompiled)
                Editor.LogWarning("PerfSDK: native module built without NVPERF support. Set NVPERF_SDK_PATH, restart the editor, then Recompile Scripts.");
            else
                Editor.Log(PerfSDKPlatform.GetEditorReadyMessage());
        }

        public override void DeinitializeEditor()
        {
            ScriptsBuilder.ScriptsReloadBegin -= OnScriptsReloadBegin;
            PerfSdkGpuProfilerEditor.Detach();
            GpuProfilerBridge.Detach();
            base.DeinitializeEditor();
        }

        static void OnScriptsReloadBegin()
        {
            PerfSdkGpuProfilerEditor.PrepareForScriptsReload();
            GpuProfilerBridge.PrepareForScriptsReload();
        }
    }
}

#endif
