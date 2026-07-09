#pragma once

#include "Engine/Core/Collections/Array.h"
#include "Engine/Core/Types/String.h"
#include "Engine/Scripting/ScriptingType.h"

class GPUContext;

/// <summary>
/// NVIDIA Nsight Perf SDK integration for GPU profiler region inspection.
/// </summary>
API_CLASS(Static, Namespace="NVIDIA") class RenderPerfTools
{
    DECLARE_SCRIPTING_TYPE_NO_SPAWN(RenderPerfTools);

public:
    /// <summary>
    /// Game-designer friendly GPU throughput metric (0-100%).
    /// </summary>
    API_STRUCT(NoDefault) struct DesignerGpuMetric
    {
        DECLARE_SCRIPTING_TYPE_MINIMAL(DesignerGpuMetric);

        /// <summary>
        /// Short metric title for game developers.
        /// </summary>
        API_FIELD() String Title;

        /// <summary>
        /// Plain-language explanation and guidance.
        /// </summary>
        API_FIELD() String Hint;

        /// <summary>
        /// Normalized value in range [0, 100].
        /// </summary>
        API_FIELD() float Value;

        /// <summary>
        /// True if higher values are better (e.g. cache hit rate).
        /// </summary>
        API_FIELD() bool HigherIsBetter;
    };

    /// <summary>
    /// True if the PerfSDK plugin was built with NVPerf SDK support.
    /// </summary>
    API_PROPERTY() static bool GetIsCompiled();

    /// <summary>
    /// True if a region profiling session is active.
    /// </summary>
    API_PROPERTY() static bool GetIsSessionActive();

    /// <summary>
    /// The selected GPU profiler region name.
    /// </summary>
    API_PROPERTY() static String GetRegionName();

    /// <summary>
    /// Perf SDK status line (chip, API, sampling rate).
    /// </summary>
    API_PROPERTY() static String GetStatusText();

    /// <summary>
    /// Combined region summary and live Perf SDK HUD text.
    /// </summary>
    API_PROPERTY() static String GetMetricsText();

    /// <summary>
    /// Live chip-area rows for the editor metrics table. Each line: Area\tValueOrBar\tDetail\tHigherIsBetter\tFormat.
    /// </summary>
    API_PROPERTY() static String GetChipAreasText();

    /// <summary>
    /// Increments when live metrics or status change. Use to avoid rebuilding the metrics UI every frame.
    /// </summary>
    API_PROPERTY() static uint32 GetMetricsRevision();

    /// <summary>
    /// Game-designer friendly GPU throughput metrics (0-100%) updated while a session is active.
    /// </summary>
    API_FIELD(ReadOnly) static Array<DesignerGpuMetric, InlinedAllocation<32>> DesignerMetrics;

    /// <summary>
    /// GPU time in ms for the selected profiler region (from the click that opened this session).
    /// </summary>
    API_PROPERTY() static float GetRegionGpuTimeMs();

    /// <summary>
    /// Draw calls for the selected profiler region.
    /// </summary>
    API_PROPERTY() static int32 GetRegionDrawCalls();

    /// <summary>
    /// Triangles for the selected profiler region.
    /// </summary>
    API_PROPERTY() static int32 GetRegionTriangles();

    /// <summary>
    /// Vertices for the selected profiler region.
    /// </summary>
    API_PROPERTY() static int32 GetRegionVertices();

    /// <summary>
    /// Last error message from TryOpenRegionProfiler.
    /// </summary>
    API_PROPERTY() static String GetErrorText();

    /// <summary>
    /// Starts (or reuses) a Perf SDK sampling session for the selected GPU profiler region.
    /// Returns false if Perf SDK is unavailable or initialization failed.
    /// </summary>
    API_FUNCTION() static bool TryOpenRegionProfiler(const String& regionName, int32 regionDepth, float gpuTimeMs, int32 drawCalls, int32 triangles, int32 vertices);

    /// <summary>
    /// Stops the active Perf SDK sampling session.
    /// </summary>
    API_FUNCTION() static void CloseRegionProfiler();

    /// <summary>
    /// Decodes and ingests Perf SDK samples once per engine frame (deduplicated).
    /// </summary>
    static void ProcessGpuFrameIfNeeded(GPUContext* context);

    /// <summary>
    /// Editor/game tick: applies deferred metric rebuild and metrics catalog load on the main thread.
    /// </summary>
    API_FUNCTION() static void TickSession();

    /// <summary>
    /// Cached list of GPU metrics from the active Perf SDK session. Each line: name\ttype\tdescription.
    /// Call TryLoadAvailableMetrics before reading.
    /// </summary>
    API_PROPERTY() static String GetAvailableMetricsText();

    /// <summary>
    /// Enumerates metrics supported on the current GPU into AvailableMetricsText.
    /// </summary>
    API_FUNCTION() static bool TryLoadAvailableMetrics();

    /// <summary>
    /// Adds a metric (throughput, ratio, counter, etc.) to the live metrics table.
    /// </summary>
    API_FUNCTION() static bool TryAddUserMetric(const String& metricName);

    /// <summary>
    /// Removes a user-added metric from the live metrics table.
    /// </summary>
    API_FUNCTION() static bool TryRemoveUserMetric(const String& metricName);

    /// <summary>
    /// Newline-separated names of metrics added by the user this session.
    /// </summary>
    API_PROPERTY() static String GetUserMetricNamesText();

    /// <summary>
    /// Newline-separated internal metric names currently shown in the metrics table.
    /// </summary>
    API_PROPERTY() static String GetActiveMetricNamesText();

    /// <summary>
    /// Called before scene rendering to begin Perf SDK sampling for the frame.
    /// </summary>
    static void OnGpuFrameBegin();

    /// <summary>
    /// Called after rendering to tick live Perf SDK sampling.
    /// </summary>
    static void OnGpuFrameEnd();

    /// <summary>
    /// Binds render-task delegates for per-frame sampling (safe to call repeatedly).
    /// </summary>
    static void EnsureGpuFrameHooks();
};
