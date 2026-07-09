#pragma once

#include "Engine/Scripting/Plugins/GamePlugin.h"

/// <summary>
/// NVIDIA Nsight PerfSDK plugin. Shared native GamePlugin for both editor and runtime modes.
/// </summary>
API_CLASS(Namespace="NVIDIA") class PERFSDK_API PerfSDK : public GamePlugin
{
    DECLARE_SCRIPTING_TYPE(PerfSDK);

public:
    void Initialize() override;
    void Deinitialize() override;
};
