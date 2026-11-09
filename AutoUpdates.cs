using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace ZeroEqualizer;

internal static class AutoUpdates
{
    internal const string Repository = "https://github.com/Zer0Synapse/Zero-Equalizer";
    internal const string LaunchArgument = "--zero-equalizer-updated";
    private const string Feed = Repository + "/releases/latest/download/update.json";
    private const int MaximumZipBytes = 24 * 1024 * 1024;
    private const int MaximumExpandedBytes = 48 * 1024 * 1024;
    private const string Executable = "ZeroEqualizer.exe";
    private const string AssemblyFile = "ZeroEqualizer.dll";
    private const string Ready = "verified.json";
    private const string Attempted = "attempted";
    private const string Healthy = "healthy";
    private static readonly string[] RuntimeFiles =
    [
        Executable,
        AssemblyFile,
        "ZeroEqualizer.deps.json",
        "ZeroEqualizer.runtimeconfig.json",
        "NAudio.Core.dll",
        "NAudio.Wasapi.dll"
    ];
    private static readonly string[] Documentation = ["Start here.txt", "NAudio-LICENSE.txt"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, MaxDepth = 8 };
    private static readonly Version Current = typeof(AutoUpdates).Assembly.GetName().Version!;
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroEqualizer", "Updates");

    internal sealed record Release(string Version, string Url, string Sha256, long Size);
    private sealed record Verified(string Version, Dictionary<string, string> Files);

    internal static async Task<bool> TryStartLatest()
    {
        try
        {
            // Never run a downloaded user-local package with administrator rights.
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return false;

            Directory.CreateDirectory(Root);
            if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
                return false;
            using var updateLock = AcquireStartupLock();
            if (updateLock == null)
                return true;

            var candidates = FindCandidates(Root, Current);
            var newest = candidates.FirstOrDefault();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var staged = await FetchUpdate(Root, newest.Version ?? Current, timeout.Token);
                if (staged != null)
                {
                    newest = staged.Value;
                    if (candidates.All(candidate => candidate.Version != newest.Version))
                        candidates.Insert(0, newest);
                }
            }
            catch (Exception error) when (Recoverable(error))
            {
                WriteStatus("Update check did not complete. " + error.GetType().Name);
            }

            // A bad startup gets one attempt. Fall back to a previously working copy, then this executable.
            foreach (var candidate in candidates.Take(2))
            {
                try
                {
                    File.WriteAllText(Path.Combine(candidate.Path, Attempted), DateTimeOffset.UtcNow.ToString("O"));
                    var token = Guid.NewGuid().ToString("N");
                    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, StartupSignal(token));
                    var request = new ProcessStartInfo(Path.Combine(candidate.Path, Executable))
                    {
                        UseShellExecute = false,
                        WorkingDirectory = candidate.Path
                    };
                    request.ArgumentList.Add(LaunchArgument);
                    request.ArgumentList.Add(token);
                    using var child = Process.Start(request);
                    if (child == null)
                        continue;
                    var startup = Stopwatch.StartNew();
                    while (!ready.WaitOne(0) && !child.HasExited && startup.Elapsed < TimeSpan.FromSeconds(3))
                        await Task.Delay(75);
                    if (ready.WaitOne(0) || !child.HasExited)
                    {
                        // A running child owns the window. Opening this copy too would duplicate it.
                        WriteStatus("Started " + candidate.Version.ToString(3));
                        return true;
                    }
                    File.Delete(Path.Combine(candidate.Path, Healthy));
                    WriteStatus("Update startup exited. Using an earlier copy.");
                }
                catch (Exception error) when (Recoverable(error))
                {
                    WriteStatus("Update could not start. " + error.GetType().Name);
                }
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            WriteStatus("Using the current version. " + error.GetType().Name);
        }
        return false;
    }

    private static FileStream? AcquireStartupLock()
    {
        try
        {
            return new FileStream(Path.Combine(Root, "startup.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }
    }

    private static string StartupSignal(string token) => "Local\\ZeroEqualizer.UpdateReady." + token;

    internal static void MarkHealthy(string? token)
    {
        try
        {
            var currentFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            if (Path.GetDirectoryName(currentFolder)?.Equals(Root, StringComparison.OrdinalIgnoreCase) == true &&
                File.Exists(Path.Combine(currentFolder, Ready)))
            {
                File.WriteAllText(Path.Combine(currentFolder, Healthy), "Startup completed");
                if (token != null && EventWaitHandle.TryOpenExisting(StartupSignal(token), out var ready))
                {
                    using (ready) ready.Set();
                }
            }
        }
        catch (Exception error) when (Recoverable(error)) { }
    }

    private static List<(Version Version, string Path)> FindCandidates(string root, Version minimum)
    {
        var candidates = new List<(Version Version, string Path)>();
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            if (TryVersion(Path.GetFileName(path), out var version) && version > minimum &&
                CanLaunch(path, version))
                candidates.Add((version, path));
        }
        return candidates.OrderByDescending(candidate => candidate.Version).ToList();
    }

    private static bool CanLaunch(string folder, Version version)
    {
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
                File.Exists(Path.Combine(folder, Attempted)) && !File.Exists(Path.Combine(folder, Healthy)))
                return false;
            var marker = Path.Combine(folder, Ready);
            if (!File.Exists(marker) || new FileInfo(marker).Length > 16_384)
                return false;
            var verified = JsonSerializer.Deserialize<Verified>(File.ReadAllBytes(marker), Json);
            return verified != null && verified.Version == version.ToString(3) && verified.Files != null &&
                RuntimeFiles.All(name => VerifiedFile(folder, verified.Files, name)) &&
                MatchesAssembly(folder, version);
        }
        catch (Exception error) when (Recoverable(error)) { return false; }
    }

    private static bool VerifiedFile(string folder, Dictionary<string, string> hashes, string name)
    {
        var file = new FileInfo(Path.Combine(folder, name));
        if (!file.Exists || file.Length < 1 || file.Length > MaximumExpandedBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !hashes.TryGetValue(name, out var hash))
            return false;
        using var stream = file.OpenRead();
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    internal static Version Validate(Release release)
    {
        if (!TryVersion(release.Version, out var version))
            throw new InvalidDataException("Invalid release version.");
        var url = Repository + $"/releases/download/v{release.Version}/ZeroEqualizer-{release.Version}.zip";
        if (release.Url != url || release.Sha256 is not { Length: 64 } ||
            !release.Sha256.All(Uri.IsHexDigit) || release.Size is < 1 or > MaximumZipBytes)
            throw new InvalidDataException("Invalid release download.");
        return version;
    }

    private static bool TryVersion(string? value, out Version version)
    {
        if (value is { Length: <= 17 } && Version.TryParse(value, out var parsed) && parsed.Build >= 0 &&
            parsed.Revision < 0 && value == parsed.ToString(3))
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    // A four-component assembly version and a three-component public tag refer to the same release.
    private static bool MatchesAssembly(string folder, Version version)
    {
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(folder, AssemblyFile));
        return assembly.Name == "ZeroEqualizer" && assembly.Version?.ToString(3) == version.ToString(3);
    }

    internal static async Task<(Version Version, string Path)?> FetchUpdate(string root, Version minimum,
        CancellationToken cancellation)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZeroEqualizer");
        var manifest = await Download(client, Feed, 16_384, cancellation).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<Release>(manifest, Json) ??
            throw new InvalidDataException("The release manifest is empty.");
        var version = Validate(release);
        if (version <= minimum)
            return null;

        var destination = Path.Combine(root, release.Version);
        if (Directory.Exists(destination))
            return CanLaunch(destination, version) ? (version, destination) : null;

        var bytes = await Download(client, release.Url, release.Size, cancellation).ConfigureAwait(false);
        VerifyPackage(bytes, release);
        var staging = Path.Combine(root, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var hashes = Extract(bytes, staging, cancellation);
            if (!MatchesAssembly(staging, version))
                throw new InvalidDataException("The downloaded application version does not match the release.");
            File.WriteAllText(Path.Combine(staging, Ready), JsonSerializer.Serialize(new Verified(release.Version, hashes)));
            Directory.Move(staging, destination);
            return (version, destination);
        }
        finally
        {
            // This directory is newly created under the update root. Delete only the known temporary files.
            if (Directory.Exists(staging))
            {
                try
                {
                    foreach (var name in RuntimeFiles.Concat(Documentation).Append(Ready))
                        File.Delete(Path.Combine(staging, name));
                    Directory.Delete(staging);
                }
                catch (Exception error) when (Recoverable(error)) { }
            }
        }
    }

    internal static void VerifyPackage(byte[] bytes, Release release)
    {
        if (bytes.LongLength != release.Size ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Windows download does not match the release checksum.");
    }

    private static async Task<byte[]> Download(HttpClient client, string url, long limit, CancellationToken cancellation)
    {
        var uri = new Uri(url);
        for (int redirect = 0; redirect < 6; redirect++)
        {
            if (!IsTrustedHost(uri))
                throw new InvalidDataException("Unexpected release download host.");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location ?? throw new InvalidDataException("Missing release redirect.");
                uri = new Uri(uri, next);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit)
                throw new InvalidDataException("Release download exceeds its size limit.");
            using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[32_768];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > limit)
                    throw new InvalidDataException("Release download exceeds its size limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("Too many release redirects.");
    }

    internal static bool IsTrustedHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Host is "github.com" or "release-assets.githubusercontent.com" or
            "objects.githubusercontent.com";

    internal static Dictionary<string, string> Extract(byte[] bytes, string folder, CancellationToken cancellation)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count < RuntimeFiles.Length ||
            archive.Entries.Count > RuntimeFiles.Length + Documentation.Length)
            throw new InvalidDataException("Unexpected release ZIP contents.");

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var buffer = new byte[32_768];
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = entry.FullName;
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((!RuntimeFiles.Contains(name, StringComparer.Ordinal) && !Documentation.Contains(name, StringComparer.Ordinal)) ||
                files.ContainsKey(name) || (unixType != 0 && unixType != 0x8000) || entry.Length <= 0 ||
                entry.Length > MaximumExpandedBytes - total)
                throw new InvalidDataException("Unsafe or unexpected release ZIP entry.");

            using var input = entry.Open();
            using var file = new FileStream(Path.Combine(folder, name), FileMode.CreateNew);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                size += read;
                if (size > entry.Length)
                    throw new InvalidDataException("Expanded release ZIP entry exceeds its declared size.");
                file.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            if (size != entry.Length)
                throw new InvalidDataException("Truncated release ZIP entry.");
            files.Add(name, Convert.ToHexString(hash.GetHashAndReset()));
            total += size;
        }
        if (!RuntimeFiles.All(files.ContainsKey))
            throw new InvalidDataException("The release ZIP is missing application files.");
        return files;
    }

    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or HttpRequestException or OperationCanceledException or
        BadImageFormatException or Win32Exception or ArgumentException or SecurityException or InvalidOperationException;

    private static void WriteStatus(string message)
    {
        try { File.WriteAllText(Path.Combine(Root, "status.txt"), message); }
        catch (Exception error) when (Recoverable(error)) { }
    }
}
