using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicMixer.Dsp;
using NAudio.Wave;

return DspTestApplication.Run(args);

internal static class DspTestApplication
{
    public static int Run(string[] args)
    {
        try
        {
            if (args is ["--verify-local", var manifest]) return LocalVoiceRegression.Run(manifest);
            CommandLineOptions options = CommandLineOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            ProcessFile(options);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 1;
        }
    }

    internal static void ProcessFile(CommandLineOptions options)
    {
        string inputPath = Path.GetFullPath(options.InputPath!);
        string outputPath = Path.GetFullPath(options.OutputPath!);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Input and output must be different files.");
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input WAV was not found.", inputPath);
        }

        if (!string.Equals(Path.GetExtension(inputPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The offline POC accepts WAV input only.");
        }

        AudioData source = ReadWave(inputPath);
        VoiceDspParameters preset = options.CreatePreset();
        using var processor = new ProfileVoiceProcessor(source.SampleRate, source.Channels, preset);

        int latencyFrames = processor.LatencySamples;
        int latencyInterleavedSamples = checked(latencyFrames * source.Channels);
        int renderedLength = checked(source.Samples.Length + latencyInterleavedSamples);
        var renderedOutput = new float[renderedLength];

        int chunkFrames = Math.Max(1, (int)MathF.Round(source.SampleRate * options.ChunkMilliseconds / 1_000f));
        int chunkSamples = checked(chunkFrames * source.Channels);

        // JIT and bind the native call before measuring the steady-state loop.
        // Reset makes this transparent to the actual offline render.
        int warmupSamples = Math.Min(chunkSamples, source.Samples.Length);
        processor.Process(source.Samples.AsSpan(0, warmupSamples), renderedOutput.AsSpan(0, warmupSamples));
        processor.Reset();

        var stopwatch = new Stopwatch();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int processingCallbacks = 0;
        stopwatch.Start();

        for (int offset = 0; offset < source.Samples.Length; offset += chunkSamples)
        {
            int count = Math.Min(chunkSamples, source.Samples.Length - offset);
            processor.Process(
                source.Samples.AsSpan(offset, count),
                renderedOutput.AsSpan(offset, count));
            processingCallbacks++;
        }

        stopwatch.Stop();
        long processingAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        processor.Flush(renderedOutput.AsSpan(source.Samples.Length, latencyInterleavedSamples));

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using (var writer = new WaveFileWriter(
            outputPath,
            WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate, source.Channels)))
        {
            // Streaming starts with Signalsmith's reported pre-roll. Removing the
            // complete input+output latency keeps this file aligned to raw.wav;
            // flushing above renders the remaining tail before trimming.
            ReadOnlySpan<float> alignedOutput = renderedOutput.AsSpan(latencyInterleavedSamples, source.Samples.Length);
            writer.Write(MemoryMarshal.AsBytes(alignedOutput));
        }

        double durationSeconds = source.Samples.Length / (double)(source.SampleRate * source.Channels);
        double latencyMilliseconds = latencyFrames * 1_000d / source.SampleRate;
        double realtimeFactor = stopwatch.Elapsed.TotalSeconds / Math.Max(durationSeconds, double.Epsilon);

        Console.WriteLine($"Input:             {inputPath}");
        Console.WriteLine($"Output:            {outputPath}");
        Console.WriteLine($"Format:            {source.SampleRate} Hz, {source.Channels} channel(s), {durationSeconds:F2} s");
        Console.WriteLine($"Pitch engine:      {preset.PitchEngine}");
        Console.WriteLine($"Pitch/formant:     {preset.PitchSemitones:+0.##;-0.##;0} / {preset.FormantSemitones:+0.##;-0.##;0} semitones");
        Console.WriteLine($"Tonality limit:    {preset.TonalityLimitHz:0.##} Hz");
        Console.WriteLine($"Analysis/interval: {processor.AnalysisBlockSamples} / {processor.ProcessingIntervalSamples} samples");
        Console.WriteLine($"Algorithm latency: {latencyFrames} samples ({latencyMilliseconds:F2} ms; input {processor.InputLatencySamples} + output {processor.OutputLatencySamples})");
        Console.WriteLine($"Processing time:   {stopwatch.Elapsed.TotalMilliseconds:F1} ms ({realtimeFactor:F3}x realtime)");
        Console.WriteLine($"Average callback:  {stopwatch.Elapsed.TotalMilliseconds / processingCallbacks:F3} ms ({processingCallbacks:N0} callbacks)");
        Console.WriteLine($"Managed loop alloc:{processingAllocations,10:N0} bytes");
        Console.WriteLine("Preset status:     CPU-only voice tuning; no AI/ML");
    }

    private static AudioData ReadWave(string path)
    {
        using var reader = new AudioFileReader(path);
        int estimatedSamples = checked((int)Math.Min(
            int.MaxValue,
            Math.Max(4_096L, reader.Length / sizeof(float))));
        var samples = new float[estimatedSamples];
        int count = 0;

        while (true)
        {
            if (count == samples.Length)
            {
                int expandedLength = checked((int)Math.Min(int.MaxValue, Math.Max(samples.Length + 1L, samples.Length * 2L)));
                Array.Resize(ref samples, expandedLength);
            }

            int read = reader.Read(samples.AsSpan(count));
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count == 0)
        {
            throw new InvalidDataException("Input WAV contains no audio samples.");
        }

        count -= count % reader.WaveFormat.Channels;
        if (count == 0)
        {
            throw new InvalidDataException("Input WAV does not contain one complete audio frame.");
        }

        Array.Resize(ref samples, count);
        return new AudioData(samples, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            MicMixer voice offline DSP prototype

            Usage:
              MicMixer.DspTest.exe raw.wav processed.wav [options]

            Options:
              --pitch <semitones>       Pitch shift (default: 0)
              --formant <semitones>     Independent formant shift (default: 0)
              --formant-base <hz>       Voice fundamental estimate; 0 = auto (default: 0)
              --tonality-hz <hz>        Preserve tonal components up to this frequency (default: 8000)
              --preset <json>           Load a versioned voice profile or DSP parameters JSON
              --block-ms <ms>           Signalsmith analysis block (default: 40)
              --interval-ms <ms>        Signalsmith processing interval (default: 10)
              --chunk-ms <ms>           Simulated audio callback size (default: 10)
              -h, --help                Show this help

            Local private regression (missing data is an error):
              MicMixer.DspTest.exe --verify-local <private-manifest.json>

            Output is a 32-bit float WAV with algorithmic pre-roll removed, so it
            remains sample-aligned with raw.wav for an A/B comparison.
            """);
    }

    private sealed record AudioData(float[] Samples, int SampleRate, int Channels);
}

internal sealed record CommandLineOptions
{
    public string? InputPath { get; init; }
    public string? OutputPath { get; init; }
    public bool ShowHelp { get; init; }
    public float? PitchSemitones { get; init; }
    public float? FormantSemitones { get; init; }
    public float? FormantBaseHz { get; init; }
    public float? TonalityLimitHz { get; init; }
    public string? PresetPath { get; init; }
    public float? BlockMilliseconds { get; init; }
    public float? IntervalMilliseconds { get; init; }
    public float ChunkMilliseconds { get; init; } = 10f;

    public static CommandLineOptions Parse(string[] args)
    {
        if (args.Length == 0 || args is ["-h"] or ["--help"])
        {
            return new CommandLineOptions { ShowHelp = true };
        }

        if (args.Length < 2)
        {
            throw new ArgumentException("Both input and output WAV paths are required.");
        }

        var result = new CommandLineOptions { InputPath = args[0], OutputPath = args[1] };
        for (int i = 2; i < args.Length; i++)
        {
            string option = args[i];
            if (option is "-h" or "--help")
            {
                return result with { ShowHelp = true };
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {option}.");
            }

            string valueText = args[++i];
            if (option == "--preset")
            {
                result = result with { PresetPath = valueText };
                continue;
            }

            float value = ParseFloat(valueText, option);
            result = option switch
            {
                "--pitch" => result with { PitchSemitones = value },
                "--formant" => result with { FormantSemitones = value },
                "--formant-base" => result with { FormantBaseHz = value },
                "--tonality-hz" => result with { TonalityLimitHz = value },
                "--block-ms" => result with { BlockMilliseconds = value },
                "--interval-ms" => result with { IntervalMilliseconds = value },
                "--chunk-ms" => result with { ChunkMilliseconds = Positive(value, option) },
                _ => throw new ArgumentException($"Unknown option: {option}")
            };
        }

        return result;
    }

    public VoiceDspParameters CreatePreset()
    {
        VoiceDspParameters preset = VoiceDspParameters.Initial;
        if (PresetPath != null)
        {
            string path = Path.GetFullPath(PresetPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Preset JSON was not found.", path);
            }

            using FileStream stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.EnumerateObject().Any(p => p.Name.Equals("parameters", StringComparison.OrdinalIgnoreCase)))
                return ApplyTo(VoiceProfileStore.ReadFile(path).Parameters);
            stream.Position = 0;
            preset = JsonSerializer.Deserialize<VoiceDspParameters>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                }) ?? throw new InvalidDataException("Preset JSON contained no object.");
        }

        return ApplyTo(preset);
    }

    private VoiceDspParameters ApplyTo(VoiceDspParameters preset)
    {
        return preset with
        {
            PitchSemitones = PitchSemitones ?? preset.PitchSemitones,
            FormantSemitones = FormantSemitones ?? preset.FormantSemitones,
            FormantBaseHz = FormantBaseHz ?? preset.FormantBaseHz,
            TonalityLimitHz = TonalityLimitHz ?? preset.TonalityLimitHz,
            BlockMilliseconds = BlockMilliseconds ?? preset.BlockMilliseconds,
            IntervalMilliseconds = IntervalMilliseconds ?? preset.IntervalMilliseconds
        };
    }

    private static float ParseFloat(string text, string option)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
        {
            throw new ArgumentException($"{option} requires a finite number using '.' as decimal separator.");
        }

        return value;
    }

    private static float Positive(float value, string option)
    {
        if (value <= 0f)
        {
            throw new ArgumentOutOfRangeException(option, value, "Value must be greater than zero.");
        }

        return value;
    }
}
