#if !FLAX_EDITOR && USE_PROFILER

using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// Mode 2: C# GamePlugin companion that initializes runtime profiling on cooked builds.
    /// Works alongside the native PerfSDK GamePlugin.
    /// </summary>
    public sealed class PerfSDKGame : GamePlugin
    {
        public PerfSDKGame()
        {
            _description = new PluginDescription
            {
                Name = "PerfSDK Runtime",
                Category = "Profiling",
                Description = "NVIDIA PerfSDK GPU profiler for cooked Debug/Development builds (F1 to toggle).",
                Author = "NVIDIA",
                Version = new System.Version(1, 0),
            };
        }

        public override void Initialize()
        {
            base.Initialize();
            PerfSDKRuntime.Initialize();
        }

        public override void Deinitialize()
        {
            PerfSDKRuntime.Deinitialize();
            base.Deinitialize();
        }
    }
}

#endif
