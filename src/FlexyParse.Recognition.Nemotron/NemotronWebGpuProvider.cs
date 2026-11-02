using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

namespace FlexyParse.Nemotron;

/// <summary>Registers the process-wide plugin once; CPU-only consumers never load it.</summary>
internal static class NemotronWebGpuProvider
{
    // Registration must outlive all sessions. Lazy also serializes concurrent first use.
    private static readonly Lazy<OrtEnv> Environment = new(() =>
    {
        var environment = OrtEnv.Instance();
        environment.RegisterExecutionProviderLibrary("flexyparse_webgpu", WebGpuEp.GetLibraryPath());
        return environment;
    });

    internal static void Configure(SessionOptions settings, int deviceIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deviceIndex);
        try
        {
            var environment = Environment.Value;
            var devices = environment.GetEpDevices()
                .Where(device => device.EpName == WebGpuEp.GetEpName()).ToArray();
            ValidateDeviceIndex(deviceIndex, devices.Length);
            settings.AppendExecutionProvider(environment, [devices[deviceIndex]], new Dictionary<string, string>
            {
                // Page/prefix sizes and KV cache lengths change between runs.
                ["enableGraphCapture"] = "0"
            });
        }
        catch (Exception exception) when (exception is OnnxRuntimeException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException
            or FileNotFoundException)
        {
            throw new InvalidOperationException(
                "Nemotron WebGPU initialization failed. Check the WebGPU plugin/runtime compatibility, platform architecture, " +
                "GPU driver and GpuDeviceId. Linux requires libvulkan.so.1 and a working Vulkan ICD. " +
                "CUDA/cuDNN are not required; no automatic CPU-mode fallback is used.", exception);
        }
    }

    internal static void ValidateDeviceIndex(int deviceIndex, int deviceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deviceIndex);
        if (deviceIndex >= deviceCount)
            throw new InvalidOperationException(
                $"Nemotron WebGPU device index {deviceIndex} is unavailable; {deviceCount} WebGPU device(s) discovered. " +
                "Check GpuDeviceId, GPU drivers and platform support. No automatic CPU-mode fallback is used.");
    }
}
