using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// Minimal COM interop wrapper around Windows' Core Audio IPolicyConfig interface
/// to switch the default recording endpoint while a USB headset is plugged in.
/// </summary>
internal static class WindowsAudioPolicyConfig
{
    public static bool TrySetDefaultEndpoint(string deviceId, Role role, out int hr)
    {
        hr = unchecked((int)0x80004005); // E_FAIL
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        object? clientObj = null;
        try
        {
            clientObj = new PolicyConfigClient();
            var policy = (IPolicyConfig)clientObj;
            hr = policy.SetDefaultEndpoint(deviceId, (int)role);
            return hr >= 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (clientObj != null && Marshal.IsComObject(clientObj))
            {
                _ = Marshal.ReleaseComObject(clientObj);
            }
        }
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void Unused1();
        void Unused2();
        void Unused3();
        void Unused4();
        void Unused5();
        void Unused6();
        void Unused7();
        void Unused8();
        void Unused9();
        void Unused10();

        [PreserveSig]
        int SetDefaultEndpoint([In, MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, [In] int eRole);
    }
}
