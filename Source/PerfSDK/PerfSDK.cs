#if FLAX_EDITOR && USE_PROFILER



using System;

using FlaxEditor;

using FlaxEngine;



namespace NVIDIA

{

    /// <summary>

    /// In-editor NVIDIA PerfSDK integration plugin.

    /// </summary>

    public sealed class PerfSDKEditor : EditorPlugin

    {

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



            ScriptsBuilder.ScriptsReloadBegin += OnScriptsReloadBegin;

            GpuProfilerBridge.Attach();



            if (!RenderPerfTools.IsCompiled)

            {

                Editor.LogWarning("PerfSDK: native module built without NVPERF support. Set NVPERF_SDK_PATH, restart the editor, then Recompile Scripts.");

            }

            else

            {

                Editor.Log("PerfSDK: ready — double-click a GPU profiler region to inspect metrics.");

            }

        }



        /// <inheritdoc />

        public override void DeinitializeEditor()

        {

            ScriptsBuilder.ScriptsReloadBegin -= OnScriptsReloadBegin;

            GpuProfilerBridge.Detach();



            base.DeinitializeEditor();

        }



        static void OnScriptsReloadBegin()

        {

            // Must run on the outgoing plugin instance before its assembly is unloaded.

            GpuProfilerBridge.PrepareForScriptsReload();

        }

    }

}



#endif

