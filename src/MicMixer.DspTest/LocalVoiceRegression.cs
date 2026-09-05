using System.Security.Cryptography;
using System.Text.Json;
using MicMixer.Dsp;
using NAudio.Wave;

internal static class LocalVoiceRegression
{
    internal sealed record Manifest(string ProfilePath, string ProfileSha256, Case[] Cases);
    internal sealed record Case(string RawPath, string RawSha256, string BeforePath, string BeforeSha256,
        string? ApprovedPath, string? ApprovedSha256, bool AlternateWindow, float ChunkMilliseconds);

    public static int Run(string path)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), VoiceProfileStore.JsonOptions)
            ?? throw new InvalidDataException("Private regression manifest is empty.");
        if (manifest.Cases is not { Length: > 0 }) throw new InvalidDataException("No private regression cases provided.");
        CheckFile(manifest.ProfilePath, manifest.ProfileSha256);
        var profile = VoiceProfileStore.ReadFile(manifest.ProfilePath);
        foreach (var item in manifest.Cases)
        {
            CheckFile(item.RawPath, item.RawSha256);
            CheckFile(item.BeforePath, item.BeforeSha256);
            if (item.ApprovedPath != null) CheckFile(item.ApprovedPath, item.ApprovedSha256!);
            if (!float.IsFinite(item.ChunkMilliseconds) || item.ChunkMilliseconds <= 0)
                throw new InvalidDataException("Invalid private callback size.");
            string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Guid.NewGuid() + ".wav");
            try
            {
                DspTestApplication.ProcessFile(new CommandLineOptions
                {
                    InputPath = item.RawPath, OutputPath = output, PresetPath = manifest.ProfilePath,
                    BlockMilliseconds = profile.Resolve(item.AlternateWindow).BlockMilliseconds,
                    ChunkMilliseconds = item.ChunkMilliseconds
                });
                Compare(output, item.BeforePath, 0);
                if (item.ApprovedPath != null) Compare(output, item.ApprovedPath, 1d / 8_388_608);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
        Console.WriteLine($"Private regression passed: {manifest.Cases.Length} cases; exact before/after samples.");
        return 0;
    }

    private static void CheckFile(string path, string expected)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required private regression data is missing.", path);
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Private regression data hash mismatch: {path}");
    }

    private static void Compare(string actual, string expected, double tolerance)
    {
        using var a = new AudioFileReader(actual);
        using var b = new AudioFileReader(expected);
        if (a.WaveFormat.SampleRate != b.WaveFormat.SampleRate || a.WaveFormat.Channels != b.WaveFormat.Channels)
            throw new InvalidDataException("Regression audio format mismatch.");
        var x = new float[4096]; var y = new float[4096];
        double maximum = 0; double sum = 0; long count = 0;
        while (true)
        {
            int n = a.Read(x.AsSpan()); int m = b.Read(y.AsSpan());
            if (n != m) throw new InvalidDataException("Regression audio length mismatch.");
            if (n == 0) break;
            for (int i=0;i<n;i++)
            {
                double difference = Math.Abs((double)x[i]-y[i]);
                if (!double.IsFinite(difference) || difference > tolerance)
                    throw new InvalidDataException($"Audio regression failed at sample {count+i}: {difference:R} > {tolerance:R}");
                maximum = Math.Max(maximum,difference); sum += difference*difference;
            }
            count += n;
        }
        if (count == 0) throw new InvalidDataException("Regression audio is empty.");
        Console.WriteLine($"Compared {count} samples: max={maximum:R}, RMS={Math.Sqrt(sum/count):R}");
    }
}
