using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinWipeHelper
{
    public sealed class SignatureInfo
    {
        public bool Valid;          // WinVerifyTrust says the Authenticode signature is good
        public string Status;       // "Valid", "NotSigned", "HashMismatch", ...
        public string Subject;      // signer certificate subject, or "unsigned"
        public bool Matches(string expectedSigner) =>
            Valid && (string.IsNullOrEmpty(expectedSigner) ||
                      (Subject ?? "").IndexOf(expectedSigner, StringComparison.OrdinalIgnoreCase) >= 0);
        public override string ToString() => $"{Status}, {Subject}";
    }

    /// <summary>Authenticode check, same as PowerShell's Get-AuthenticodeSignature (WinVerifyTrust).</summary>
    public static class Signature
    {
        public static SignatureInfo Check(string path)
        {
            var info = new SignatureInfo { Status = "Unknown", Subject = "unsigned" };
            int hr = Verify(path);
            info.Valid = hr == 0;
            info.Status = Describe(hr);
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(path);
                if (cert != null) info.Subject = cert.Subject;
            }
            catch { }
            return info;
        }

        static string Describe(int hr)
        {
            switch ((uint)hr)
            {
                case 0: return "Valid";
                case 0x800B0100: return "NotSigned";
                case 0x80096010: return "HashMismatch";
                case 0x800B0101: return "Expired";
                case 0x800B010C: return "Revoked";
                case 0x800B0109: return "UntrustedRoot";
                case 0x800B0111: return "ExplicitlyDistrusted";
                case 0x80092026: return "SecurityOptionsError";
                default: return "Invalid (0x" + ((uint)hr).ToString("X8") + ")";
            }
        }

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

        static int Verify(string path)
        {
            var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(file));
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                dwUIChoice = 2,             // WTD_UI_NONE
                fdwRevocationChecks = 0,    // WTD_REVOKE_NONE (like Get-AuthenticodeSignature)
                dwUnionChoice = 1,          // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,          // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x00000010,   // WTD_REVOCATION_CHECK_NONE
            };
            try
            {
                int hr = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                data.dwStateAction = 2;     // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                return hr;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
