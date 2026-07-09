// Copyright (c) Flax Engine. Cross-platform backend capability queries for PerfSDK.

#include "PerfSdkPlatform.h"

#if COMPILE_WITH_RENDER_PERF_NVPERF

#include "Engine/Platform/Platform.h"

PerfSdkPlatform::BackendKind PerfSdkPlatform::GetBackendKind(RendererType type)
{
    switch (type)
    {
    case RendererType::DirectX11: return BackendKind::D3D11;
    case RendererType::DirectX12: return BackendKind::D3D12;
    case RendererType::Vulkan:    return BackendKind::Vulkan;
    default:                      return BackendKind::Unknown;
    }
}

bool PerfSdkPlatform::IsSupportedOnCurrentPlatform(RendererType type)
{
    BackendKind backend = GetBackendKind(type);
    if (backend == BackendKind::Unknown)
        return false;

#if PLATFORM_LINUX
    return backend == BackendKind::Vulkan;
#elif PLATFORM_WIN32
    return backend == BackendKind::D3D11 || backend == BackendKind::D3D12 || backend == BackendKind::Vulkan;
#else
    return false;
#endif
}

bool PerfSdkPlatform::IsCompiledForPlatform(BackendKind backend)
{
    switch (backend)
    {
#if GRAPHICS_API_DIRECTX11
    case BackendKind::D3D11:  return true;
#endif
#if GRAPHICS_API_DIRECTX12
    case BackendKind::D3D12:  return true;
#endif
#if GRAPHICS_API_VULKAN
    case BackendKind::Vulkan: return true;
#endif
    default: return false;
    }
}

String PerfSdkPlatform::GetSetupHint(RendererType type)
{
    if (!IsSupportedOnCurrentPlatform(type))
    {
#if PLATFORM_LINUX
        return TEXT("Linux supports Vulkan only. Switch the project renderer to Vulkan in Graphics Settings.");
#else
        return TEXT("Unsupported graphics API. Use DirectX 11, DirectX 12, or Vulkan on Windows.");
#endif
    }

    switch (GetBackendKind(type))
    {
    case BackendKind::D3D11:
        return TEXT("DirectX 11 (Windows): immediate-mode Range Profiler. Requires NVIDIA GPU with profiling permissions. Avoid -gpudebug.");
    case BackendKind::D3D12:
        return TEXT("DirectX 12 (Windows): deferred Range Profiler. Set NVPERF_SDK_PATH before startup. Avoid -gpudebug. Cook as Debug/Development.");
    case BackendKind::Vulkan:
#if PLATFORM_LINUX
        return TEXT("Vulkan (Linux): set NVPERF_SDK_PATH before startup. Ensure NvPerf/lib/a64 on LD_LIBRARY_PATH. Avoid -gpudebug. Cook as Debug/Development.");
#else
        return TEXT("Vulkan (Windows): set NVPERF_SDK_PATH before startup (device extensions at init). Avoid -gpudebug. Cook as Debug/Development.");
#endif
    default:
        return String::Empty;
    }
}

String PerfSdkPlatform::FormatErrorWithHint(const String& nativeError, RendererType type)
{
    String result = nativeError.IsEmpty() ? TEXT("PerfSDK failed to open the region profiler.") : nativeError;
    String hint = GetSetupHint(type);
    if (!hint.IsEmpty())
    {
        result += TEXT("\n\n");
        result += hint;
    }
    return result;
}

#endif
