// Copyright (c) Wojciech Figat. All rights reserved.

#include "RenderPerfTools.h"
#include "Engine/Core/Types/StringBuilder.h"
#include "Engine/Engine/Engine.h"
#include "Engine/Graphics/GPUAdapter.h"
#include "Engine/Graphics/GPUContext.h"
#include "Engine/Graphics/GPUDevice.h"
#include "Engine/Graphics/Enums.h"
#include "Engine/Graphics/RenderTask.h"
#include "Engine/Platform/CriticalSection.h"
#include "Engine/Profiler/ProfilerGPU.h"
#include "PerfSdkHud.h"

namespace
{
    static bool SessionActive = false;
    static String RegionName;
    static String RegionSummary;
    static String ErrorText;
    static float RegionGpuTimeMs = 0.0f;
    static int32 RegionDepth = -1;
    static int32 RegionDrawCalls = 0;
    static int32 RegionTriangles = 0;
    static int32 RegionVertices = 0;
    static bool GpuFrameHooksInstalled = false;
    static uint64 LastProcessedGpuFrame = 0;

#if COMPILE_WITH_RENDER_PERF_NVPERF
    static PerfSdkHud Hud;

    static void OnProfilerGpuEventBegin(const Char* name, int32 depth, int32 index, GPUContext* context)
    {
        if (!SessionActive)
            return;
        Hud.OnProfilerGpuEventBegin(name, depth, index, context);
    }

    static void OnProfilerGpuEventEnd(int32 index, GPUContext* context)
    {
        if (!SessionActive)
            return;
        Hud.OnProfilerGpuEventEnd(index, context);
    }

    // Fired from GPUDevice::DrawBegin (before any GPU command submission this frame).
    static void OnProfilerGpuFrameBegin()
    {
        if (!SessionActive)
            return;
        Hud.OnFrameBegin();
    }

    // Fired from GPUDevice::DrawEnd (after present/flush, once all command lists are submitted).
    static void OnProfilerGpuFrameEnd()
    {
        if (!SessionActive)
            return;
        Hud.OnFrameEnd();
        Hud.UpdateSessionStatus();
        Hud.GetDesignerMetrics(RenderPerfTools::DesignerMetrics);
    }

    static void InstallProfilerGpuCallbacks()
    {
        ProfilerGPU::EventBegin = OnProfilerGpuEventBegin;
        ProfilerGPU::EventEnd = OnProfilerGpuEventEnd;
        ProfilerGPU::FrameBegin = OnProfilerGpuFrameBegin;
        ProfilerGPU::FrameEnd = OnProfilerGpuFrameEnd;
    }

    static void RemoveProfilerGpuCallbacks()
    {
        ProfilerGPU::EventBegin = nullptr;
        ProfilerGPU::EventEnd = nullptr;
        ProfilerGPU::FrameBegin = nullptr;
        ProfilerGPU::FrameEnd = nullptr;
    }
#endif
}

Array<RenderPerfTools::DesignerGpuMetric, InlinedAllocation<32>> RenderPerfTools::DesignerMetrics;

bool RenderPerfTools::GetIsCompiled()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    return true;
#else
    return false;
#endif
}

bool RenderPerfTools::GetIsSessionActive()
{
    return SessionActive;
}

String RenderPerfTools::GetRegionName()
{
    return RegionName;
}

String RenderPerfTools::GetStatusText()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (Hud.IsActive())
        return Hud.GetStatusText();
#endif
    return String::Empty;
}

String RenderPerfTools::GetMetricsText()
{
    StringBuilder sb;
    if (!RegionSummary.IsEmpty())
    {
        sb.Append(RegionSummary);
        sb.AppendLine();
        sb.AppendLine();
    }

#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (Hud.IsActive())
    {
        if (!Hud.GetGraphicsApiName().IsEmpty())
        {
            sb.AppendFormat(TEXT("Graphics API: {0}"), Hud.GetGraphicsApiName());
            sb.AppendLine();
        }
        if (!Hud.GetStatusText().IsEmpty())
        {
            sb.Append(Hud.GetStatusText());
            sb.AppendLine();
            sb.AppendLine();
        }
        if (!Hud.GetOverlayText().IsEmpty())
            sb.Append(Hud.GetOverlayText());
    }
#endif

    return sb.ToString();
}

String RenderPerfTools::GetChipAreasText()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (Hud.IsActive())
        return Hud.GetChipAreasText();
#endif
    return String::Empty;
}

String RenderPerfTools::GetErrorText()
{
    return ErrorText;
}

float RenderPerfTools::GetRegionGpuTimeMs()
{
    return RegionGpuTimeMs;
}

int32 RenderPerfTools::GetRegionDrawCalls()
{
    return RegionDrawCalls;
}

int32 RenderPerfTools::GetRegionTriangles()
{
    return RegionTriangles;
}

int32 RenderPerfTools::GetRegionVertices()
{
    return RegionVertices;
}

bool RenderPerfTools::TryOpenRegionProfiler(const String& regionName, int32 regionDepth, float gpuTimeMs, int32 drawCalls, int32 triangles, int32 vertices)
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    ErrorText.Clear();
    RegionName = regionName;
    RegionDepth = regionDepth;
    RegionGpuTimeMs = gpuTimeMs;
    RegionDrawCalls = drawCalls;
    RegionTriangles = triangles;
    RegionVertices = vertices;
    RegionSummary = String::Format(TEXT("GPU Profiler Region: {0} (depth {1})\r\nGPU Time: {2:.3f} ms\r\nDraw Calls: {3}\r\nTriangles: {4}\r\nVertices: {5}"),
                                   regionName, regionDepth, gpuTimeMs, drawCalls, triangles, vertices);
    DesignerMetrics.Clear();

    GPUDevice* device = GPUDevice::Instance;
    if (!device)
    {
        ErrorText = TEXT("GPU device is not available.");
        SessionActive = false;
        return false;
    }

    GPUAdapter* adapter = device->GetAdapter();
    GPUContext* context = device->GetMainContext();
    if (!adapter || !context)
    {
        ErrorText = TEXT("GPU adapter or context is not available.");
        SessionActive = false;
        return false;
    }

    if (!Hud.IsActive())
    {
        if (!Hud.TryInitialize(device, adapter, context))
        {
            ErrorText = Hud.GetStatusText();
            if (ErrorText.IsEmpty())
                ErrorText = TEXT("Failed to initialize NVIDIA Perf SDK.");
            SessionActive = false;
            RemoveProfilerGpuCallbacks();
            return false;
        }
    }

    Hud.SetTargetRegion(regionName, regionDepth);
    Hud.UpdateSessionStatus();
    InstallProfilerGpuCallbacks();
    EnsureGpuFrameHooks();

    SessionActive = true;
    return true;
#else
    ErrorText = TEXT("PerfSDK plugin was built without NVIDIA Perf SDK support. Set NVPERF_SDK_PATH and rebuild scripts.");
    SessionActive = false;
    return false;
#endif
}

void RenderPerfTools::CloseRegionProfiler()
{
    SessionActive = false;
    LastProcessedGpuFrame = 0;
#if COMPILE_WITH_RENDER_PERF_NVPERF
    RemoveProfilerGpuCallbacks();
    Hud.Shutdown();
#endif
    RegionName.Clear();
    RegionSummary.Clear();
    ErrorText.Clear();
    RegionGpuTimeMs = 0.0f;
    RegionDepth = -1;
    RegionDrawCalls = 0;
    RegionTriangles = 0;
    RegionVertices = 0;
    DesignerMetrics.Clear();
}

void RenderPerfTools::ProcessGpuFrameIfNeeded(GPUContext* context)
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    // Range Profiler passes are driven by the engine DrawBegin/DrawEnd frame callbacks.
    // This path only refreshes the editor UI text/metrics (safe to call from a UI tick).
    if (!SessionActive || !Hud.IsActive())
        return;
    Hud.UpdateSessionStatus();
    Hud.GetDesignerMetrics(DesignerMetrics);
#endif
}

void RenderPerfTools::TickSession()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    GPUDevice* device = GPUDevice::Instance;
    if (!device)
        return;
    ProcessGpuFrameIfNeeded(device->GetMainContext());
#endif
}

void RenderPerfTools::EnsureGpuFrameHooks()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    // Frame hooks are installed via ProfilerGPUPerfSdkCallbacks::OnFrameBegin/OnFrameEnd
    // (see InstallProfilerGpuCallbacks); nothing to bind per render-task anymore.
    GpuFrameHooksInstalled = true;
#endif
}

void RenderPerfTools::OnGpuFrameBegin()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (SessionActive)
        Hud.OnFrameBegin();
#endif
}

void RenderPerfTools::OnGpuFrameEnd()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (SessionActive)
        Hud.OnFrameEnd();
#endif
}

