using System;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ZeroEqualizer;

internal sealed class ApoConfig(string configPath)
{
    private const string Begin = "# BEGIN ZERO EQUALIZER MANAGED BLOCK";
    private const string End = "# END ZERO EQUALIZER MANAGED BLOCK";
    private const string ManualGainTag = "# Zero Equalizer manual preamp";
    private static readonly Regex LegacyGainLine = new(
        @"(?m)^(?<channels>Channel: all(?<newline>\r?\n))(?:Preamp: -[0-9]+\.[0-9] dB\r?\n)?",
        RegexOptions.CultureInvariant);
    private static readonly object WriteGate = new();

    public string ConfigPath { get; } = Path.GetFullPath(configPath);

    public bool IsConnected => ConnectedDevice.HasValue;

    public Guid? ConnectedDevice
    {
        get
        {
            try
            {
                return FindBlock(ReadConfig())?.Device;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static string? FindConfigPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var apo = machine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                if (apo?.GetValue("ConfigPath") is not string directory || !Path.IsPathFullyQualified(directory))
                    continue;

                var configured = Path.Combine(directory, "config.txt");
                return File.Exists(configured) ? configured : null;
            }
            catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
            {
                // The conventional install path can still be checked without registry access.
            }
        }

        var conventional = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "EqualizerAPO", "config", "config.txt");
        return File.Exists(conventional) ? conventional : null;
    }

    public void Apply(Guid deviceId, string filters)
    {
        if (deviceId == Guid.Empty)
            throw new ArgumentException("Select a playback device first.", nameof(deviceId));

        var lines = FilterLines(filters);
        Update((original, block) =>
        {
            var userConfig = RemoveBlock(original, block);
            VerifyCompleteConditionals(userConfig);
            var newline = userConfig.Contains("\r\n", StringComparison.Ordinal) || !userConfig.Contains('\n') ? "\r\n" : "\n";
            var managed = newline + Begin + newline +
                $"Device: {deviceId:B}" + newline +
                "Stage: post-mix" + newline +
                "Channel: all" + newline +
                ManualGainTag + newline +
                (lines.Length == 0 ? "" : string.Join(newline, lines) + newline) +
                End + newline;

            // Keeping the block last avoids changing device, stage, or channel selection for user filters.
            return userConfig + managed;
        }, backupOnAttach: true);
    }

    public void Disconnect() => Update(RemoveBlock, backupOnAttach: false);

    public void UpgradeLegacyGain()
    {
        Update((original, block) =>
        {
            if (block is not { UsesManualGain: false } current)
                return original;

            // Older versions wrote their negative automatic preamp immediately after Channel.
            // Edit only that exact line within the validated block, without reserializing other settings.
            var managed = original[current.MarkerStart..current.Last];
            var upgraded = LegacyGainLine.Replace(managed, match =>
                match.Groups["channels"].Value + ManualGainTag + match.Groups["newline"].Value, 1);
            return string.Concat(original.AsSpan(0, current.MarkerStart), upgraded.AsSpan(), original.AsSpan(current.Last));
        }, backupOnAttach: false);
    }

    private void Update(Func<string, Block?, string> transform, bool backupOnAttach)
    {
        lock (WriteGate)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var original = ReadConfig();
                    var block = FindBlock(original);
                    var updated = transform(original, block);
                    if (updated == original)
                        return;

                    if (backupOnAttach && block is null)
                    {
                        var backup = ConfigPath + ".zero-equalizer-backup";
                        if (!File.Exists(backup))
                            WriteAtomic(backup, original, replace: false);
                    }

                    WriteAtomic(ConfigPath, updated, replace: true, expected: original);
                    return;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 4)
                {
                    // Equalizer APO briefly denies replacement while reading its configuration.
                    Thread.Sleep(25);
                }
            }
        }
    }

    private string ReadConfig()
    {
        using var file = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("The Equalizer APO configuration is too large to edit.");

        using var buffer = new MemoryStream((int)file.Length);
        file.CopyTo(buffer);
        // APO accepts UTF-8 and ANSI. Latin-1 is used only as a reversible byte-to-character mapping.
        var text = Encoding.Latin1.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (text.Contains('\0'))
            throw new InvalidDataException("The Equalizer APO configuration must be a UTF-8 or ANSI text file.");
        return text;
    }

    private static string[] FilterLines(string filters)
    {
        var lines = filters.ReplaceLineEndings("\n").Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        VerifyCompleteConditionals(string.Join('\n', lines));
        foreach (var line in lines)
        {
            if ((line.StartsWith('#') && line != Begin && line != End) ||
                line.StartsWith("Preamp:", StringComparison.Ordinal) ||
                line.StartsWith("Filter:", StringComparison.Ordinal) ||
                line is "If: sampleRate > 8000" or "If: sampleRate > 10000" or
                    "If: sampleRate > 16000" or "If: sampleRate > 32000" or "EndIf:")
            {
                if (line.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0)
                    continue;
            }

            throw new InvalidDataException("Unexpected command in the Zero Equalizer filter settings.");
        }
        return lines;
    }

    private static Block? FindBlock(string text)
    {
        var start = text.IndexOf(Begin, StringComparison.OrdinalIgnoreCase);
        var end = text.IndexOf(End, StringComparison.OrdinalIgnoreCase);
        if (start < 0 && end < 0)
            return null;

        if (start < 0 || end < start + Begin.Length ||
            text.LastIndexOf(Begin, StringComparison.OrdinalIgnoreCase) != start ||
            text.LastIndexOf(End, StringComparison.OrdinalIgnoreCase) != end ||
            !text.AsSpan(start, Begin.Length).SequenceEqual(Begin) ||
            !text.AsSpan(end, End.Length).SequenceEqual(End) ||
            !IsOwnLine(text, start, Begin.Length) || !IsOwnLine(text, end, End.Length))
            throw new InvalidDataException("The Zero Equalizer block in config.txt is incomplete. Restore it from the backup or remove the damaged block before connecting.");

        var body = text[(start + Begin.Length)..end].ReplaceLineEndings("\n").Trim('\n').Split('\n');
        if (body.Length < 3 || !body[0].StartsWith("Device: ", StringComparison.Ordinal) ||
            !Guid.TryParseExact(body[0].AsSpan("Device: ".Length), "B", out var device) || device == Guid.Empty ||
            body[1] != "Stage: post-mix" || body[2] != "Channel: all")
            throw new InvalidDataException("The Zero Equalizer block in config.txt has been edited. Remove the edited block before connecting.");
        FilterLines(string.Join("\n", body, 3, body.Length - 3));

        var first = start;
        if (first > 0 && text[first - 1] == '\n')
        {
            first--;
            if (first > 0 && text[first - 1] == '\r')
                first--;
        }
        var last = end + End.Length;
        if (last < text.Length && text[last] == '\r')
            last++;
        if (last < text.Length && text[last] == '\n')
            last++;
        return new Block(first, start, last, device, Array.IndexOf(body, ManualGainTag) >= 0);
    }

    private static bool IsOwnLine(string text, int offset, int length)
    {
        var end = offset + length;
        return (offset == 0 || text[offset - 1] == '\n') &&
            (end == text.Length || text[end] == '\n' ||
                (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n'));
    }

    private static string RemoveBlock(string text, Block? block)
    {
        if (block is not { } current)
            return text;

        var start = current.First;
        if (current.Last < text.Length && start > 0 && text[start - 1] != '\n')
            start = current.MarkerStart;
        return text.Remove(start, current.Last - start);
    }

    private static void VerifyCompleteConditionals(string config)
    {
        var depth = 0;
        using var reader = new StringReader(config);
        while (reader.ReadLine() is { } line)
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var command = line.AsSpan(0, colon).Trim();
            if (command.SequenceEqual("If"))
                depth++;
            else if (command.SequenceEqual("EndIf") && --depth < 0)
                break;
        }

        if (depth != 0)
            throw new InvalidDataException("Equalizer APO config.txt contains an unmatched If or EndIf command. Correct it before connecting.");
    }

    private void WriteAtomic(string path, string text, bool replace, string? expected = null)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".zero-equalizer-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, Encoding.Latin1.GetBytes(text));
            if (replace)
            {
                if (ReadConfig() != expected)
                    throw new IOException("Equalizer APO configuration changed while saving. Try again.");
                File.Replace(temp, path, null);
            }
            else
            {
                try
                {
                    File.Move(temp, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // A concurrent first attach already created the backup.
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private readonly record struct Block(int First, int MarkerStart, int Last, Guid Device, bool UsesManualGain);
}
