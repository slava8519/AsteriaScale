using System.Runtime.InteropServices;
using AsteriaScale.Interop;
using Microsoft.Win32;

namespace AsteriaScale.Services;

public sealed record HardwareInfo(
    string CpuName,
    string GpuName,
    string NpuName,
    bool NpuDetected,
    bool WindowsMlCompatible,
    Version WindowsVersion)
{
    public string Summary => NpuDetected
        ? string.Format("{0}  •  NPU найден", GpuName)
        : string.Format("{0}  •  GPU-режим", GpuName);

    public string NpuExplanation => NpuDetected
        ? "NPU выполняет локальное OCR под курсором и может ускорять модель значений. GPU масштабирует кадр и, при выборе Vulkan, запускает TranslateGemma."
        : "Выделенный NPU не найден. Все пресеты работают на DirectX 11 GPU.";
}

public sealed class HardwareService
{
    public Task<HardwareInfo> DetectAsync() => Task.Run(Detect);

    private static HardwareInfo Detect()
    {
        string cpu = ReadCpuName();
        string gpu = ReadGpuName();
        string npu = ReadNpuName();
        Version version = Environment.OSVersion.Version;
        bool windowsMlCompatible = version.Major >= 10 && version.Build >= 26100;
        return new HardwareInfo(cpu, gpu, npu, !string.IsNullOrWhiteSpace(npu), windowsMlCompatible, version);
    }

    private static string ReadCpuName()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU Windows";
    }

    private static string ReadGpuName()
    {
        NativeMethods.DisplayDevice device = new()
        {
            Size = Marshal.SizeOf<NativeMethods.DisplayDevice>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceId = string.Empty,
            DeviceKey = string.Empty
        };

        for (uint index = 0; NativeMethods.EnumDisplayDevices(null, index, ref device, 0); index++)
        {
            const int attachedToDesktop = 0x1;
            if ((device.StateFlags & attachedToDesktop) != 0 && !string.IsNullOrWhiteSpace(device.DeviceString))
            {
                return device.DeviceString.Trim();
            }

            device.Size = Marshal.SizeOf<NativeMethods.DisplayDevice>();
        }

        return "DirectX 11 GPU";
    }

    private static string ReadNpuName()
    {
        try
        {
            using RegistryKey? pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pci is null)
            {
                return string.Empty;
            }

            foreach (string deviceId in pci.GetSubKeyNames())
            {
                using RegistryKey? device = pci.OpenSubKey(deviceId);
                if (device is null)
                {
                    continue;
                }

                foreach (string instanceId in device.GetSubKeyNames())
                {
                    using RegistryKey? instance = device.OpenSubKey(instanceId);
                    string text = string.Join(" ",
                        instance?.GetValue("FriendlyName") as string ?? string.Empty,
                        instance?.GetValue("DeviceDesc") as string ?? string.Empty,
                        instance?.GetValue("Class") as string ?? string.Empty);

                    if (text.Contains("NPU", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Neural", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Compute Accelerator", StringComparison.OrdinalIgnoreCase))
                    {
                        string friendlyName = instance?.GetValue("FriendlyName") as string ?? string.Empty;
                        return string.IsNullOrWhiteSpace(friendlyName) ? "NPU Compute Accelerator" : friendlyName.Trim();
                    }
                }
            }
        }
        catch
        {
            // Hardware discovery is advisory and must never prevent the application from starting.
        }

        return string.Empty;
    }
}
