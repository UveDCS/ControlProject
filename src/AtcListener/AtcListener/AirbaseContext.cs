using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Opus.Core;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using NLog;

namespace AtcListener;

// Estado en tiempo de ejecucion de UN aerodromo: su propia frecuencia, decodificador Opus
// (el estado del decoder no se puede compartir entre streams de audio distintos), buffer de
// audio en curso, y su propia maquina de estados. Así dos aerodromos nunca se interfieren
// aunque haya dos jugadores hablando por radios distintas a la vez.
public sealed class AirbaseContext(AirbaseConfig config, double lat, double lon, double alt, Logger logger)
{
    private static readonly TimeSpan TransmissionEndGap = TimeSpan.FromMilliseconds(500);

    public string Name => config.Name;
    public double FrequencyHz => config.FrequencyMhz * 1_000_000.0;
    public Modulation Modulation { get; } = Enum.Parse<Modulation>(config.Modulation, ignoreCase: true);
    public double Lat => lat;
    public double Lon => lon;

    public AtcStateMachine StateMachine { get; } =
        new(config.Runways, config.TaxiRoute, lat, lon, alt, logger);

    private readonly OpusDecoder _decoder = OpusDecoder.Create(16000, 1);
    private readonly MemoryStream _pcmBuffer = new();
    private readonly object _bufferLock = new();
    private DateTime _lastPacketAt = DateTime.MinValue;
    private bool _hasAudio;

    public void AppendAudio(byte[] opusBytes, int opusLength)
    {
        var decoded = _decoder.Decode(opusBytes, opusLength, out var decodedLength);
        if (decodedLength <= 0) return;

        lock (_bufferLock)
        {
            _pcmBuffer.Write(decoded, 0, decodedLength);
            _lastPacketAt = DateTime.UtcNow;
            _hasAudio = true;
        }
    }

    // Si ha pasado el hueco de silencio suficiente, devuelve el audio acumulado y limpia el buffer.
    public byte[]? TakeCompletedTransmission()
    {
        lock (_bufferLock)
        {
            if (_hasAudio && DateTime.UtcNow - _lastPacketAt > TransmissionEndGap)
            {
                var pcm = _pcmBuffer.ToArray();
                _pcmBuffer.SetLength(0);
                _hasAudio = false;
                return pcm;
            }
        }

        return null;
    }
}
