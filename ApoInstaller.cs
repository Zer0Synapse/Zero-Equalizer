using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ZeroEqualizer;

internal static class ApoInstaller
{
    public const string ProjectPage = "https://sourceforge.net/projects/equalizerapo/";
    private const string Version = "1.4.2";
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { MaxAutomaticRedirections = 8 })
    {
        MaxResponseContentBufferSize = MaximumBytes,
        Timeout = TimeSpan.FromSeconds(40)
    };

    public static string FileName => $"EqualizerAPO-{Package.Architecture}-{Version}.exe";

    private static (string Architecture, string Hash) Package => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => ("x64", "7403be7427bbe1936a40dded082829b6e217fc4f5990fee5cba501f0ae055afa"),
        Architecture.X86 => ("x86", "15bdaba028b8a7805e17805e4596c27ab240381b5d5044cf247fa80717341b35"),
        Architecture.Arm64 => ("ARM64", "0dbfc64dee60f94cf77b297feb1f54d2633d49669bb474086fffb8db23e8f066"),
        _ => throw new PlatformNotSupportedException("Equalizer APO 1.4.2 does not have a package for this system.")
    };

    public static async Task<string> Download(CancellationToken cancellation)
    {
        // SourceForge can return a landing page whose signed mirror URL expires.
        var requestTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = $"{ProjectPage}files/{Version}/{FileName}/download?tsrequest={requestTime}";
        var bytes = await Http.GetByteArrayAsync(url, cancellation);
        if (!IsExpectedPackage(bytes))
        {
            var page = System.Text.Encoding.UTF8.GetString(bytes);
            var link = Regex.Match(page, "data-release-url=\"([^\"]+)\"");
            var address = WebUtility.HtmlDecode(link.Groups[1].Value);
            if (!Uri.TryCreate(address, UriKind.Absolute, out var mirror) || mirror.Scheme != Uri.UriSchemeHttps ||
                mirror.Host != "downloads.sourceforge.net" || !mirror.IsDefaultPort || mirror.UserInfo.Length > 0 ||
                mirror.AbsolutePath != $"/project/equalizerapo/{Version}/{FileName}")
                throw new InvalidDataException("SourceForge did not return a verified installer. Use the project download page.");
            bytes = await Http.GetByteArrayAsync(mirror, cancellation);
        }

        return await StageVerified(bytes, cancellation);
    }

    public static async Task<string> UseDownloadedFile(string path, CancellationToken cancellation)
    {
        using var file = File.OpenRead(path);
        if (file.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException($"Select the official {FileName} installer.");
        var bytes = new byte[file.Length];
        await file.ReadExactlyAsync(bytes, cancellation);
        return await StageVerified(bytes, cancellation);
    }

    private static async Task<string> StageVerified(byte[] bytes, CancellationToken cancellation)
    {
        if (!IsExpectedPackage(bytes))
            throw new InvalidDataException($"The download did not match the official {FileName} checksum.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroEqualizer", "Setup");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, FileName);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellation);
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsExpectedPackage(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z' &&
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(Package.Hash, StringComparison.OrdinalIgnoreCase);

    public static async Task<int> Run(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        if (!IsExpectedPackage(bytes)) throw new InvalidDataException("The installer changed after verification.");
        using var installer = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        }) ?? throw new InvalidOperationException("Windows could not open the Equalizer APO installer.");
        await installer.WaitForExitAsync();
        return installer.ExitCode;
    }
}
