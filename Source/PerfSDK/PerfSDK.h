#pragma once

#include "Engine/Scripting/Plugins/GamePlugin.h"

/// <summary>
/// NVIDIA Nsight PerfSDK plugin for in-editor GPU region metrics.
/// </summary>
API_CLASS(Namespace="NVIDIA") class PERFSDK_API PerfSDK : public GamePlugin
{
    DECLARE_SCRIPTING_TYPE(PerfSDK);

public:
    // [GamePlugin]
    void Initialize() override;
    void Deinitialize() override;
};
