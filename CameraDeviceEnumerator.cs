using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LivestreamStudio;

public static class CameraDeviceEnumerator
{
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid AudioInputDeviceCategory = new("33D9A762-90C8-11D0-BD43-00A0C911CE86");
    private static readonly Guid PropertyBagId = new("55272A00-42CB-11CE-8135-00AA004BB851");

    public static IReadOnlyList<CameraDevice> GetDevices() => EnumerateDevices(VideoInputDeviceCategory);

    public static IReadOnlyList<string> GetAudioDeviceNames() =>
        EnumerateDevices(AudioInputDeviceCategory).Select(device => device.Name).ToList();

    private static IReadOnlyList<CameraDevice> EnumerateDevices(Guid deviceCategory)
    {
        var deviceEnumerator = (ICreateDevEnum)new SystemDeviceEnumerator();
        IEnumMoniker? monikers = null;
        IBindCtx? bindContext = null;

        try
        {
            var category = deviceCategory;
            var result = deviceEnumerator.CreateClassEnumerator(ref category, out monikers, 0);
            if (result == 1 || monikers is null)
            {
                return [];
            }

            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out bindContext));

            var devices = new List<CameraDevice>();
            var monikerBatch = new IMoniker[1];
            while (monikers.Next(1, monikerBatch, IntPtr.Zero) == 0)
            {
                var moniker = monikerBatch[0];
                object? propertyBag = null;

                try
                {
                    var propertyBagId = PropertyBagId;
                    moniker.BindToStorage(bindContext, null, ref propertyBagId, out propertyBag);
                    ((IPropertyBag)propertyBag).Read("FriendlyName", out var name, IntPtr.Zero);
                    moniker.GetDisplayName(bindContext, null, out var devicePath);

                    if (name is string friendlyName && !string.IsNullOrWhiteSpace(devicePath))
                    {
                        devices.Add(new CameraDevice(friendlyName, devicePath, devices.Count));
                    }
                }
                finally
                {
                    ReleaseComObject(propertyBag);
                    ReleaseComObject(moniker);
                }
            }

            return devices;
        }
        finally
        {
            ReleaseComObject(bindContext);
            ReleaseComObject(monikers);
            ReleaseComObject(deviceEnumerator);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx bindContext);

    [ComImport]
    [Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86")]
    private class SystemDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(
            ref Guid category,
            out IEnumMoniker? enumerator,
            int flags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        void Read(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] out object value,
            IntPtr errorLog);
    }
}

public sealed record CameraDevice(string Name, string DevicePath, int DirectShowIndex)
{
    public override string ToString() => Name;
}
