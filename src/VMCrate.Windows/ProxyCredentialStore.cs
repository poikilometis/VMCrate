using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed record ProxyCredential(string User, string Password);

public static class ProxyCredentialStore
{
    public static ProxyCredential? Read(UpdateProxy proxy)
    {
        if (!CredRead(Key(proxy), 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168)
                return null;
            throw new LabException("error.proxyCredential");
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            if (value.BlobSize is < 0 or > 2560 || value.BlobSize % 2 != 0)
                throw new LabException("error.proxyCredential");
            var secret = value.BlobSize == 0 ? "" : Marshal.PtrToStringUni(value.Blob, value.BlobSize / 2);
            return new(value.UserName ?? "", secret ?? "");
        }
        finally { CredFree(pointer); }
    }

    public static void Write(UpdateProxy proxy, ProxyCredential value)
    {
        if (string.IsNullOrWhiteSpace(value.User) || value.User.Length > 513 || value.Password.Length > 1280 ||
            value.User.Contains(':') || value.User.Any(char.IsControl) || value.Password.Any(char.IsControl))
            throw new LabException("error.proxyOptions");
        var secret = Marshal.StringToCoTaskMemUni(value.Password);
        try
        {
            var credential = new Credential
            {
                Type = 1, TargetName = Key(proxy), UserName = value.User, Persist = 2,
                BlobSize = checked(value.Password.Length * sizeof(char)), Blob = secret
            };
            if (!CredWrite(ref credential, 0))
                throw new LabException("error.proxyCredential");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(secret); }
    }

    private static string Key(UpdateProxy proxy) =>
        "VMCrate/UpdateProxy/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{proxy.Address}:{proxy.Port}")));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int BlobSize;
        public nint Blob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);
}
