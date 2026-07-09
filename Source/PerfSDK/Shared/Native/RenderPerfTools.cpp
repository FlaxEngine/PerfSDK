// Copyright (c) Wojciech Figat. All rights reserved.

#include "RenderPerfTools.h"
#include "PerfSdkPlatform.h"
#include "Engine/Core/Types/StringBuilder.h"
#include "Engine/Engine/Engine.h"
#include "Engine/Graphics/GPUAdapter.h"
#include "Engine/Graphics/GPUContext.h"
#include "Engine/Graphics/GPUDevice.h"
#include "Engine/Graphics/Enums.h"
#include "Engine/Graphics/RenderTask.h"
#include "Engine/Platform/CriticalSection.h"
#include "Engine/Platform/StringUtils.h"
#include "Engine/Profiler/ProfilerGPU.h"
#include "Engine/Threading/Threading.h"
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
    static uint32 MetricsRevision = 0;

    static bool IsExcludedProfilerRegion(const Char* name)
    {
        return name && (StringUtils::Compare(name, TEXT("GUI")) == 0 || StringUtils::Compare(name, TEXT("Render2D")) == 0);
    }

    static bool IsExcludedProfilerRegion(const String& regionName)
    {
        return regionName == TEXT("GUI") || regionName == TEXT("Render2D");
    }

#if COMPILE_WITH_RENDER_PERF_NVPERF
    static PerfSdkHud Hud;
    static String AvailableMetricsText;
    static bool PendingMetricsCatalogLoad = false;
    static CriticalSection HudSessionLock;

    static void OnProfilerGpuEventBegin(const Char* name, int32 depth, int32 index, GPUContext* context)
    {
        if (IsExcludedProfilerRegion(name))
            return;
        if (!SessionActive)
            return;
        ScopeLock lock(HudSessionLock);
        if (!SessionActive || !Hud.IsActive())
            return;
        Hud.OnProfilerGpuEventBegin(name, depth, index, context);
    }

    static void OnProfilerGpuEventEnd(int32 index, GPUContext* context)
    {
        if (!SessionActive)
            return;
        ScopeLock lock(HudSessionLock);
        if (!SessionActive || !Hud.IsActive())
            return;
        Hud.OnProfilerGpuEventEnd(index, context);
    }

    // Fired from GPUDevice::DrawBegin (before any GPU command submission this frame).
    static void OnProfilerGpuFrameBegin()
    {
        if (!SessionActive)
            return;
        ScopeLock lock(HudSessionLock);
        if (!SessionActive || !Hud.IsActive())
            return;
        Hud.OnFrameBegin();
    }

    // Fired from GPUDevice::DrawEnd (after present/flush, once all command lists are submitted).
    static void OnProfilerGpuFrameEnd()
    {
        if (!SessionActive)
            return;
        ScopeLock lock(HudSessionLock);
        if (!SessionActive || !Hud.IsActive())
            return;
        Hud.OnFrameEnd();
        Hud.UpdateSessionStatus();
        Hud.GetDesignerMetrics(RenderPerfTools::DesignerMetrics);
        MetricsRevision = Hud.GetMetricsRevision();
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
    ScopeLock lock(HudSessionLock);
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
    ScopeLock lock(HudSessionLock);
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
    ScopeLock lock(HudSessionLock);
    if (SessionActive && Hud.IsActive())
        return Hud.GetChipAreasText();
#endif
    return String::Empty;
}

uint32 RenderPerfTools::GetMetricsRevision()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    ScopeLock lock(HudSessionLock);
    if (SessionActive && Hud.IsActive())
        return Hud.GetMetricsRevision();
#endif
    return MetricsRevision;
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
    if (IsExcludedProfilerRegion(regionName))
    {
        ErrorText = TEXT("GUI and Render2D are excluded from PerfSDK profiling.");
        SessionActive = false;
        return false;
    }

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

    ScopeLock lock(HudSessionLock);
    if (!Hud.IsActive())
    {
        if (!Hud.TryInitialize(device, adapter, context))
        {
            ErrorText = PerfSdkPlatform::FormatErrorWithHint(Hud.GetStatusText(), device->GetRendererType());
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

    AvailableMetricsText.Clear();
    PendingMetricsCatalogLoad = false;
    SessionActive = true;
    MetricsRevision = Hud.GetMetricsRevision();
    return true;
#else
    ErrorText = TEXT("PerfSDK plugin was built without NVIDIA Perf SDK support. Set NVPERF_SDK_PATH and rebuild scripts.");
    SessionActive = false;
    return false;
#endif
}

void RenderPerfTools::CloseRegionProfiler()
{
    LastProcessedGpuFrame = 0;
    MetricsRevision++;
#if COMPILE_WITH_RENDER_PERF_NVPERF
    SessionActive = false;
    PendingMetricsCatalogLoad = false;
    RemoveProfilerGpuCallbacks();
    if (GPUDevice::Instance)
        GPUDevice::Instance->WaitForGPU();
    {
        ScopeLock lock(HudSessionLock);
        if (Hud.IsActive())
            Hud.Shutdown();
    }
#else
    SessionActive = false;
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
#if COMPILE_WITH_RENDER_PERF_NVPERF
    AvailableMetricsText.Clear();
#endif
}

void RenderPerfTools::ProcessGpuFrameIfNeeded(GPUContext* context)
{
    (void)context;
#if COMPILE_WITH_RENDER_PERF_NVPERF
    // Range decode/poll runs on the render thread in OnProfilerGpuFrameEnd (Hud.OnFrameEnd).
    // UI tick must not call PollRangeMetrics — duplicate decode was spiking CPU every frame.
#endif
}

void RenderPerfTools::TickSession()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (!SessionActive)
        return;

    ScopeLock lock(HudSessionLock);
    if (!SessionActive || !Hud.IsActive())
        return;

    Hud.TryProcessDeferredWork();

    if (PendingMetricsCatalogLoad && IsInMainThread())
    {
        AvailableMetricsText = Hud.LoadAvailableMetricsText();
        PendingMetricsCatalogLoad = false;
    }
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
    if (!SessionActive)
        return;
    ScopeLock lock(HudSessionLock);
    if (!SessionActive || !Hud.IsActive())
        return;
    Hud.OnFrameBegin();
#endif
}

void RenderPerfTools::OnGpuFrameEnd()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (!SessionActive)
        return;
    ScopeLock lock(HudSessionLock);
    if (!SessionActive || !Hud.IsActive())
        return;
    Hud.OnFrameEnd();
#endif
}

String RenderPerfTools::GetAvailableMetricsText()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    return AvailableMetricsText;
#else
    return String::Empty;
#endif
}

bool RenderPerfTools::TryLoadAvailableMetrics()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    if (!SessionActive)
    {
        AvailableMetricsText = TEXT("Open a GPU profiler region first.");
        return false;
    }

    if (!PendingMetricsCatalogLoad && !AvailableMetricsText.IsEmpty())
        return true;

    {
        ScopeLock lock(HudSessionLock);
        if (!SessionActive || !Hud.IsActive())
        {
            AvailableMetricsText = TEXT("Open a GPU profiler region first.");
            return false;
        }
    }

    if (PendingMetricsCatalogLoad)
    {
        if (AvailableMetricsText.IsEmpty())
            AvailableMetricsText = TEXT("Loading available metrics on the GPU thread...");
        return false;
    }

    PendingMetricsCatalogLoad = true;
    AvailableMetricsText = TEXT("Loading available metrics on the GPU thread...");
    return false;
#else
    ErrorText = TEXT("PerfSDK plugin was built without NVIDIA Perf SDK support.");
    return false;
#endif
}

bool RenderPerfTools::TryAddUserMetric(const String& metricName)
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    ScopeLock lock(HudSessionLock);
    if (!SessionActive || !Hud.IsActive())
    {
        ErrorText = TEXT("Open a GPU profiler region first.");
        return false;
    }
    String error;
    if (!Hud.TryAddUserMetric(metricName, error))
    {
        ErrorText = error;
        return false;
    }
    ErrorText.Clear();
    return true;
#else
    ErrorText = TEXT("PerfSDK plugin was built without NVIDIA Perf SDK support.");
    return false;
#endif
}

bool RenderPerfTools::TryRemoveUserMetric(const String& metricName)
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    ScopeLock lock(HudSessionLock);
    if (!SessionActive || !Hud.IsActive())
    {
        ErrorText = TEXT("Open a GPU profiler region first.");
        return false;
    }
    String error;
    if (!Hud.TryRemoveUserMetric(metricName, error))
    {
        ErrorText = error;
        return false;
    }
    ErrorText.Clear();
    return true;
#else
    ErrorText = TEXT("PerfSDK plugin was built without NVIDIA Perf SDK support.");
    return false;
#endif
}

String RenderPerfTools::GetUserMetricNamesText()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    ScopeLock lock(HudSessionLock);
    if (SessionActive && Hud.IsActive())
        return Hud.GetUserMetricNamesText();
#endif
    return String::Empty;
}

String RenderPerfTools::GetActiveMetricNamesText()
{
#if COMPILE_WITH_RENDER_PERF_NVPERF
    ScopeLock lock(HudSessionLock);
    if (SessionActive && Hud.IsActive())
        return Hud.GetActiveMetricNamesText();
#endif
    return String::Empty;
}

