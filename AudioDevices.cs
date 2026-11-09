using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace ZeroEqualizer;

internal sealed record AudioDevice(Guid Id, string Name, bool IsDefault, bool ApoRegistered)
{
    public bool EnhancementsDisabled { get; init; }
    public bool ApoPostMix { get; init; }
}

internal static class AudioDevices
{
    private const uint Active = 1;
    private const uint ReadOnly = 0;
    private const string EffectsKey = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    private const string EnhancementsDisabledKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private static readonly Guid ApoPreMix = new("EACD2258-FCAC-4FF4-B36D-419E924A6D79");
    private static readonly Guid ApoPostMix = new("EC1CC9CE-FAED-4822-828A-82A81A6F018F");
    private static readonly int[] PreMixSlots = [1, 5, 13];
    private static readonly int[] PostMixSlots = [2, 6, 7, 14, 15];
    private static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly PropertyKey EndpointGuid = new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 4);

    public static IReadOnlyList<AudioDevice> List()
    {
        var devices = new List<AudioDevice>();
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        try
        {
            var defaultId = GetDefaultId(enumerator);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(DataFlow.Render, Active, out var endpoints));
            try
            {
                Marshal.ThrowExceptionForHR(endpoints.GetCount(out var count));
                for (uint index = 0; index < count; index++)
                {
                    // An endpoint can disappear between enumeration and inspection.
                    if (endpoints.Item(index, out var endpoint) < 0)
                        continue;

                    try
                    {
                        if (endpoint.OpenPropertyStore(ReadOnly, out var properties) < 0)
                            continue;

                        try
                        {
                            if (!Guid.TryParse(ReadString(properties, EndpointGuid), out var id) || id == Guid.Empty)
                                continue;

                            var name = ReadString(properties, FriendlyName);
                            if (string.IsNullOrWhiteSpace(name))
                                name = id.ToString("B");

                            var (registered, postMix, enhancementsDisabled) = DetectApo(id);
                            devices.Add(new AudioDevice(id, name, id == defaultId, registered)
                            {
                                ApoPostMix = postMix,
                                EnhancementsDisabled = enhancementsDisabled
                            });
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(properties);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(endpoint);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(endpoints);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        devices.Sort((left, right) =>
        {
            var compareDefault = right.IsDefault.CompareTo(left.IsDefault);
            return compareDefault != 0
                ? compareDefault
                : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });
        return devices;
    }

    public static string? FindConfigurator()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var installKey = localMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            if (installKey?.GetValue("InstallPath") is string path)
            {
                var configurator = ConfiguratorIn(path);
                if (configurator != null)
                    return configurator;
            }
        }

        return ConfiguratorIn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO"));
    }

    private static string? ConfiguratorIn(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            return null;

        var selector = Path.Combine(directory, "DeviceSelector.exe");
        if (File.Exists(selector))
            return selector;

        var legacy = Path.Combine(directory, "Configurator.exe");
        return File.Exists(legacy) ? legacy : null;
    }

    private static Guid? GetDefaultId(IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var endpoint) < 0)
            return null;

        try
        {
            if (endpoint.OpenPropertyStore(ReadOnly, out var properties) < 0)
                return null;

            try
            {
                return Guid.TryParse(ReadString(properties, EndpointGuid), out var id) ? id : null;
            }
            finally
            {
                Marshal.ReleaseComObject(properties);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    private static (bool Registered, bool PostMix, bool EnhancementsDisabled) DetectApo(Guid id)
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var effects = localMachine.OpenSubKey($@"{RenderKey}\{id:B}\FxProperties");
            if (effects == null)
                return default;

            var postMix = HasApo(effects, PostMixSlots, ApoPostMix);
            var registered = postMix || HasApo(effects, PreMixSlots, ApoPreMix);
            var enhancementsDisabled = effects.GetValue(EnhancementsDisabledKey) is int value && value != 0;
            return (registered, postMix, enhancementsDisabled);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Absence of readable registration evidence cannot confirm that the effect is set up.
        }

        return default;
    }

    private static bool HasApo(RegistryKey effects, int[] slots, Guid expected)
    {
        bool Matches(string value) => Guid.TryParse(value, out var id) && id == expected;
        foreach (var slot in slots)
        {
            var value = effects.GetValue($"{EffectsKey},{slot}");
            if (value is string single && Matches(single))
                return true;
            if (value is string[] multiple && Array.Exists(multiple, Matches))
                return true;
        }

        return false;
    }

    private static string? ReadString(IPropertyStore properties, PropertyKey key)
    {
        if (properties.GetValue(ref key, out var value) < 0)
            return null;

        try
        {
            // Both endpoint GUID and display name are LPWSTR properties.
            return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    private enum DataFlow { Render, Capture, All }
    private enum Role { Console, Multimedia, Communications }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        private readonly Guid format = formatId;
        private readonly uint property = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        // The vtable places Activate before OpenPropertyStore.
        [PreserveSig] int Activate(in Guid iid, uint context, IntPtr parameters, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
}
