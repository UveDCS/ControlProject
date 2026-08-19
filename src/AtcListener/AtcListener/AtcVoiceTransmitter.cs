using System.Speech.Synthesis;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Opus;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Opus.Core;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network.Client;
using NLog;

namespace AtcListener;

// Genera TTS y lo envia por el MISMO cliente/GUID que usamos para escuchar.
// Importante: si usaramos un GUID distinto (como el exe externo DCS-SR-ExternalAudio),
// el servidor SRS no sabe que es "nuestra propia voz" y nos la reenviaria, causando que
// el reconocedor se escuche a si mismo (bug real observado en pruebas).
public sealed class AtcVoiceTransmitter : IDisposable
{
    private const int SampleRate = 16000; // debe coincidir con Constants.MIC_SAMPLE_RATE de SRS
    private const int FrameMs = 40;
    private const int FrameBytes = SampleRate / 1000 * FrameMs * 2; // 640 muestras * 2 bytes = 1280

    private readonly SpeechSynthesizer _synth = new();
    private readonly OpusEncoder _encoder = OpusEncoder.Create(SampleRate, 1, Application.Voip);
    private readonly UDPVoiceHandler _udpHandler;
    private readonly uint _unitId;
    private ulong _packetNumber;

    public AtcVoiceTransmitter(UDPVoiceHandler udpHandler, uint unitId, string voiceName)
    {
        _udpHandler = udpHandler;
        _unitId = unitId;

        try
        {
            _synth.SelectVoice(voiceName);
        }
        catch (Exception)
        {
            // Se queda con la voz por defecto del sistema si el nombre no existe
        }
    }

    // freqHz/modulation se indican por llamada - un mismo transmisor sirve a varios aerodromos.
    public async Task SpeakAsync(string text, double freqHz, Modulation modulation, Logger logger)
    {
        logger.Info($"[TX] Respondiendo ({freqHz / 1_000_000.0:0.000} MHz {modulation}): \"{text}\"");

        using var ms = new MemoryStream();
        _synth.SetOutputToAudioStream(ms,
            new System.Speech.AudioFormat.SpeechAudioFormatInfo(SampleRate,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                System.Speech.AudioFormat.AudioChannel.Mono));
        _synth.Speak(text);
        _synth.SetOutputToNull();

        var pcm = ms.ToArray(); // SetOutputToAudioStream ya da PCM crudo, sin cabecera WAV
        if (pcm.Length == 0) return;

        // Temporizador periodico en vez de Task.Delay en bucle: Task.Delay acumula deriva
        // (el trabajo de cada vuelta tarda algo, asi que "40ms de delay" se convierte en
        // "40ms + lo que tarde el resto" real) y eso generaba huecos entre paquetes que el
        // cliente SRS interpretaba como sueltas de PTT (sonido entrecortado, confirmado en pruebas).
        using var frameTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(FrameMs));

        var frameBuf = new byte[FrameBytes];
        for (var offset = 0; offset < pcm.Length; offset += FrameBytes)
        {
            var bytesAvailable = Math.Min(FrameBytes, pcm.Length - offset);
            Buffer.BlockCopy(pcm, offset, frameBuf, 0, bytesAvailable);
            if (bytesAvailable < FrameBytes)
                Array.Clear(frameBuf, bytesAvailable, FrameBytes - bytesAvailable); // rellena el ultimo frame con silencio

            var encoded = _encoder.Encode(frameBuf, frameBuf.Length, out var encodedLength);
            if (encodedLength > 0)
            {
                var packet = new UDPVoicePacket
                {
                    AudioPart1Bytes = encoded[..encodedLength],
                    AudioPart1Length = (ushort)encodedLength,
                    Frequencies = [freqHz],
                    Modulations = [(byte)modulation],
                    Encryptions = [0],
                    UnitId = _unitId,
                    RetransmissionCount = 0,
                    PacketNumber = ++_packetNumber
                };

                _udpHandler.Send(packet);
            }

            await frameTimer.WaitForNextTickAsync();
        }
    }

    public void Dispose()
    {
        _synth.Dispose();
        _encoder.Dispose();
    }
}
