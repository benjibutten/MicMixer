using System.Text.Json.Serialization;

namespace MicMixer.Dsp;

[JsonConverter(typeof(JsonStringEnumConverter<PitchEngine>))]
public enum PitchEngine
{
    Signalsmith,
    TimeDomain
}
