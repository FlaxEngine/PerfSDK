#include "PerfSDK.h"
#include "RenderPerfTools.h"

PerfSDK::PerfSDK(const SpawnParams& params)
    : GamePlugin(params)
{
    _description.Name = TEXT("PerfSDK");
#if USE_EDITOR
    _description.Category = TEXT("Profiling");
    _description.Description = TEXT("NVIDIA Nsight PerfSDK metrics in the GPU Profiler (click a region to inspect chip counters).");
    _description.Author = TEXT("NVIDIA");
#endif
    _description.Version = Version(1, 0);
}

void PerfSDK::Initialize()
{
    GamePlugin::Initialize();
    RenderPerfTools::EnsureGpuFrameHooks();
}

void PerfSDK::Deinitialize()
{
    if (RenderPerfTools::GetIsSessionActive())
        RenderPerfTools::CloseRegionProfiler();
    GamePlugin::Deinitialize();
}
