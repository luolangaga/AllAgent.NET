using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AllAgent.NET.Core.Services.Updates;

public sealed record ChecksumResult(bool Ok, bool Missing, string? Expected, string? Actual)
{
    public string Message => Missing
        ? "发布资源没有提供校验和。"
        : Ok ? "SHA256 校验通过。" : $"SHA256 不匹配：期望 {Expected}，实际 {Actual}。";
}

public sealed record SignatureResult(UpdateSignatureStatus Status, string? Subject, string? Message)
{
    public bool IsBlocking => Status == UpdateSignatureStatus.Invalid;
}

public static class UpdateVerification
{
    public static async Task<ChecksumResult> VerifyChecksumAsync(string file, string? checksums, string assetName, bool requireChecksum, CancellationToken ct)
    {
        var expected = FindChecksum(checksums, assetName);
        if (expected == null)
            return new ChecksumResult(!requireChecksum, true, null, null);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return new ChecksumResult(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase), false, expected, actual);
    }

    public static string? FindChecksum(string? checksums, string assetName)
    {
        if (string.IsNullOrWhiteSpace(checksums)) return null;
        var wanted = Path.GetFileName(assetName);
        foreach (var raw in checksums.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var star = line.IndexOf('*');
            if (star > 0)
            {
                var hash = line[..star].Trim();
                var name = Path.GetFileName(line[(star + 1)..].Trim());
                if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return hash;
                continue;
            }
            var separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0) continue;
            var digest = line[..separator].Trim();
            var fileName = Path.GetFileName(line[separator..].Trim());
            if (fileName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return digest;
        }
        return null;
    }

    public static SignatureResult VerifyAuthenticode(string path, string? expectedPublisher = null)
    {
        if (!File.Exists(path)) return new(UpdateSignatureStatus.Invalid, null, "文件不存在。");
        if (!OperatingSystem.IsWindows()) return new(UpdateSignatureStatus.NotApplicable, null, "非 Windows 平台不检查 Authenticode。");
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return new(UpdateSignatureStatus.NotApplicable, null, "该文件类型没有 Authenticode 签名。");

        var subject = ReadSubject(path);
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path,
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = filePointer,
                dwStateAction = 1,
                dwProvFlags = 0x100,
            };
            Marshal.StructureToPtr(data, dataPointer, false);
            var action = WinTrustAction;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, dataPointer);

            data.dwStateAction = 2;
            Marshal.StructureToPtr(data, dataPointer, false);
            WinVerifyTrust(IntPtr.Zero, ref action, dataPointer);

            if (result == 0)
            {
                if (!string.IsNullOrWhiteSpace(expectedPublisher) &&
                    (subject == null || !subject.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase)))
                    return new(UpdateSignatureStatus.Invalid, subject, $"签名者不是预期的发布者：{subject ?? "<unknown>"}。");
                return new(UpdateSignatureStatus.Valid, subject, "Authenticode 签名有效。");
            }
            if ((uint)result == 0x800B0100)
                return new(UpdateSignatureStatus.Unsigned, subject, "文件没有 Authenticode 签名。");
            return new(UpdateSignatureStatus.Invalid, subject, $"Authenticode 校验失败：0x{(uint)result:X8}。");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return new(UpdateSignatureStatus.Invalid, subject, "无法执行 Authenticode 校验：" + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static string? ReadSubject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var certificate = X509Certificate.CreateFromSignedFile(path);
            using var certificate2 = new X509Certificate2(certificate);
#pragma warning restore SYSLIB0057
            return certificate2.Subject;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Guid WinTrustAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);
}
