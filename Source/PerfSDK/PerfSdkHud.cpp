// Copyright (c) Flax Engine. Nsight Perf SDK HUD helper for RenderPerfSDK.
//
// All backends (D3D11, D3D12, Vulkan) use the NVIDIA Perf SDK Range Profiler to collect
// *true per-region* GPU counters: the clicked GPU profiler scope is bracketed with
// PushRange/PopRange in the command stream, and the metric set is gathered over the few
// passes the configuration needs (one pass per frame the region executes).
//
// D3D11 is immediate-mode, so BeginPass/PushRange/PopRange/EndPass happen inline during
// the region event. D3D12/Vulkan are deferred: queue-level BeginPass/EndPass bracket the
// whole frame (driven by GPUDevice::DrawBegin/DrawEnd before/after command-list submission),
// while command-list/command-buffer PushRange/PopRange mark the region during recording.
// BeginSession for D3D12/Vulkan runs at DrawBegin (OnFrameBegin), before any queue submit —
// the same point as NvPerf ReportGeneratorVulkan::OnFrameStart.

#include "PerfSdkHud.h"
#include "Engine/Core/Log.h"
#include "Engine/Core/Types/StringBuilder.h"
#include "Engine/Engine/Engine.h"
#include "Engine/Platform/FileSystem.h"
#include "Engine/Platform/Platform.h"
#include "Engine/Platform/StringUtils.h"
#include "Engine/Utilities/StringConverter.h"

#if COMPILE_WITH_RENDER_PERF_NVPERF

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef NOGDI
#define NOGDI
#endif

#include <cstdarg>
#include <cstdio>
#include <algorithm>
#include <cmath>
#include <memory>
#include <string>
#include <vector>

#if GRAPHICS_API_DIRECTX11 || GRAPHICS_API_DIRECTX12
#include <dxgi.h>
#if GRAPHICS_API_DIRECTX11
#include <d3d11.h>
#endif
#if GRAPHICS_API_DIRECTX12
#include <d3d12.h>
#endif
#if PLATFORM_WIN32
#include <wrl/client.h>
#endif
#endif

#if PLATFORM_WIN32
// D3D/DXGI headers include Windows.h and define GetEnvironmentVariable/GetCommandLine
// macros that break Flax Platform::GetEnvironmentVariable and Engine::GetCommandLine.
#undef GetEnvironmentVariable
#undef SetEnvironmentVariable
#undef GetCommandLine
#endif

#include <ThirdParty/volk/volk.h>

#define RYML_SINGLE_HDR_DEFINE_NOW
#include <ryml_all.hpp>
#ifndef _RYML_SINGLE_HEADER_AMALGAMATED_HPP_
#define _RYML_SINGLE_HEADER_AMALGAMATED_HPP_
#endif
#undef RYML_SINGLE_HDR_DEFINE_NOW

#include <NvPerfHudDataModel.h>
#include <NvPerfHudTextRenderer.h>

#include <NvPerfVulkan.h>
#include <NvPerfMetricConfigurationsHAL.h>
#include <NvPerfPeriodicSamplerGpu.h>
#include <NvPerfPeriodicSamplerVulkan.h>
#include <NvPerfCounterData.h>
#include <NvPerfScopeExitGuard.h>

#include <NvPerfMetricsEvaluator.h>
#include <NvPerfMetricsConfigBuilder.h>
#include <NvPerfCounterConfiguration.h>
#include <NvPerfRangeProfilerVulkan.h>
#if GRAPHICS_API_DIRECTX11
#include <NvPerfD3D11.h>
#include <NvPerfRangeProfilerD3D11.h>
#endif
#if GRAPHICS_API_DIRECTX12
#include <NvPerfD3D12.h>
#include <NvPerfPeriodicSamplerD3D12.h>
#include <NvPerfRangeProfilerD3D12.h>
#endif

#include "nvperf_host_impl.h"

#ifdef Rectangle
#undef Rectangle
#endif

#include "Engine/Graphics/GPUAdapter.h"
#include "Engine/Graphics/GPUContext.h"
#include "Engine/Graphics/GPUDevice.h"
#include "Engine/Graphics/Enums.h"

#if GRAPHICS_API_VULKAN
PFN_vkGetInstanceProcAddr vkGetInstanceProcAddr = nullptr;
PFN_vkGetDeviceProcAddr vkGetDeviceProcAddr = nullptr;
PFN_vkDeviceWaitIdle vkDeviceWaitIdle = nullptr;
PFN_vkQueueWaitIdle vkQueueWaitIdle = nullptr;
#endif

namespace
{
#if GRAPHICS_API_DIRECTX11 || GRAPHICS_API_DIRECTX12
    using Microsoft::WRL::ComPtr;
#endif

    static String RendererTypeToDisplayName(RendererType type)
    {
        switch (type)
        {
        case RendererType::DirectX11: return TEXT("DirectX 11");
        case RendererType::DirectX12: return TEXT("DirectX 12");
        case RendererType::Vulkan: return TEXT("Vulkan");
        case RendererType::DirectX10: return TEXT("DirectX 10");
        case RendererType::DirectX10_1: return TEXT("DirectX 10.1");
        case RendererType::OpenGL4_1: return TEXT("OpenGL 4.1");
        case RendererType::OpenGL4_4: return TEXT("OpenGL 4.4");
        case RendererType::OpenGLES3: return TEXT("OpenGL ES 3");
        case RendererType::OpenGLES3_1: return TEXT("OpenGL ES 3.1");
        case RendererType::Null: return TEXT("Null");
        case RendererType::WebGPU: return TEXT("WebGPU");
        default: return TEXT("Unknown");
        }
    }

    enum class PerfSdkBackend
    {
        D3D11,
        D3D12,
        Vulkan,
    };

    enum class ChipMetricFormat
    {
        Percent,
        Warps,
        Pixels,
        Bandwidth,
        Time,
        Counter,
    };

    static std::string ToUtf8Ascii(const String& s)
    {
        std::string result;
        result.reserve((size_t)s.Length());
        for (int32 i = 0; i < s.Length(); i++)
        {
            const Char c = s[i];
            result.push_back(c < 128 ? static_cast<char>(c) : '?');
        }
        return result;
    }

    // Per-region metric (D3D11 Range Profiler). Each maps an NvPerf metric name to a display row.
    struct RangeAreaDefinition
    {
        const Char* GroupName;
        const Char* AreaName;
        const char* MetricName;
        const Char* MetricLabel;
        ChipMetricFormat Format;
        float BarScale;
        bool HigherIsBetter;
    };

    // Owned copy used for built-in and user-added metrics in the active configuration.
    struct RangeMetricDef
    {
        String GroupName;
        String AreaName;
        std::string MetricName;
        String MetricLabel;
        ChipMetricFormat Format;
        float BarScale;
        bool HigherIsBetter;
        bool IsUserAdded = false;
    };

    struct ActiveRangeMetric
    {
        RangeMetricDef Def;
        NVPW_MetricEvalRequest Request;
    };

    static RangeMetricDef ToOwnedDef(const RangeAreaDefinition& src)
    {
        RangeMetricDef def;
        def.GroupName = src.GroupName;
        def.AreaName = src.AreaName;
        def.MetricName = src.MetricName;
        def.MetricLabel = src.MetricLabel;
        def.Format = src.Format;
        def.BarScale = src.BarScale;
        def.HigherIsBetter = src.HigherIsBetter;
        def.IsUserAdded = false;
        return def;
    }

    static String MakeDisplayNameFromMetricName(const char* metricName)
    {
        if (!metricName || !metricName[0])
            return TEXT("Metric");

        String result(metricName);
        result.Replace(TEXT("__"), TEXT(" "));
        result.Replace(TEXT("_"), TEXT(" "));
        result.Replace(TEXT("."), TEXT(" "));
        while (result.Contains(TEXT("  ")))
            result.Replace(TEXT("  "), TEXT(" "));
        return result;
    }

    static ChipMetricFormat InferMetricFormat(const std::string& metricName)
    {
        if (metricName.find("time") != std::string::npos || metricName.find("duration") != std::string::npos)
            return ChipMetricFormat::Time;
        if (metricName.find("bandwidth") != std::string::npos || metricName.find("byte") != std::string::npos)
            return ChipMetricFormat::Bandwidth;
        if (metricName.find("warp") != std::string::npos && metricName.find(".pct") == std::string::npos)
            return ChipMetricFormat::Warps;
        return ChipMetricFormat::Percent;
    }

    static ChipMetricFormat ResolveUserMetricFormat(NVPW_MetricsEvaluator* evaluator, const std::string& metricName)
    {
        if (evaluator)
        {
            NVPW_MetricType metricType;
            size_t metricIndex = 0;
            if (nv::perf::GetMetricTypeAndIndex(evaluator, metricName.c_str(), metricType, metricIndex))
            {
                if (metricType == NVPW_METRIC_TYPE_COUNTER)
                    return ChipMetricFormat::Counter;
                if (metricType == NVPW_METRIC_TYPE_RATIO || metricType == NVPW_METRIC_TYPE_THROUGHPUT)
                    return ChipMetricFormat::Percent;
            }
        }
        return InferMetricFormat(metricName);
    }

    static bool InferHigherIsBetter(const std::string& metricName)
    {
        return metricName.find("hit_rate") != std::string::npos ||
               metricName.find("_rate.pct") != std::string::npos;
    }

    static RangeMetricDef MakeUserMetricDef(NVPW_MetricsEvaluator* evaluator, const std::string& metricName)
    {
        RangeMetricDef def;
        def.GroupName = TEXT("Custom");
        def.MetricName = metricName;
        def.AreaName = MakeDisplayNameFromMetricName(metricName.c_str());
        def.MetricLabel = def.AreaName;
        def.Format = ResolveUserMetricFormat(evaluator, metricName);
        def.BarScale = def.Format == ChipMetricFormat::Time ? 5.0f : 100.0f;
        def.HigherIsBetter = InferHigherIsBetter(metricName);
        def.IsUserAdded = true;
        return def;
    }

    static const RangeAreaDefinition gRangeAreaDefinitions[] =
    {
        // Overall GPU engine occupancy / SM pressure for the region.
        { TEXT("GPU Engines"), TEXT("GPU Active"), "gr__cycles_active.avg.pct_of_peak_sustained_elapsed", TEXT("GPU Active"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("GPU Engines"), TEXT("SM Throughput"), "sm__throughput.avg.pct_of_peak_sustained_elapsed", TEXT("SM Throughput"), ChipMetricFormat::Percent, 100.0f, false },

        // Graphics vs compute split by shader-domain warp occupancy (separate bars).
        // VTG = vertex/tess/geometry (graphics front-end), PS = pixel (graphics back-end), CS = compute.
        { TEXT("Workload"), TEXT("Vertex/Geo Warps"), "tpc__warps_active_shader_vtg.avg.pct_of_peak_sustained_elapsed", TEXT("Graphics (Vtx/Geo)"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Workload"), TEXT("Pixel Warps"), "tpc__warps_active_shader_ps.avg.pct_of_peak_sustained_elapsed", TEXT("Graphics (Pixel)"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Workload"), TEXT("Compute Warps"), "tpc__warps_active_shader_cs.avg.pct_of_peak_sustained_elapsed", TEXT("Compute"), ChipMetricFormat::Percent, 100.0f, false },

        // Shader core detail. Occupancy tells you how well latency is hidden; pipe rows show the bound unit.
        { TEXT("Shaders"), TEXT("SM Active"), "sm__cycles_active.avg.pct_of_peak_sustained_elapsed", TEXT("SM Active"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Shaders"), TEXT("Occupancy"), "sm__warps_active.avg.pct_of_peak_sustained_elapsed", TEXT("Occupancy"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Shaders"), TEXT("Issue Active"), "sm__issue_active.avg.pct_of_peak_sustained_elapsed", TEXT("Issue Active"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Shaders"), TEXT("FMA Pipe"), "sm__pipe_fma_cycles_active.avg.pct_of_peak_sustained_elapsed", TEXT("FMA Active"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Shaders"), TEXT("ALU Pipe"), "sm__pipe_alu_cycles_active.avg.pct_of_peak_sustained_elapsed", TEXT("ALU Active"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Shaders"), TEXT("Tensor Pipe"), "sm__pipe_tensor_cycles_active.avg.pct_of_peak_sustained_elapsed", TEXT("Tensor Active"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Shaders"), TEXT("LSU Pipe"), "sm__inst_executed_pipe_lsu.avg.pct_of_peak_sustained_elapsed", TEXT("LSU (Mem) Active"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Shaders"), TEXT("Tex Pipe"), "sm__inst_executed_pipe_tex.avg.pct_of_peak_sustained_elapsed", TEXT("Tex Active"), ChipMetricFormat::Percent, 100.0f, false },

        // Fixed-function graphics (only active during raster passes; idle for pure compute regions).
        { TEXT("Raster (ROP)"), TEXT("Rasterizer"), "raster__throughput.avg.pct_of_peak_sustained_elapsed", TEXT("Rasterizer"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Raster (ROP)"), TEXT("Color/Blend"), "crop__throughput.avg.pct_of_peak_sustained_elapsed", TEXT("Color ROP"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Raster (ROP)"), TEXT("Depth"), "zrop__throughput.avg.pct_of_peak_sustained_elapsed", TEXT("Depth ROP"), ChipMetricFormat::Percent, 100.0f, false },

        { TEXT("VRAM"), TEXT("DRAM Throughput"), "dram__throughput.avg.pct_of_peak_sustained_elapsed", TEXT("VRAM Throughput"), ChipMetricFormat::Percent, 100.0f, false },

        // Cache pressure + hit rates. Low hit rate + high DRAM throughput = memory bound.
        { TEXT("Caches"), TEXT("L2 Throughput"), "lts__throughput.avg.pct_of_peak_sustained_elapsed", TEXT("L2 Throughput"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Caches"), TEXT("L2 Hit Rate"), "lts__t_sector_hit_rate.pct", TEXT("L2 Hit Rate"), ChipMetricFormat::Percent, 100.0f, true },
        { TEXT("Caches"), TEXT("L1/TEX Throughput"), "l1tex__throughput.avg.pct_of_peak_sustained_elapsed", TEXT("L1/TEX Throughput"), ChipMetricFormat::Percent, 100.0f, false },
        { TEXT("Caches"), TEXT("L1 Hit Rate"), "l1tex__t_sector_hit_rate.pct", TEXT("L1 Hit Rate"), ChipMetricFormat::Percent, 100.0f, true },

        { TEXT("Timing"), TEXT("Region GPU Time"), "gpu__time_duration.sum", TEXT("GPU Time"), ChipMetricFormat::Time, 5.0f, false },
    };

    static const Char* gChipAreaGroupOrder[] =
    {
        TEXT("Custom"),
        TEXT("GPU Engines"),
        TEXT("Workload"),
        TEXT("Shaders"),
        TEXT("Raster (ROP)"),
        TEXT("VRAM"),
        TEXT("Caches"),
        TEXT("Timing"),
    };

    struct PerfSdkHudImpl
    {
        PerfSdkBackend backend = PerfSdkBackend::D3D11;

        std::string chipName;

        // ---- Range Profiler backends (true per-region; one active per backend) ----
        // D3D11 is immediate-mode: BeginPass/PushRange/PopRange/EndPass happen inline during the region event.
        // D3D12/Vulkan are deferred: queue-level BeginPass/EndPass bracket the frame (DrawBegin/DrawEnd),
        // while command-list/command-buffer PushRange/PopRange mark the clicked region during recording.
#if GRAPHICS_API_DIRECTX11
        ComPtr<ID3D11Device> d3d11Device;
        ComPtr<ID3D11DeviceContext> d3d11Context;
        nv::perf::profiler::RangeProfilerD3D11 rangeProfilerD3D11;
#endif
#if GRAPHICS_API_DIRECTX12
        ComPtr<ID3D12CommandQueue> d3d12Queue;
        nv::perf::profiler::RangeProfilerD3D12 rangeProfilerD3D12;
#endif
#if GRAPHICS_API_VULKAN
        nv::perf::profiler::RangeProfilerVulkan rangeProfilerVulkan;
        VkInstance vkInstance = VK_NULL_HANDLE;
        VkPhysicalDevice vkPhysicalDevice = VK_NULL_HANDLE;
        VkDevice vkDevice = VK_NULL_HANDLE;
        VkQueue vkQueue = VK_NULL_HANDLE;
        uint32_t vkQueueFamilyIndex = 0;
        nv::perf::ClockInfo vkSavedClockInfo = {};
        bool vkClockSaved = false;
#endif

        // ---- Shared metrics evaluation (backend-agnostic once created) ----
        std::unique_ptr<nv::perf::MetricsEvaluator> metricsEvaluator;
        NVPW_MetricsEvaluator* metricsEvaluatorRaw = nullptr;
        nv::perf::CounterConfiguration rangeCounterConfig;
        std::vector<ActiveRangeMetric> activeMetrics;
        std::vector<std::string> userMetricNames;
        std::vector<double> rangeMetricValues;

        bool rangeSessionActive = false;
        bool rangeDataValid = false;
        uint32_t rangeMeasurementCount = 0;
        uint32_t rangePassesSubmitted = 0;
        size_t rangePassesPerSample = 1;

        // ---- Region selection ----
        String targetRegionName;
        std::string targetRegionNameUtf8;
        int32 targetRegionDepth = -1;
        int32 activeRegionEventIndex = -1;
        bool regionRangePushed = false;   // a command-list range is currently open (deferred backends)
        void* regionCmdPtr = nullptr;     // the exact command list/buffer the region range was pushed into (deferred backends)
        bool regionProfilingActive = false;
        uint32_t regionCompletionCount = 0;
        uint32_t gpuFramesProcessed = 0;

        // Deferred backends own the GPU queue from the render thread. BeginSession/EndSession must NOT run on
        // the UI click thread. D3D12/Vulkan both start at OnFrameBegin (before any vkQueueSubmit), matching
        // NvPerf ReportGeneratorVulkan::OnFrameStart. BeginSession after vkQueuePresentKHR fails with
        // NVPA_STATUS_INVALID_CONTEXT_STATE because the present is still in flight. D3D11 is immediate-mode.
        bool pendingSessionBegin = false;   // render thread should (re)start the range session
        bool pendingSessionEnd = false;     // render thread should end the range session
        uint32_t sessionBeginAttempts = 0;  // BeginRangeSession tries while deferred session is pending
        String lastSessionError;            // last NvPerf BeginSession failure (e.g. ERR_NVGPUCTRPERM)

        // ---- Diagnostics (deferred backend stall investigation) ----
        uint32_t dbgPassBeginCount = 0;     // queue-level BeginPass calls that succeeded
        uint32_t dbgPassEndCount = 0;       // queue-level EndPass calls that succeeded
        uint32_t dbgRangePushCount = 0;     // command-list PushRange calls that succeeded
        uint32_t dbgDecodeCallCount = 0;    // DecodeCounters calls (poll)
        bool dbgLoggedFirstPush = false;
    };

    static bool MetricNameAlreadyActive(const PerfSdkHudImpl& impl, const char* metricName)
    {
        for (const ActiveRangeMetric& metric : impl.activeMetrics)
        {
            if (metric.Def.MetricName == metricName)
                return true;
        }
        return false;
    }

    // Captured from the most recent NvPerf ERR log during BeginSession (cleared at each attempt).
    static String gPendingNvPerfSessionError;

    static void CaptureNvPerfSessionError(const String& message)
    {
        if (message.Contains(TEXT("profiling permissions"), StringSearchCase::IgnoreCase) ||
            message.Contains(TEXT("ERR_NVGPUCTRPERM"), StringSearchCase::IgnoreCase))
        {
            gPendingNvPerfSessionError =
                TEXT("ERR_NVGPUCTRPERM: GPU profiling permissions not enabled — see https://developer.nvidia.com/ERR_NVGPUCTRPERM");
        }
    }

    static void NoteBeginRangeSessionFailed(PerfSdkHudImpl& impl)
    {
        impl.sessionBeginAttempts++;
        if (!gPendingNvPerfSessionError.IsEmpty())
            impl.lastSessionError = gPendingNvPerfSessionError;
        else if (impl.lastSessionError.IsEmpty())
            impl.lastSessionError = TEXT("BeginRangeSession failed — see Output Log for [NvPerf] errors.");
    }

    // Forwards NvPerf's internal log messages (errors like "profiling permissions not enabled",
    // "resource conflict", "insufficient driver version", etc.) into the Flax log so we can see
    // the real reason BeginSession / BeginPass / Decode fails.
    static void NvPerfLogToFlax(const char* pPrefix, const char* /*pDate*/, const char* /*pTime*/, const char* pFunctionName, const char* pMessage, void* /*pData*/)
    {
        const String prefix = pPrefix ? String(pPrefix) : String::Empty;
        const String func = pFunctionName ? String(pFunctionName) : String::Empty;
        String message = pMessage ? String(pMessage) : String::Empty;
        message.Replace(TEXT("\n"), TEXT(" "));
        message.Replace(TEXT("\r"), TEXT(" "));
        const bool isError = prefix.Contains(TEXT("ERR")) || prefix.Contains(TEXT("err"));
        if (isError)
        {
            CaptureNvPerfSessionError(message);
            LOG(Warning, "[NvPerf:{0}] {1} ({2})", prefix, message, func);
        }
        else
            LOG(Info, "[NvPerf:{0}] {1}", prefix, message);
    }

    static const Char* BackendName(PerfSdkBackend backend)
    {
        switch (backend)
        {
        case PerfSdkBackend::D3D11:  return TEXT("D3D11");
        case PerfSdkBackend::D3D12:  return TEXT("D3D12");
        case PerfSdkBackend::Vulkan: return TEXT("Vulkan");
        }
        return TEXT("?");
    }

    static void LogVulkanEngineSetupHint()
    {
        String envPath;
        const bool envInProcess = !Platform::GetEnvironmentVariable(TEXT("NVPERF_SDK_PATH"), envPath) && !envPath.IsEmpty();
        LOG(Warning, "[PerfSDK] Vulkan Range Profiler requires a FULL FlaxEditor restart after engine/plugin changes. Startup log must include 'disabled all Vulkan layers', 'enabled N Vulkan device extension(s)', and 'Vulkan device configured for profiling (N NvPerf device extension(s))'. NVPERF_SDK_PATH in this process={0}.", envInProcess ? 1 : 0);
    }

    static bool TrySetNvPerfLibraryPath()
    {
        String envPath;
        if (Platform::GetEnvironmentVariable(TEXT("NVPERF_SDK_PATH"), envPath) || envPath.IsEmpty())
            return true;

#if PLATFORM_WIN32
        const String libDir = envPath / TEXT("NvPerf") / TEXT("bin") / TEXT("x64");
        if (!FileSystem::DirectoryExists(libDir))
            return true;

        const wchar_t* paths[] = { *libDir };
        NVPW_SetLibraryLoadPathsW_Params params = { NVPW_SetLibraryLoadPathsW_Params_STRUCT_SIZE };
        params.numPaths = 1;
        params.ppwPaths = paths;
        NVPW_SetLibraryLoadPathsW(&params);
#else
        static const Char* libSubdirs[] =
        {
            TEXT("NvPerf/lib/a64"),
            TEXT("NvPerf/lib/x64"),
        };
        for (const Char* subdir : libSubdirs)
        {
            const String libDir = envPath / subdir;
            if (!FileSystem::DirectoryExists(libDir))
                continue;

            String ldPath;
            Platform::GetEnvironmentVariable(TEXT("LD_LIBRARY_PATH"), ldPath);
            if (ldPath.IsEmpty())
                ldPath = libDir;
            else if (!ldPath.Contains(libDir))
                ldPath = libDir + TEXT(":") + ldPath;
            Platform::SetEnvironmentVariable(TEXT("LD_LIBRARY_PATH"), ldPath);
            break;
        }
#endif
        return true;
    }

    static bool MatchesTargetRegion(PerfSdkHudImpl& impl, const Char* name, int32 depth)
    {
        if (!impl.regionProfilingActive || impl.targetRegionName.IsEmpty() || !name)
            return false;
        if (StringUtils::CompareIgnoreCase(impl.targetRegionName.Get(), name) != 0)
            return false;
        // The profiler UI depth is from a captured frame and often differs from live GPU events
        // (especially right after enabling ProfilingTools). Match by region name only.
        (void)depth;
        return true;
    }

    static float NormalizeMetricPercent(double value)
    {
        if (std::isnan(value) || std::isinf(value))
            return 0.0f;
        return static_cast<float>(Math::Clamp(value, 0.0, 100.0));
    }

    // ===================== Display formatting (shared) =====================

    static float ComputeBarValue(ChipMetricFormat format, float barScale, double rawValue)
    {
        if (std::isnan(rawValue) || std::isinf(rawValue))
            return 0.0f;

        switch (format)
        {
        case ChipMetricFormat::Percent:
            return NormalizeMetricPercent(rawValue);
        case ChipMetricFormat::Time:
        {
            const double ms = rawValue / 1.0e6;
            if (barScale <= 0.0f)
                return 0.0f;
            return NormalizeMetricPercent(ms / barScale * 100.0);
        }
        case ChipMetricFormat::Counter:
            return 0.0f;
        default:
            if (barScale <= 0.0f)
                return 0.0f;
            return NormalizeMetricPercent(rawValue / barScale * 100.0);
        }
    }

    static String FormatMetricDetail(const Char* label, ChipMetricFormat format, double rawValue)
    {
        switch (format)
        {
        case ChipMetricFormat::Percent:
            return String::Format(TEXT("{0}: {1:.1f}%"), label, (float)rawValue);
        case ChipMetricFormat::Warps:
            return String::Format(TEXT("{0}: {1:.1f} warps"), label, (float)rawValue);
        case ChipMetricFormat::Pixels:
            return String::Format(TEXT("{0}: {1:.0f}"), label, (float)rawValue);
        case ChipMetricFormat::Bandwidth:
            return String::Format(TEXT("{0}: {1:.2f} GB/s"), label, (float)rawValue);
        case ChipMetricFormat::Time:
            return String::Format(TEXT("{0}: {1:.4f} ms"), label, (float)(rawValue / 1.0e6));
        case ChipMetricFormat::Counter:
        {
            if (std::isnan(rawValue) || std::isinf(rawValue))
                return String::Format(TEXT("{0}: n/a"), label);
            const double absValue = std::fabs(rawValue);
            if (absValue >= 1000.0 || std::floor(rawValue + 0.5) == rawValue)
                return String::Format(TEXT("{0}: {1}"), label, (int64)rawValue);
            return String::Format(TEXT("{0}: {1:.4f}"), label, (float)rawValue);
        }
        default:
            return String::Format(TEXT("{0}: {1:.1f}"), label, (float)rawValue);
        }
    }

    // ===================== Range Profiler (per-region, all backends) =====================

    // Only one backend's range profiler is active per session; these dispatch to it.
    static bool RangeIsInPass(PerfSdkHudImpl& impl)
    {
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  return impl.rangeProfilerD3D11.IsInPass();
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  return impl.rangeProfilerD3D12.IsInPass();
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: return impl.rangeProfilerVulkan.IsInPass();
#endif
        }
        return false;
    }

    // True once every pass the current counter configuration needs has been submitted.
    // The Range Profiler state machine expects no further BeginPass/EndPass until the
    // collected counters are decoded; starting extra passes prevents the decode from
    // ever reporting allStatisticalSamplesCollected (i.e. "forever collecting").
    static bool RangeAllPassesSubmitted(PerfSdkHudImpl& impl)
    {
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  return impl.rangeProfilerD3D11.AllPassesSubmitted();
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  return impl.rangeProfilerD3D12.AllPassesSubmitted();
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: return impl.rangeProfilerVulkan.AllPassesSubmitted();
#endif
        }
        return false;
    }

    static bool RangeBeginPass(PerfSdkHudImpl& impl)
    {
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  return impl.rangeProfilerD3D11.BeginPass();
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  return impl.rangeProfilerD3D12.BeginPass();
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: return impl.rangeProfilerVulkan.BeginPass();
#endif
        }
        return false;
    }

    static bool RangeEndPass(PerfSdkHudImpl& impl)
    {
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  return impl.rangeProfilerD3D11.EndPass();
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  return impl.rangeProfilerD3D12.EndPass();
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: return impl.rangeProfilerVulkan.EndPass();
#endif
        }
        return false;
    }

    static bool RangeEnqueue(PerfSdkHudImpl& impl)
    {
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  return impl.rangeProfilerD3D11.EnqueueCounterCollection(impl.rangeCounterConfig, 1, 1);
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  return impl.rangeProfilerD3D12.EnqueueCounterCollection(impl.rangeCounterConfig, 1, 1);
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: return impl.rangeProfilerVulkan.EnqueueCounterCollection(impl.rangeCounterConfig, 1, 1);
#endif
        }
        return false;
    }

    static bool RangeDecode(PerfSdkHudImpl& impl, nv::perf::profiler::DecodeResult& result)
    {
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  return impl.rangeProfilerD3D11.DecodeCounters(result);
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  return impl.rangeProfilerD3D12.DecodeCounters(result);
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: return impl.rangeProfilerVulkan.DecodeCounters(result);
#endif
        }
        return false;
    }

    // Builds the metrics evaluator + counter configuration (shared logic; backend only changes the factory calls).
    static bool BuildRangeCounterConfiguration(PerfSdkHudImpl& impl, NVPW_MetricsEvaluator* pMetricsEvaluator, String& statusText)
    {
        NVPW_RawCounterConfig* pRawCounterConfig = nullptr;
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  pRawCounterConfig = nv::perf::profiler::D3D11CreateRawCounterConfig(impl.chipName.c_str()); break;
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  pRawCounterConfig = nv::perf::profiler::D3D12CreateRawCounterConfig(impl.chipName.c_str()); break;
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: pRawCounterConfig = nv::perf::profiler::VulkanCreateRawCounterConfig(impl.chipName.c_str()); break;
#endif
        }
        if (!pRawCounterConfig)
        {
            statusText = TEXT("Nsight Perf: CreateRawCounterConfig failed.");
            return false;
        }

        nv::perf::MetricsConfigBuilder configBuilder;
        if (!configBuilder.Initialize(pMetricsEvaluator, pRawCounterConfig, impl.chipName.c_str()))
        {
            statusText = TEXT("Nsight Perf: MetricsConfigBuilder init failed.");
            return false;
        }

        impl.activeMetrics.clear();

        // User-added metrics first so they keep counter slots when the budget is tight.
        for (const std::string& userMetricName : impl.userMetricNames)
        {
            if (MetricNameAlreadyActive(impl, userMetricName.c_str()))
                continue;
            NVPW_MetricEvalRequest request{};
            if (!nv::perf::ToMetricEvalRequest(pMetricsEvaluator, userMetricName.c_str(), request))
                continue;
            if (!configBuilder.AddMetric(userMetricName.c_str()))
                continue;
            ActiveRangeMetric metric;
            metric.Def = MakeUserMetricDef(pMetricsEvaluator, userMetricName);
            metric.Request = request;
            impl.activeMetrics.push_back(std::move(metric));
        }

        for (int32_t i = 0; i < (int32_t)(sizeof(gRangeAreaDefinitions) / sizeof(gRangeAreaDefinitions[0])); i++)
        {
            NVPW_MetricEvalRequest request{};
            if (!nv::perf::ToMetricEvalRequest(pMetricsEvaluator, gRangeAreaDefinitions[i].MetricName, request))
                continue;
            if (!configBuilder.AddMetric(gRangeAreaDefinitions[i].MetricName))
                continue;
            ActiveRangeMetric metric;
            metric.Def = ToOwnedDef(gRangeAreaDefinitions[i]);
            metric.Request = request;
            impl.activeMetrics.push_back(std::move(metric));
        }

        if (impl.activeMetrics.empty())
        {
            statusText = TEXT("Nsight Perf: no supported range metrics on this GPU.");
            return false;
        }

        if (!nv::perf::CreateConfiguration(configBuilder, impl.rangeCounterConfig))
        {
            statusText = TEXT("Nsight Perf: range counter configuration failed.");
            return false;
        }

        impl.rangePassesPerSample = impl.rangeCounterConfig.numPasses > 0 ? impl.rangeCounterConfig.numPasses : 1;
        impl.rangeMetricValues.assign(impl.activeMetrics.size(), 0.0);
        return true;
    }

    static void EndRangeSession(PerfSdkHudImpl& impl);
    static bool BeginRangeSession(PerfSdkHudImpl& impl);

    static bool RebuildRangeCounterConfiguration(PerfSdkHudImpl& impl, String& statusText)
    {
        if (!impl.metricsEvaluatorRaw)
        {
            statusText = TEXT("Nsight Perf: metrics evaluator is not ready.");
            return false;
        }

        const bool wasSessionActive = impl.rangeSessionActive;
        EndRangeSession(impl);
        impl.rangeDataValid = false;
        impl.rangeMeasurementCount = 0;
        for (double& v : impl.rangeMetricValues)
            v = 0.0;

        if (!BuildRangeCounterConfiguration(impl, impl.metricsEvaluatorRaw, statusText))
            return false;

        if (wasSessionActive && impl.regionProfilingActive)
        {
#if GRAPHICS_API_DIRECTX11
            if (impl.backend == PerfSdkBackend::D3D11)
            {
                if (!BeginRangeSession(impl))
                {
                    NoteBeginRangeSessionFailed(impl);
                    return false;
                }
            }
            else
#endif
            {
                impl.pendingSessionBegin = true;
            }
        }
        return true;
    }

    static bool InitRangeMetrics(PerfSdkHudImpl& impl, String& statusText)
    {
        const char* chip = impl.chipName.c_str();

        size_t scratchBufferSize = 0;
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  scratchBufferSize = nv::perf::D3D11CalculateMetricsEvaluatorScratchBufferSize(chip); break;
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  scratchBufferSize = nv::perf::D3D12CalculateMetricsEvaluatorScratchBufferSize(chip); break;
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: scratchBufferSize = nv::perf::VulkanCalculateMetricsEvaluatorScratchBufferSize(chip); break;
#endif
        }
        if (scratchBufferSize == 0)
        {
            statusText = TEXT("Nsight Perf: metrics evaluator scratch size failed.");
            return false;
        }

        std::vector<uint8_t> scratchBuffer(scratchBufferSize);
        NVPW_MetricsEvaluator* pMetricsEvaluator = nullptr;
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:  pMetricsEvaluator = nv::perf::D3D11CreateMetricsEvaluator(scratchBuffer.data(), scratchBuffer.size(), chip); break;
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:  pMetricsEvaluator = nv::perf::D3D12CreateMetricsEvaluator(scratchBuffer.data(), scratchBuffer.size(), chip); break;
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan: pMetricsEvaluator = nv::perf::VulkanCreateMetricsEvaluator(scratchBuffer.data(), scratchBuffer.size(), chip); break;
#endif
        }
        if (!pMetricsEvaluator)
        {
            statusText = TEXT("Nsight Perf: CreateMetricsEvaluator failed.");
            return false;
        }
        impl.metricsEvaluator = std::make_unique<nv::perf::MetricsEvaluator>(pMetricsEvaluator, std::move(scratchBuffer));
        impl.metricsEvaluatorRaw = pMetricsEvaluator;

        return BuildRangeCounterConfiguration(impl, pMetricsEvaluator, statusText);
    }

#if GRAPHICS_API_DIRECTX11
    static bool InitRangeProfilerD3D11(PerfSdkHudImpl& impl, ID3D11Device* device, ID3D11DeviceContext* context, String& statusText)
    {
        if (!device || !context || context->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE)
        {
            statusText = TEXT("Nsight Perf: D3D11 immediate context required.");
            return false;
        }
        if (!nv::perf::D3D11LoadDriver())
        {
            statusText = TEXT("Nsight Perf: D3D11LoadDriver failed.");
            return false;
        }
        if (!nv::perf::D3D11IsNvidiaDevice(device) || !nv::perf::profiler::D3D11IsGpuSupported(device))
        {
            statusText = TEXT("Nsight Perf: D3D11 NVIDIA GPU not supported for profiling.");
            return false;
        }
        const nv::perf::DeviceIdentifiers deviceIdentifiers = nv::perf::D3D11GetDeviceIdentifiers(device);
        if (!deviceIdentifiers.pChipName)
        {
            statusText = TEXT("Nsight Perf: unknown GPU chip.");
            return false;
        }
        impl.chipName = deviceIdentifiers.pChipName;
        impl.d3d11Device = device;
        impl.d3d11Context = context;
        return InitRangeMetrics(impl, statusText);
    }
#endif
#if GRAPHICS_API_DIRECTX12
    static bool InitRangeProfilerD3D12(PerfSdkHudImpl& impl, GPUDevice* gpuDevice, String& statusText)
    {
        if (!gpuDevice)
        {
            statusText = TEXT("Nsight Perf: missing D3D12 device.");
            return false;
        }
        if (Engine::GetCommandLine().Contains(TEXT("-gpudebug")))
        {
            statusText = TEXT("Nsight Perf: cannot use Vulkan device with Debug Layer enabled. Run without '-gpudebug' command line.");
            return false;
        }
        auto* device = static_cast<ID3D12Device*>(gpuDevice->GetNativePtr());
        if (!device)
        {
            statusText = TEXT("Nsight Perf: missing D3D12 native device.");
            return false;
        }
        if (!nv::perf::D3D12LoadDriver())
        {
            statusText = TEXT("Nsight Perf: D3D12LoadDriver failed.");
            return false;
        }
        if (!nv::perf::D3D12IsNvidiaDevice(device) || !nv::perf::profiler::D3D12IsGpuSupported(device))
        {
            statusText = TEXT("Nsight Perf: D3D12 NVIDIA GPU not supported.");
            return false;
        }
        auto* queue = static_cast<ID3D12CommandQueue*>(gpuDevice->GetNativeQueue().MainQueue);
        if (!queue)
        {
            statusText = TEXT("Nsight Perf: D3D12 graphics queue not found.");
            return false;
        }
        const nv::perf::DeviceIdentifiers deviceIdentifiers = nv::perf::D3D12GetDeviceIdentifiers(device);
        if (!deviceIdentifiers.pChipName)
        {
            statusText = TEXT("Nsight Perf: unknown GPU chip.");
            return false;
        }
        impl.chipName = deviceIdentifiers.pChipName;
        impl.d3d12Queue = queue;
        return InitRangeMetrics(impl, statusText);
    }
#endif
#if GRAPHICS_API_VULKAN
    static bool InitRangeProfilerVulkan(PerfSdkHudImpl& impl, GPUDevice* gpuDevice, GPUAdapter* adapter, String& statusText)
    {
        if (!gpuDevice || !adapter)
        {
            statusText = TEXT("Nsight Perf: missing Vulkan device or adapter.");
            return false;
        }
        if (Engine::GetCommandLine().Contains(TEXT("-gpudebug")))
        {
            statusText = TEXT("Nsight Perf: cannot use Vulkan device with Debug Layer enabled. Run without '-gpudebug' command line.");
            return false;
        }
        void** handles = static_cast<void**>(gpuDevice->GetNativePtr());
        VkInstance instance = handles ? static_cast<VkInstance>(handles[0]) : VK_NULL_HANDLE;
        VkDevice device = handles ? static_cast<VkDevice>(handles[1]) : VK_NULL_HANDLE;
        VkPhysicalDevice physicalDevice = static_cast<VkPhysicalDevice>(adapter->GetNativePtr());
        if (!instance || !physicalDevice || !device)
        {
            statusText = TEXT("Nsight Perf: missing Vulkan handles.");
            return false;
        }
        vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)handles[2]; // GPUDeviceVulkan::GetNativePtr
        vkGetDeviceProcAddr = (PFN_vkGetDeviceProcAddr)handles[3]; // GPUDeviceVulkan::GetNativePtr
        vkDeviceWaitIdle = (PFN_vkDeviceWaitIdle)vkGetDeviceProcAddr(device, "vkDeviceWaitIdle");
        vkQueueWaitIdle = (PFN_vkQueueWaitIdle)vkGetDeviceProcAddr(device, "vkQueueWaitIdle");
        if (!nv::perf::VulkanLoadDriver(instance))
        {
            statusText = TEXT("Nsight Perf: VulkanLoadDriver failed.");
            return false;
        }
        if (!adapter->IsNVIDIA())
        {
            statusText = TEXT("Nsight Perf: Vulkan NVIDIA GPU not supported.");
            return false;
        }
        auto queueInfo = gpuDevice->GetNativeQueue();
        VkQueue queue = static_cast<VkQueue>(queueInfo.MainQueue);
        const uint32_t queueFamilyIndex = queueInfo.MainQueueFamilyIndex;
        if (!queue)
        {
            statusText = TEXT("Nsight Perf: Vulkan graphics queue not found.");
            return false;
        }
        impl.vkInstance = instance;
        impl.vkPhysicalDevice = physicalDevice;
        impl.vkDevice = device;
        impl.vkQueue = queue;
        impl.vkQueueFamilyIndex = queueFamilyIndex;
        const nv::perf::DeviceIdentifiers deviceIdentifiers = nv::perf::VulkanGetDeviceIdentifiers(instance, physicalDevice, device, vkGetInstanceProcAddr, vkGetDeviceProcAddr);
        if (!deviceIdentifiers.pChipName)
        {
            statusText = TEXT("Nsight Perf: unknown GPU chip.");
            return false;
        }
        impl.chipName = deviceIdentifiers.pChipName;
        impl.vkSavedClockInfo = nv::perf::VulkanGetDeviceClockState(instance, physicalDevice, device, vkGetInstanceProcAddr, vkGetDeviceProcAddr);
        impl.vkClockSaved = nv::perf::VulkanSetDeviceClockState(instance, physicalDevice, device, NVPW_DEVICE_CLOCK_SETTING_LOCK_TO_RATED_TDP, vkGetInstanceProcAddr, vkGetDeviceProcAddr);
        return InitRangeMetrics(impl, statusText);
    }
#endif

    static void EndRangeSession(PerfSdkHudImpl& impl)
    {
        if (!impl.rangeSessionActive)
            return;
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:
            if (impl.d3d11Device && impl.d3d11Context)
                nv::perf::D3D11Finish(impl.d3d11Device.Get(), impl.d3d11Context.Get());
            impl.rangeProfilerD3D11.EndSession();
            break;
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:
            impl.rangeProfilerD3D12.EndSession();
            break;
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan:
            impl.rangeProfilerVulkan.EndSession();
            break;
#endif
        }
        impl.rangeSessionActive = false;
        impl.regionRangePushed = false;
        impl.activeRegionEventIndex = -1;
        impl.rangePassesSubmitted = 0;
    }

#if GRAPHICS_API_DIRECTX12
    // Replicates the wrapper's low-level NVPW BeginSession calls to capture the exact NVPA_Status that the
    // RangeProfilerD3D12 wrapper hides behind "unknown error". Only invoked after the wrapper already failed.
    static void DiagnoseD3D12BeginSession(PerfSdkHudImpl& impl)
    {
        if (!impl.d3d12Queue)
        {
            LOG(Warning, "[PerfSDK][diag] D3D12 queue is null; cannot probe BeginSession.");
            return;
        }

        NVPW_D3D12_Profiler_CalcTraceBufferSize_Params calc = { NVPW_D3D12_Profiler_CalcTraceBufferSize_Params_STRUCT_SIZE };
        calc.maxRangesPerPass = 16;
        calc.avgRangeNameLength = 128;
        NVPA_Status st = NVPW_D3D12_Profiler_CalcTraceBufferSize(&calc);
        LOG(Warning, "[PerfSDK][diag] CalcTraceBufferSize -> {0} (traceBufferSize={1}).",
            String(nv::perf::FormatStatus(st).c_str()), (int32)calc.traceBufferSize);
        if (st != NVPA_STATUS_SUCCESS)
            return;

        NVPW_D3D12_Profiler_Queue_BeginSession_Params bs = { NVPW_D3D12_Profiler_Queue_BeginSession_Params_STRUCT_SIZE };
        bs.pCommandQueue = impl.d3d12Queue.Get();
        bs.numTraceBuffers = 5;
        bs.traceBufferSize = calc.traceBufferSize;
        bs.maxRangesPerPass = 16;
        bs.maxLaunchesPerPass = 16;
        st = NVPW_D3D12_Profiler_Queue_BeginSession(&bs);
        LOG(Warning, "[PerfSDK][diag] NVPW_D3D12_Profiler_Queue_BeginSession -> {0}.", String(nv::perf::FormatStatus(st).c_str()));
        if (st == NVPA_STATUS_SUCCESS)
        {
            NVPW_D3D12_Profiler_Queue_EndSession_Params es = { NVPW_D3D12_Profiler_Queue_EndSession_Params_STRUCT_SIZE };
            es.pCommandQueue = impl.d3d12Queue.Get();
            es.timeout = 0xFFFFFFFF;
            NVPW_D3D12_Profiler_Queue_EndSession(&es);
        }
    }
#endif
#if GRAPHICS_API_VULKAN
    // Vulkan analog of DiagnoseD3D12BeginSession: replicates RangeProfilerVulkan::BeginSession's low-level NVPW
    // calls so we can capture the exact NVPA_Status the wrapper hides behind "unknown error".
    static void DiagnoseVulkanBeginSession(PerfSdkHudImpl& impl)
    {
        if (!impl.vkQueue || !impl.vkInstance || !impl.vkPhysicalDevice || !impl.vkDevice)
        {
            LOG(Warning, "[PerfSDK][diag] Vulkan handles null; cannot probe BeginSession.");
            return;
        }

        const bool nvidia = nv::perf::VulkanIsNvidiaDevice(impl.vkPhysicalDevice, impl.vkInstance, vkGetInstanceProcAddr);
        const bool supported = nvidia && nv::perf::profiler::VulkanIsGpuSupported(impl.vkInstance, impl.vkPhysicalDevice, impl.vkDevice, vkGetInstanceProcAddr, vkGetDeviceProcAddr);
        LOG(Warning, "[PerfSDK][diag] Vulkan IsNvidiaDevice={0} IsGpuSupported={1}.", nvidia ? 1 : 0, supported ? 1 : 0);
        if (!supported)
            return;

        NVPW_VK_Profiler_CalcTraceBufferSize_Params calc = { NVPW_VK_Profiler_CalcTraceBufferSize_Params_STRUCT_SIZE };
        calc.maxRangesPerPass = 16;
        calc.avgRangeNameLength = 128;
        NVPA_Status st = NVPW_VK_Profiler_CalcTraceBufferSize(&calc);
        LOG(Warning, "[PerfSDK][diag] NVPW_VK_Profiler_CalcTraceBufferSize -> {0} (traceBufferSize={1}).",
            String(nv::perf::FormatStatus(st).c_str()), (int32)calc.traceBufferSize);
        if (st != NVPA_STATUS_SUCCESS)
            return;

        NVPW_VK_Profiler_Queue_BeginSession_Params bs = { NVPW_VK_Profiler_Queue_BeginSession_Params_STRUCT_SIZE };
        bs.instance = impl.vkInstance;
        bs.physicalDevice = impl.vkPhysicalDevice;
        bs.device = impl.vkDevice;
        bs.queue = impl.vkQueue;
        bs.pfnGetInstanceProcAddr = (void*)vkGetInstanceProcAddr;
        bs.pfnGetDeviceProcAddr = (void*)vkGetDeviceProcAddr;
        bs.numTraceBuffers = 5;
        bs.traceBufferSize = calc.traceBufferSize;
        bs.maxRangesPerPass = 16;
        bs.maxLaunchesPerPass = 16;
        st = NVPW_VK_Profiler_Queue_BeginSession(&bs);
        LOG(Warning, "[PerfSDK][diag] NVPW_VK_Profiler_Queue_BeginSession -> {0}.", String(nv::perf::FormatStatus(st).c_str()));
        if (st == NVPA_STATUS_SUCCESS)
        {
            NVPW_VK_Profiler_Queue_EndSession_Params es = { NVPW_VK_Profiler_Queue_EndSession_Params_STRUCT_SIZE };
            es.queue = impl.vkQueue;
            es.timeout = 0xFFFFFFFF;
            NVPW_VK_Profiler_Queue_EndSession(&es);
        }
    }
#endif

    static bool BeginRangeSession(PerfSdkHudImpl& impl)
    {
        gPendingNvPerfSessionError.Clear();
        EndRangeSession(impl);

        // D3D12/Vulkan: the queue-based NVPW BeginSession returns NVPA_STATUS_INVALID_CONTEXT_STATE when the
        // command queue still has GPU work in flight (Flax has been rendering for many frames, unlike the
        // reference samples that begin on a pristine queue). Idle the GPU so the queue is at a clean point
        // before the profiler takes it over. Safe here because BeginRangeSession runs on the render thread.
        if (impl.backend != PerfSdkBackend::D3D11 && GPUDevice::Instance)
            GPUDevice::Instance->WaitForGPU();

        nv::perf::profiler::SessionOptions sessionOptions;
        sessionOptions.maxNumRanges = 16;
        sessionOptions.numTraceBuffers = 5;

        bool began = false;
        switch (impl.backend)
        {
#if GRAPHICS_API_DIRECTX11
        case PerfSdkBackend::D3D11:
            if (impl.d3d11Context)
                began = impl.rangeProfilerD3D11.BeginSession(impl.d3d11Context.Get(), sessionOptions);
            break;
#endif
#if GRAPHICS_API_DIRECTX12
        case PerfSdkBackend::D3D12:
            if (impl.d3d12Queue)
                began = impl.rangeProfilerD3D12.BeginSession(impl.d3d12Queue.Get(), sessionOptions);
            break;
#endif
#if GRAPHICS_API_VULKAN
        case PerfSdkBackend::Vulkan:
            if (impl.vkQueue)
            {
                // Re-bind NvPerf driver hooks and drain the queue before NvPerf takes it over (NVIDIA samples
                // call OnFrameStart/BeginSession at frame start, before submit; never after present).
                if (!nv::perf::VulkanLoadDriver(impl.vkInstance))
                    LOG(Warning, "[PerfSDK] VulkanLoadDriver failed before BeginSession.");
                if (impl.vkDevice)
                    vkDeviceWaitIdle(impl.vkDevice);
                vkQueueWaitIdle(impl.vkQueue);
                began = impl.rangeProfilerVulkan.BeginSession(impl.vkInstance, impl.vkPhysicalDevice, impl.vkDevice, impl.vkQueue, impl.vkQueueFamilyIndex, sessionOptions, vkGetInstanceProcAddr, vkGetDeviceProcAddr);
            }
            break;
#endif
        }
        if (!began)
        {
            LOG(Warning, "[PerfSDK] BeginRangeSession FAILED ({0}).", BackendName(impl.backend));
#if GRAPHICS_API_DIRECTX12
            if (impl.backend == PerfSdkBackend::D3D12)
                DiagnoseD3D12BeginSession(impl);
#endif
#if GRAPHICS_API_VULKAN
        	if (impl.backend == PerfSdkBackend::Vulkan)
            {
                DiagnoseVulkanBeginSession(impl);
                LogVulkanEngineSetupHint();
            }
#endif
            return false;
        }

        impl.dbgPassBeginCount = 0;
        impl.dbgPassEndCount = 0;
        impl.dbgRangePushCount = 0;
        impl.dbgDecodeCallCount = 0;
        impl.dbgLoggedFirstPush = false;
        LOG(Info, "[PerfSDK] BeginRangeSession OK ({0}) passesPerSample={1} metrics={2}.",
            BackendName(impl.backend), (int32)impl.rangePassesPerSample, (int32)impl.activeMetrics.size());

        impl.rangeSessionActive = true;
        impl.sessionBeginAttempts = 0;
        impl.lastSessionError.Clear();
        impl.regionRangePushed = false;
        impl.activeRegionEventIndex = -1;
        impl.rangePassesSubmitted = 0;
        impl.rangeDataValid = false;

        // Queue the first measurement cycle.
        if (!RangeEnqueue(impl))
        {
            EndRangeSession(impl);
            return false;
        }
        return true;
    }

    static bool IsUsableMetricValue(double value)
    {
        return !std::isnan(value) && !std::isinf(value);
    }

    static void EvaluateRange(PerfSdkHudImpl& impl, const std::vector<uint8_t>& counterDataImage)
    {
        if (counterDataImage.empty() || impl.activeMetrics.empty() || !impl.metricsEvaluatorRaw)
            return;

        const uint8_t* image = counterDataImage.data();
        const size_t imageSize = counterDataImage.size();
        if (!nv::perf::MetricsEvaluatorSetDeviceAttributes(impl.metricsEvaluatorRaw, image, imageSize))
            return;

        const size_t numRanges = nv::perf::CounterDataGetNumRanges(image);
        if (numRanges == 0)
            return;

        std::vector<NVPW_MetricEvalRequest> requests;
        requests.reserve(impl.activeMetrics.size());
        for (const ActiveRangeMetric& metric : impl.activeMetrics)
            requests.push_back(metric.Request);

        if (impl.rangeMetricValues.size() != requests.size())
            impl.rangeMetricValues.assign(requests.size(), 0.0);

        std::vector<uint8_t> updated(requests.size(), 0);
        size_t successCount = 0;

        // Prefer batch evaluation on each range; merged counter data can land on any range index.
        for (size_t rangeIndex = 0; rangeIndex < numRanges; rangeIndex++)
        {
            std::vector<double> batchValues(requests.size(), 0.0);
            if (!nv::perf::EvaluateToGpuValues(
                    impl.metricsEvaluatorRaw,
                    image,
                    imageSize,
                    rangeIndex,
                    requests.size(),
                    requests.data(),
                    batchValues.data()))
            {
                continue;
            }

            for (size_t i = 0; i < batchValues.size(); i++)
            {
                if (!IsUsableMetricValue(batchValues[i]))
                    continue;
                impl.rangeMetricValues[i] = batchValues[i];
                updated[i] = 1;
                successCount++;
            }
        }

        // Per-metric fallback across all ranges for anything the batch pass missed.
        for (size_t i = 0; i < requests.size(); i++)
        {
            if (updated[i])
                continue;

            for (size_t rangeIndex = 0; rangeIndex < numRanges; rangeIndex++)
            {
                double value = impl.rangeMetricValues[i];
                if (nv::perf::EvaluateToGpuValues(
                        impl.metricsEvaluatorRaw,
                        image,
                        imageSize,
                        rangeIndex,
                        1,
                        &requests[i],
                        &value) &&
                    IsUsableMetricValue(value))
                {
                    impl.rangeMetricValues[i] = value;
                    updated[i] = 1;
                    successCount++;
                    break;
                }
            }
        }

        if (successCount == 0)
            return;

        impl.rangeDataValid = true;
        impl.rangeMeasurementCount++;
    }

    static NVPW_Submetric DefaultSubmetricForMetricType(NVPW_MetricType metricType)
    {
        switch (metricType)
        {
        case NVPW_METRIC_TYPE_RATIO:
            return NVPW_SUBMETRIC_RATIO;
        case NVPW_METRIC_TYPE_THROUGHPUT:
            return NVPW_SUBMETRIC_PCT_OF_PEAK_SUSTAINED_ELAPSED;
        default:
            return NVPW_SUBMETRIC_NONE;
        }
    }

    static std::string BuildDefaultMetricEvalName(NVPW_MetricsEvaluator* evaluator, NVPW_MetricType metricType, size_t metricIndex)
    {
        NVPW_MetricEvalRequest request{};
        request.metricIndex = metricIndex;
        request.metricType = static_cast<uint8_t>(metricType);
        request.rollupOp = static_cast<uint8_t>(NVPW_ROLLUP_OP_AVG);
        request.submetric = static_cast<uint8_t>(DefaultSubmetricForMetricType(metricType));
        return nv::perf::ToString(evaluator, request);
    }

    static void PollRangeMetricsInternal(PerfSdkHudImpl& impl)
    {
        if (!impl.rangeSessionActive)
            return;
        if (RangeIsInPass(impl)) // never decode mid-pass
            return;

        nv::perf::profiler::DecodeResult decodeResult = {};
        if (!RangeDecode(impl, decodeResult))
        {
            // Session is unusable after a decode failure: restart it cleanly.
            LOG(Warning, "[PerfSDK] DecodeCounters returned FALSE ({0}); restarting session. passBegin={1} passEnd={2} push={3}.",
                BackendName(impl.backend), impl.dbgPassBeginCount, impl.dbgPassEndCount, impl.dbgRangePushCount);
            BeginRangeSession(impl);
            return;
        }

        impl.dbgDecodeCallCount++;
        const size_t numRanges = decodeResult.counterDataImage.empty() ? 0 : nv::perf::CounterDataGetNumRanges(decodeResult.counterDataImage.data());
        // Throttle: log first few polls + every 60th, so we see progress without flooding.
        if (impl.dbgDecodeCallCount <= 5 || (impl.dbgDecodeCallCount % 60) == 0)
        {
            LOG(Info, "[PerfSDK] Decode#{0} ({1}) allSamples={2} imageBytes={3} numRanges={4} passEnd={5} push={6}.",
                impl.dbgDecodeCallCount, BackendName(impl.backend),
                decodeResult.allStatisticalSamplesCollected ? 1 : 0,
                (int32)decodeResult.counterDataImage.size(), (int32)numRanges,
                impl.dbgPassEndCount, impl.dbgRangePushCount);
        }

        if (decodeResult.allStatisticalSamplesCollected && !decodeResult.counterDataImage.empty())
        {
            const bool wasValid = impl.rangeDataValid;
            EvaluateRange(impl, decodeResult.counterDataImage);
            if (!wasValid)
                LOG(Info, "[PerfSDK] Samples complete ({0}): numRanges={1} -> dataValid={2}.",
                    BackendName(impl.backend), (int32)numRanges, impl.rangeDataValid ? 1 : 0);
            impl.rangePassesSubmitted = 0;
            // Arm the next measurement cycle so the next region begin has a config.
            RangeEnqueue(impl);
        }
    }

}

void PerfSdkHud::PollRangeMetrics()
{
    if (!_active || !_impl)
        return;
    PollRangeMetricsInternal(*static_cast<PerfSdkHudImpl*>(_impl));
}

bool PerfSdkHud::TryInitialize(GPUDevice* device, GPUAdapter* adapter, GPUContext* context)
{
    Shutdown();

    if (!device || !context)
    {
        _statusText = TEXT("Nsight Perf: missing GPU device or context.");
        return false;
    }

    const RendererType rendererType = device->GetRendererType();
    _graphicsApiName = RendererTypeToDisplayName(rendererType);

    if (rendererType != RendererType::DirectX11 &&
        rendererType != RendererType::DirectX12 &&
        rendererType != RendererType::Vulkan)
    {
        _statusText = String::Format(TEXT("Nsight Perf: unsupported graphics API ({0}). Use D3D11, D3D12, or Vulkan."), _graphicsApiName);
        return false;
    }

    TrySetNvPerfLibraryPath();
    if (!nv::perf::InitializeNvPerf())
    {
        _statusText = TEXT("Nsight Perf: NVPW_InitializeHost/Target failed.");
        return false;
    }

    // Surface NvPerf's own diagnostics (privilege/driver/resource errors) in the Flax log, at max verbosity.
    nv::perf::UserLogEnableCustom(NvPerfLogToFlax, nullptr);
    nv::perf::SetLogVolumeLevel(nv::perf::LogSeverity::Err, 100);
    nv::perf::SetLogVolumeLevel(nv::perf::LogSeverity::Wrn, 100);
    nv::perf::SetLogVolumeLevel(nv::perf::LogSeverity::Inf, 100);

    auto impl = std::make_unique<PerfSdkHudImpl>();
    bool initOk = false;
    switch (rendererType)
    {
#if GRAPHICS_API_DIRECTX11
    case RendererType::DirectX11:
    {
        impl->backend = PerfSdkBackend::D3D11;
        auto* d3dContext = static_cast<ID3D11DeviceContext*>(context->GetNativePtr());
        ComPtr<ID3D11Device> d3dDevice;
        if (d3dContext)
            d3dContext->GetDevice(&d3dDevice);
        initOk = InitRangeProfilerD3D11(*impl, d3dDevice.Get(), d3dContext, _statusText);
        break;
    }
#endif
#if GRAPHICS_API_DIRECTX12
    case RendererType::DirectX12:
    {
        impl->backend = PerfSdkBackend::D3D12;
        initOk = InitRangeProfilerD3D12(*impl, device, _statusText);
        break;
    }
#endif
#if GRAPHICS_API_VULKAN
    case RendererType::Vulkan:
    {
        impl->backend = PerfSdkBackend::Vulkan;
        initOk = InitRangeProfilerVulkan(*impl, device, adapter, _statusText);
        break;
    }
#endif
    default:
        break;
    }
    if (!initOk)
        return false;

    const String chipName = impl->chipName.empty() ? String(TEXT("GPU")) : String(impl->chipName.c_str());
    _impl = impl.release();
    _active = true;

    _statusText = String::Format(TEXT("Nsight Perf: {0} | {1} | Range Profiler (per-region)"), _graphicsApiName, chipName);
    _overlayText = TEXT("Nsight Perf: select a GPU region to profile.");
    return true;
}

void PerfSdkHud::Shutdown()
{
    if (!_impl)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    EndRangeSession(*impl);
#if GRAPHICS_API_VULKAN
    if (impl->backend == PerfSdkBackend::Vulkan && impl->vkClockSaved)
    {
        nv::perf::VulkanSetDeviceClockState(impl->vkInstance, impl->vkPhysicalDevice, impl->vkDevice, impl->vkSavedClockInfo, vkGetInstanceProcAddr, vkGetDeviceProcAddr);
        impl->vkClockSaved = false;
    }
#endif
    delete impl;
    _impl = nullptr;
    _active = false;
}

void PerfSdkHud::SetTargetRegion(const String& regionName, int32 regionDepth)
{
    if (!_active || !_impl)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    impl->targetRegionName = regionName;
    impl->targetRegionNameUtf8 = ToUtf8Ascii(regionName);
    impl->targetRegionDepth = regionDepth;
    impl->activeRegionEventIndex = -1;
    impl->regionRangePushed = false;
    impl->regionProfilingActive = !regionName.IsEmpty();
    impl->sessionBeginAttempts = 0;
    impl->regionCompletionCount = 0;
    impl->gpuFramesProcessed = 0;
    impl->lastSessionError.Clear();

    impl->rangeDataValid = false;
    impl->rangeMeasurementCount = 0;
    for (double& v : impl->rangeMetricValues)
        v = 0.0;

#if GRAPHICS_API_DIRECTX11
    if (impl->backend == PerfSdkBackend::D3D11)
    {
        // Immediate-mode: safe to (re)start the session inline on this thread.
        if (impl->regionProfilingActive)
        {
            if (!BeginRangeSession(*impl))
                NoteBeginRangeSessionFailed(*impl);
        }
        else
            EndRangeSession(*impl);
    }
    else
#endif
    {
        // Deferred backends: hand session lifecycle to the render thread (OnFrameBegin). Starting a
        // queue-based BeginSession here (UI thread) while the render thread drives the queue makes it fail.
        impl->pendingSessionBegin = impl->regionProfilingActive;
        impl->pendingSessionEnd = !impl->regionProfilingActive;
        LOG(Info, "[PerfSDK] SetTargetRegion '{0}' depth {1} ({2}) -> deferring session begin to render-thread OnFrameBegin.",
            regionName, regionDepth, BackendName(impl->backend));
    }

    UpdateSessionStatus();
}

void PerfSdkHud::OnFrameBegin()
{
    if (!_active || !_impl)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);

#if GRAPHICS_API_DIRECTX11
    // D3D11 is immediate-mode: BeginPass/PushRange happen inline during the region event.
    if (impl->backend == PerfSdkBackend::D3D11)
    {
        if (impl->regionProfilingActive && !impl->rangeSessionActive)
        {
            if (BeginRangeSession(*impl))
                impl->sessionBeginAttempts = 0;
            else
            {
                NoteBeginRangeSessionFailed(*impl);
                if (impl->sessionBeginAttempts <= 3 || (impl->sessionBeginAttempts % 120) == 0)
                    LOG(Warning, "[PerfSDK] D3D11 BeginRangeSession FAILED, attempt {0}.", impl->sessionBeginAttempts);
            }
        }
        return;
    }
#endif

#if GRAPHICS_API_DIRECTX12 || GRAPHICS_API_VULKAN
    // D3D12/Vulkan: queue-based session begin/end at frame start (before GPU submission), matching
    // NvPerf ReportGeneratorVulkan::OnFrameStart / D3D12 ReportGeneratorD3D12::OnFrameStart.
    if (impl->backend == PerfSdkBackend::D3D12 || impl->backend == PerfSdkBackend::Vulkan)
    {
        if (impl->pendingSessionEnd)
        {
            impl->pendingSessionEnd = false;
            EndRangeSession(*impl);
        }
        if (impl->pendingSessionBegin || (impl->regionProfilingActive && !impl->rangeSessionActive))
        {
            if (BeginRangeSession(*impl))
                impl->pendingSessionBegin = false;
            else
            {
                NoteBeginRangeSessionFailed(*impl);
                if (impl->sessionBeginAttempts <= 3 || (impl->sessionBeginAttempts % 120) == 0)
                    LOG(Warning, "[PerfSDK] Render-thread BeginRangeSession FAILED ({0}), attempt {1}.", BackendName(impl->backend), impl->sessionBeginAttempts);
            }
        }
    }
#endif

    // Reset per-frame region tracking, then open a queue-level pass before any GPU submission this frame.
    impl->activeRegionEventIndex = -1;
    impl->regionRangePushed = false;
    impl->regionCmdPtr = nullptr;
    if (!impl->regionProfilingActive || !impl->rangeSessionActive)
        return;
    if (RangeIsInPass(*impl))
        return;
    // Only open a new pass while the current configuration still needs more passes. Once every pass
    // has been submitted we must stop and let the counters decode, otherwise the Range Profiler keeps
    // collecting forever and never reports allStatisticalSamplesCollected.
    if (RangeAllPassesSubmitted(*impl))
        return;
    if (RangeBeginPass(*impl))
    {
        impl->dbgPassBeginCount++;
        if (impl->dbgPassBeginCount <= 3)
            LOG(Info, "[PerfSDK] OnFrameBegin BeginPass OK ({0}) #{1}.", BackendName(impl->backend), impl->dbgPassBeginCount);
    }
    else if (impl->dbgPassBeginCount == 0)
    {
        LOG(Warning, "[PerfSDK] OnFrameBegin BeginPass FAILED ({0}).", BackendName(impl->backend));
    }
}

void PerfSdkHud::OnFrameEnd()
{
    if (!_active || !_impl)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    impl->gpuFramesProcessed++;

    // Deferred backends close the queue-level pass after all of this frame's command lists have been submitted.
    if (impl->backend != PerfSdkBackend::D3D11 && RangeIsInPass(*impl))
    {
        const bool hadRange = impl->regionRangePushed;
        if (RangeEndPass(*impl))
        {
            impl->rangePassesSubmitted++;
            impl->dbgPassEndCount++;
            if (impl->dbgPassEndCount <= 3)
            {
                LOG(Info, "[PerfSDK] OnFrameEnd EndPass OK ({0}) #{1} rangePushedThisFrame={2}.",
                    BackendName(impl->backend), impl->dbgPassEndCount, hadRange ? 1 : 0);
            }
        }
        else if (impl->dbgPassEndCount == 0)
        {
            LOG(Warning, "[PerfSDK] OnFrameEnd EndPass FAILED ({0}).", BackendName(impl->backend));
        }
        impl->regionRangePushed = false;
        impl->regionCmdPtr = nullptr;
        impl->activeRegionEventIndex = -1;
    }

    // Decode whatever is ready (all backends; never blocks, re-arms the next cycle automatically).
    PollRangeMetricsInternal(*impl);
}

void PerfSdkHud::OnProfilerGpuEventBegin(const Char* name, int32 depth, int32 index, GPUContext* context)
{
    if (!_active || !_impl || !context)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    if (impl->activeRegionEventIndex != -1)
        return; // already bracketing the region this frame
    if (!MatchesTargetRegion(*impl, name, depth))
        return;

#if GRAPHICS_API_DIRECTX11
    if (impl->backend == PerfSdkBackend::D3D11)
    {
        // Immediate: open the pass and queue-level range right here around the clicked region.
        if (!impl->rangeSessionActive || RangeIsInPass(*impl))
            return;
        if (!impl->rangeProfilerD3D11.BeginPass())
            return;
        if (!impl->rangeProfilerD3D11.PushRange(impl->targetRegionNameUtf8.c_str()))
        {
            impl->rangeProfilerD3D11.EndPass();
            return;
        }
        impl->activeRegionEventIndex = index;
        impl->regionRangePushed = true;
        return;
    }
#endif

    // Deferred (D3D12/Vulkan): the pass was opened at frame begin; mark the region in the command stream.
    if (!RangeIsInPass(*impl))
    {
        if (!impl->dbgLoggedFirstPush)
        {
            impl->dbgLoggedFirstPush = true;
            LOG(Warning, "[PerfSDK] Region '{0}' matched but NO pass open at event begin ({1}). PushRange skipped.",
                impl->targetRegionName, BackendName(impl->backend));
        }
        return;
    }
    void* cmd = context->GetNativePtr();
    if (!cmd)
    {
        LOG(Warning, "[PerfSDK] Region '{0}' matched but context native cmd ptr is null ({1}).",
            impl->targetRegionName, BackendName(impl->backend));
        return;
    }
    bool pushed = false;
#if GRAPHICS_API_DIRECTX12
    if (impl->backend == PerfSdkBackend::D3D12)
        pushed = nv::perf::profiler::D3D12PushRange(static_cast<ID3D12GraphicsCommandList*>(cmd), impl->targetRegionNameUtf8.c_str());
#endif
#if GRAPHICS_API_VULKAN
    if (impl->backend == PerfSdkBackend::Vulkan)
        pushed = nv::perf::profiler::VulkanPushRange(static_cast<VkCommandBuffer>(cmd), impl->targetRegionNameUtf8.c_str());
#endif
    if (!pushed)
    {
        LOG(Warning, "[PerfSDK] PushRange FAILED for region '{0}' ({1}).", impl->targetRegionName, BackendName(impl->backend));
        return;
    }
    impl->dbgRangePushCount++;
    if (!impl->dbgLoggedFirstPush)
    {
        impl->dbgLoggedFirstPush = true;
        LOG(Info, "[PerfSDK] PushRange OK for region '{0}' ({1}) cmd={2}.",
            impl->targetRegionName, BackendName(impl->backend), (uint64)(uintptr_t)cmd);
    }
    impl->activeRegionEventIndex = index;
    impl->regionRangePushed = true;
    impl->regionCmdPtr = cmd; // pop must target the same command list/buffer the range was pushed into
}

void PerfSdkHud::OnProfilerGpuEventEnd(int32 index, GPUContext* context)
{
    if (!_active || !_impl)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    if (index != impl->activeRegionEventIndex)
        return;

    if (impl->backend == PerfSdkBackend::D3D11)
    {
#if GRAPHICS_API_DIRECTX11
        if (impl->regionRangePushed)
        {
            impl->rangeProfilerD3D11.PopRange();
            impl->rangeProfilerD3D11.EndPass();
            impl->rangePassesSubmitted++;
        }
#endif
    }
    else if (impl->regionRangePushed)
    {
        // Pop on the exact command list/buffer used for the push (the context may have rotated buffers).
        void* cmd = impl->regionCmdPtr;
        if (cmd)
        {
#if GRAPHICS_API_DIRECTX12
            if (impl->backend == PerfSdkBackend::D3D12)
                nv::perf::profiler::D3D12PopRange(static_cast<ID3D12GraphicsCommandList*>(cmd));
#endif
#if GRAPHICS_API_VULKAN
            if (impl->backend == PerfSdkBackend::Vulkan)
                nv::perf::profiler::VulkanPopRange(static_cast<VkCommandBuffer>(cmd));
#endif
        }
    }

    impl->regionRangePushed = false;
    impl->regionCmdPtr = nullptr;
    impl->activeRegionEventIndex = -1;
    impl->regionCompletionCount++;
}

void PerfSdkHud::UpdateSessionStatus()
{
    if (!_active || !_impl)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);

    if (!impl->regionProfilingActive)
    {
        _statusText = String::Format(TEXT("Nsight Perf: {0} | Range Profiler | select a GPU region"), _graphicsApiName);
        return;
    }
    String state;
    if (impl->rangeDataValid)
        state = String::Format(TEXT("ready ({0} updates)"), impl->rangeMeasurementCount);
    else if (!impl->rangeSessionActive && !impl->lastSessionError.IsEmpty())
        state = impl->lastSessionError;
    else if (!impl->rangeSessionActive && impl->gpuFramesProcessed == 0)
        state = TEXT("waiting for GPU frame — press Play (Vulkan/D3D12 start session on render thread)");
    else if (!impl->rangeSessionActive)
        state = String::Format(TEXT("session failed ({0} attempts) — see Output Log for [PerfSDK]"), impl->sessionBeginAttempts);
    else if (impl->rangeSessionActive && impl->dbgRangePushCount == 0 && impl->gpuFramesProcessed > 5)
        state = TEXT("no PushRange yet — Play scene, double-click a region that runs every frame");
    else
        state = String::Format(TEXT("collecting pass {0}/{1}"), impl->rangePassesSubmitted, (int32)impl->rangePassesPerSample);
    _statusText = String::Format(
        TEXT("Nsight Perf: {0} | per-region \"{1}\" depth {2} | hits {3} | {4}"),
        _graphicsApiName,
        impl->targetRegionName,
        impl->targetRegionDepth,
        impl->regionCompletionCount,
        state);
}

void PerfSdkHud::GetDesignerMetrics(Array<RenderPerfTools::DesignerGpuMetric, InlinedAllocation<32>>& metrics) const
{
    metrics.Clear();
    if (!_active || !_impl)
        return;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    if (!impl->rangeDataValid)
        return;
    for (size_t k = 0; k < impl->activeMetrics.size(); k++)
    {
        const RangeMetricDef& def = impl->activeMetrics[k].Def;
        const double rawValue = k < impl->rangeMetricValues.size() ? impl->rangeMetricValues[k] : 0.0;
        RenderPerfTools::DesignerGpuMetric metric;
        metric.Title = def.AreaName;
        metric.Hint = FormatMetricDetail(def.MetricLabel.Get(), def.Format, rawValue);
        metric.Value = ComputeBarValue(def.Format, def.BarScale, rawValue);
        metric.HigherIsBetter = def.HigherIsBetter;
        metrics.Add(metric);
    }
}

String PerfSdkHud::GetChipAreasText() const
{
    if (!_active || !_impl)
        return String::Empty;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    StringBuilder sb;

    for (const Char* groupName : gChipAreaGroupOrder)
    {
        bool wroteGroup = false;
        for (size_t k = 0; k < impl->activeMetrics.size(); k++)
        {
            const RangeMetricDef& def = impl->activeMetrics[k].Def;
            if (def.GroupName != groupName)
                continue;
            if (!wroteGroup)
            {
                sb.AppendFormat(TEXT("#GROUP\t{0}\n"), groupName);
                wroteGroup = true;
            }
            const bool hasData = impl->rangeDataValid;
            const double rawValue = (hasData && k < impl->rangeMetricValues.size()) ? impl->rangeMetricValues[k] : 0.0;
            const float bar = hasData ? ComputeBarValue(def.Format, def.BarScale, rawValue) : 0.0f;
            String detail;
            if (hasData)
                detail = FormatMetricDetail(def.MetricLabel.Get(), def.Format, rawValue);
            else if (!impl->rangeSessionActive)
            {
                if (!impl->lastSessionError.IsEmpty())
                    detail = impl->lastSessionError;
                else if (impl->gpuFramesProcessed == 0)
                    detail = TEXT("Press Play — range session starts on first GPU frame");
                else
                    detail = String::Format(TEXT("Range session failed ({0} attempts) — see Output Log"), impl->sessionBeginAttempts);
            }
            else if (impl->dbgRangePushCount == 0)
                detail = TEXT("Waiting for region PushRange (Play + double-click GPU region)");
            else
                detail = TEXT("Collecting region samples...");
            if (def.Format == ChipMetricFormat::Counter)
                sb.AppendFormat(TEXT("{0}\t{1:.6f}\t{2}\t{3}\t{4}\t{5}\n"), def.AreaName, rawValue, detail, def.HigherIsBetter ? 1 : 0, static_cast<int>(def.Format), (int32)k);
            else
                sb.AppendFormat(TEXT("{0}\t{1:.1f}\t{2}\t{3}\t{4}\t{5}\n"), def.AreaName, bar, detail, def.HigherIsBetter ? 1 : 0, static_cast<int>(def.Format), (int32)k);
        }
    }
    return sb.ToString();
}

String PerfSdkHud::LoadAvailableMetricsText() const
{
    if (!_active || !_impl)
        return String::Empty;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    if (!impl->metricsEvaluatorRaw)
        return String::Empty;

    NVPW_MetricsEvaluator* evaluator = impl->metricsEvaluatorRaw;
    StringBuilder sb;

    auto appendMetrics = [&](NVPW_MetricType metricType, const Char* typeName)
    {
        const nv::perf::MetricsEnumerator enumerator = nv::perf::EnumerateMetrics(evaluator, metricType);
        for (size_t i = 0; i < enumerator.size(); i++)
        {
            const char* baseMetricName = enumerator[i];
            if (!baseMetricName || !baseMetricName[0])
                continue;

            NVPW_MetricType resolvedType = metricType;
            size_t metricIndex = 0;
            if (!nv::perf::GetMetricTypeAndIndex(evaluator, baseMetricName, resolvedType, metricIndex))
                continue;

            const std::string fullMetricName = BuildDefaultMetricEvalName(evaluator, resolvedType, metricIndex);
            if (fullMetricName.empty())
                continue;

            const char* description = nv::perf::GetMetricDescription(evaluator, resolvedType, metricIndex);
            sb.AppendFormat(TEXT("{0}\t{1}\t{2}\n"),
                            String(fullMetricName.c_str()),
                            typeName,
                            description ? String(description) : String::Empty);
        }
    };

    appendMetrics(NVPW_METRIC_TYPE_THROUGHPUT, TEXT("Throughput"));
    appendMetrics(NVPW_METRIC_TYPE_RATIO, TEXT("Ratio"));
    appendMetrics(NVPW_METRIC_TYPE_COUNTER, TEXT("Counter"));
    return sb.ToString();
}

bool PerfSdkHud::TryAddUserMetric(const String& metricName, String& errorText)
{
    errorText.Clear();
    if (!_active || !_impl)
    {
        errorText = TEXT("Perf SDK session is not active.");
        return false;
    }
    if (metricName.IsEmpty())
    {
        errorText = TEXT("Metric name is empty.");
        return false;
    }

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    const std::string metricNameUtf8 = ToUtf8Ascii(metricName);
    if (MetricNameAlreadyActive(*impl, metricNameUtf8.c_str()))
    {
        errorText = TEXT("Metric is already shown.");
        return false;
    }

    NVPW_MetricEvalRequest request{};
    if (!impl->metricsEvaluatorRaw ||
        !nv::perf::ToMetricEvalRequest(impl->metricsEvaluatorRaw, metricNameUtf8.c_str(), request))
    {
        errorText = TEXT("Metric is not supported on this GPU.");
        return false;
    }

    for (const std::string& existing : impl->userMetricNames)
    {
        if (existing == metricNameUtf8)
        {
            errorText = TEXT("Metric is already added.");
            return false;
        }
    }

    impl->userMetricNames.push_back(metricNameUtf8);
    String rebuildError;
    if (!RebuildRangeCounterConfiguration(*impl, rebuildError))
    {
        impl->userMetricNames.pop_back();
        errorText = rebuildError.IsEmpty() ? TEXT("Failed to rebuild metric configuration.") : rebuildError;
        return false;
    }

    if (!MetricNameAlreadyActive(*impl, metricNameUtf8.c_str()))
    {
        impl->userMetricNames.pop_back();
        errorText = TEXT("Could not schedule metric (hardware counter limit). Try a different metric.");
        return false;
    }

    UpdateSessionStatus();
    return true;
}

bool PerfSdkHud::TryRemoveUserMetric(const String& metricName, String& errorText)
{
    errorText.Clear();
    if (!_active || !_impl)
    {
        errorText = TEXT("Perf SDK session is not active.");
        return false;
    }
    if (metricName.IsEmpty())
    {
        errorText = TEXT("Metric name is empty.");
        return false;
    }

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    const std::string metricNameUtf8 = ToUtf8Ascii(metricName);
    auto it = std::find(impl->userMetricNames.begin(), impl->userMetricNames.end(), metricNameUtf8);
    if (it == impl->userMetricNames.end())
    {
        errorText = TEXT("Only user-added metrics can be removed.");
        return false;
    }

    impl->userMetricNames.erase(it);
    String rebuildError;
    if (!RebuildRangeCounterConfiguration(*impl, rebuildError))
    {
        impl->userMetricNames.push_back(metricNameUtf8);
        errorText = rebuildError.IsEmpty() ? TEXT("Failed to rebuild metric configuration.") : rebuildError;
        return false;
    }

    UpdateSessionStatus();
    return true;
}

String PerfSdkHud::GetUserMetricNamesText() const
{
    if (!_active || !_impl)
        return String::Empty;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    StringBuilder sb;
    for (size_t i = 0; i < impl->userMetricNames.size(); i++)
    {
        if (i > 0)
            sb.Append(TEXT("\n"));
        sb.Append(String(impl->userMetricNames[i].c_str()));
    }
    return sb.ToString();
}

String PerfSdkHud::GetActiveMetricNamesText() const
{
    if (!_active || !_impl)
        return String::Empty;

    auto* impl = static_cast<PerfSdkHudImpl*>(_impl);
    StringBuilder sb;
    for (size_t i = 0; i < impl->activeMetrics.size(); i++)
    {
        if (i > 0)
            sb.Append(TEXT("\n"));
        sb.Append(String(impl->activeMetrics[i].Def.MetricName.c_str()));
    }
    return sb.ToString();
}

#else

bool PerfSdkHud::TryInitialize(GPUDevice*, GPUAdapter*, GPUContext*)
{
    _graphicsApiName = TEXT("Unknown");
    _statusText = TEXT("Nsight Perf: COMPILE_WITH_RENDER_PERF_NVPERF build required.");
    return false;
}

void PerfSdkHud::Shutdown()
{
    _active = false;
}

void PerfSdkHud::OnFrameBegin()
{
}

void PerfSdkHud::OnFrameEnd()
{
}

void PerfSdkHud::SetTargetRegion(const String&, int32)
{
}

void PerfSdkHud::UpdateSessionStatus()
{
}

void PerfSdkHud::PollRangeMetrics()
{
}

void PerfSdkHud::OnProfilerGpuEventBegin(const Char*, int32, int32, GPUContext*)
{
}

void PerfSdkHud::OnProfilerGpuEventEnd(int32, GPUContext*)
{
}

void PerfSdkHud::GetDesignerMetrics(Array<RenderPerfTools::DesignerGpuMetric, InlinedAllocation<32>>& metrics) const
{
    metrics.Clear();
}

String PerfSdkHud::GetChipAreasText() const
{
    return String::Empty;
}

String PerfSdkHud::LoadAvailableMetricsText() const
{
    return String::Empty;
}

bool PerfSdkHud::TryAddUserMetric(const String&, String& errorText)
{
    errorText = TEXT("Nsight Perf: COMPILE_WITH_RENDER_PERF_NVPERF build required.");
    return false;
}

bool PerfSdkHud::TryRemoveUserMetric(const String&, String& errorText)
{
    errorText = TEXT("Nsight Perf: COMPILE_WITH_RENDER_PERF_NVPERF build required.");
    return false;
}

String PerfSdkHud::GetUserMetricNamesText() const
{
    return String::Empty;
}

String PerfSdkHud::GetActiveMetricNamesText() const
{
    return String::Empty;
}

#endif
