// Copyright (c) Flax Engine. Nsight Perf SDK HUD helper for RenderPerfSDK.

#pragma once

#include "Engine/Core/Collections/Array.h"
#include "Engine/Core/Types/String.h"
#include "RenderPerfTools.h"

class GPUAdapter;
class GPUContext;
class GPUDevice;

/// <summary>
/// Per-region PerfSDK GPU metrics (Range Profiler) for D3D11, D3D12 and Vulkan.
/// </summary>
class PerfSdkHud
{
public:
    bool TryInitialize(GPUDevice* device, GPUAdapter* adapter, GPUContext* context);
    void Shutdown();

    /// <summary>
    /// Called once per frame before any GPU command submission (engine DrawBegin).
    /// Opens a queue-level Range Profiler pass for deferred backends (D3D12/Vulkan).
    /// </summary>
    void OnFrameBegin();

    /// <summary>
    /// Called once per frame after present/flush (engine DrawEnd). Closes the queue-level
    /// pass for deferred backends and decodes any completed region measurement.
    /// </summary>
    void OnFrameEnd();

    /// <summary>
    /// Limits PerfSDK sampling to GPU profiler events matching the selected region.
    /// </summary>
    void SetTargetRegion(const String& regionName, int32 regionDepth);

    /// <summary>
    /// Called from ProfilerGPU when a GPU event begins (via engine callback).
    /// </summary>
    void OnProfilerGpuEventBegin(const Char* name, int32 depth, int32 index, GPUContext* context);

    /// <summary>
    /// Called from ProfilerGPU when a GPU event ends (via engine callback).
    /// </summary>
    void OnProfilerGpuEventEnd(int32 index, GPUContext* context);

    void UpdateSessionStatus();

    bool IsActive() const { return _active; }
    const String& GetGraphicsApiName() const { return _graphicsApiName; }
    const String& GetOverlayText() const { return _overlayText; }
    const String& GetStatusText() const { return _statusText; }

    /// <summary>
    /// Fills designer-friendly throughput metrics from the live HUD data model.
    /// </summary>
    void GetDesignerMetrics(Array<RenderPerfTools::DesignerGpuMetric, InlinedAllocation<32>>& metrics) const;

    /// <summary>
    /// Tab-separated chip area rows for the editor metrics table (Area, throughput %, detail, higher-is-better flag).
    /// </summary>
    String GetChipAreasText() const;

    /// <summary>
    /// Polls range counter decode/evaluate on the editor tick (non-blocking; safe between GPU passes).
    /// </summary>
    void PollRangeMetrics();

    /// <summary>
    /// Enumerates all metrics supported on the current GPU. Each line: name\ttype\tdescription.
    /// </summary>
    String LoadAvailableMetricsText() const;

    /// <summary>
    /// Adds a user-selected metric to the live range profiler configuration.
    /// </summary>
    bool TryAddUserMetric(const String& metricName, String& errorText);

    /// <summary>
    /// Removes a previously added user metric.
    /// </summary>
    bool TryRemoveUserMetric(const String& metricName, String& errorText);

    /// <summary>
    /// Newline-separated metric names added by the user this session.
    /// </summary>
    String GetUserMetricNamesText() const;

    /// <summary>
    /// Newline-separated internal metric names currently shown in the table.
    /// </summary>
    String GetActiveMetricNamesText() const;

private:
    bool _active = false;
    String _graphicsApiName;
    String _overlayText;
    String _statusText;
    void* _impl = nullptr;
};
