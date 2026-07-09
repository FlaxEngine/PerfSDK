// Copyright (c) Flax Engine. Cross-platform backend capability queries for PerfSDK.
// Isolates Windows (D3D11/D3D12/VK) vs Linux (VK) knowledge in one place.

#pragma once

#include "Engine/Core/Types/String.h"
#include "Engine/Graphics/Enums.h"

#if COMPILE_WITH_RENDER_PERF_NVPERF

/// <summary>
/// Platform and graphics-backend capability helpers for PerfSDK.
/// All Windows-specific code paths are gated behind PLATFORM_WIN32 / GRAPHICS_API_DIRECTX*.
/// Linux builds compile only Vulkan paths — D3D code is excluded by the build system.
/// </summary>
class PerfSdkPlatform
{
public:
    enum class BackendKind : uint8
    {
        Unknown = 0,
        D3D11 = 1,
        D3D12 = 2,
        Vulkan = 3,
    };

    /// <summary>
    /// Maps Flax RendererType to PerfSDK backend kind.
    /// </summary>
    static BackendKind GetBackendKind(RendererType type);

    /// <summary>
    /// True if the backend is supported on the current OS build.
    /// </summary>
    static bool IsSupportedOnCurrentPlatform(RendererType type);

    /// <summary>
    /// True if the backend was compiled into this plugin binary.
    /// Mirrors PerfSDK.Build.cs: Windows gets D3D11+D3D12+VK, Linux gets VK only.
    /// </summary>
    static bool IsCompiledForPlatform(BackendKind backend);

    /// <summary>
    /// API-specific setup hint for error messages (Editor and Runtime modes).
    /// </summary>
    static String GetSetupHint(RendererType type);

    /// <summary>
    /// Combines native error text with platform/API setup guidance.
    /// </summary>
    static String FormatErrorWithHint(const String& nativeError, RendererType type);
};

#endif
