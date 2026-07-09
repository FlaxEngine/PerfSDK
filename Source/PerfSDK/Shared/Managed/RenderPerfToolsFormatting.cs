#if USE_PROFILER
using FlaxEngine;

namespace NVIDIA
{
    /// <summary>
    /// Shared formatting helpers used by both editor and runtime PerfSDK UI.
    /// </summary>
    internal static class RenderPerfToolsFormatting
    {
        public static string FormatRendererType(RendererType type)
        {
            switch (type)
            {
            case RendererType.DirectX10: return "DirectX 10";
            case RendererType.DirectX10_1: return "DirectX 10.1";
            case RendererType.DirectX11: return "DirectX 11";
            case RendererType.DirectX12: return "DirectX 12";
            case RendererType.Vulkan: return "Vulkan";
            case RendererType.OpenGL4_1: return "OpenGL 4.1";
            case RendererType.OpenGL4_4: return "OpenGL 4.4";
            case RendererType.OpenGLES3: return "OpenGL ES 3";
            case RendererType.OpenGLES3_1: return "OpenGL ES 3.1";
            case RendererType.Null: return "Null";
            case RendererType.WebGPU: return "WebGPU";
            default: return type.ToString();
            }
        }

        public static unsafe void TryGetLiveRegionStats(string regionName, ref float gpuTimeMs, ref long drawCalls, ref long triangles, ref long vertices)
        {
            if (string.IsNullOrEmpty(regionName) || PerfSdkGpuProfilerFilter.IsExcludedRegionName(regionName))
                return;

            var events = ProfilingTools.EventsGPU;
            if (events == null || events.Length == 0)
                return;

            for (int i = 0; i < events.Length; i++)
            {
                ref var e = ref events[i];
                if (e.Name == null)
                    continue;

                if (!string.Equals(regionName, new string(e.Name), System.StringComparison.Ordinal))
                    continue;

                gpuTimeMs = e.Time;
                drawCalls = e.Stats.DrawCalls + e.Stats.DispatchCalls;
                triangles = e.Stats.Triangles;
                vertices = e.Stats.Vertices;
                return;
            }
        }
    }
}
#endif
