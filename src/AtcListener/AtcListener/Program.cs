// Fase 0 - Prueba de concepto: escuchar audio entrante en SRS via External AWACS Mode (EAM),
// decodificar Opus -> PCM, reconocer una gramatica minima con Windows Speech Recognition,
// y responder por voz sobre la misma frecuencia. Cierra el bucle completo escuchar->entender->hablar.

using System.Net;
using System.Speech.Recognition;
using System.Text;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Opus.Core;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.EventMessages;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network.Client;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network.Singletons;
using Caliburn.Micro;
using NLog;
using AtcListener;
using LogManager = NLog.LogManager;

var config = new NLog.Config.LoggingConfiguration();
var logconsole = new NLog.Targets.ConsoleTarget("logconsole")
{
    Layout = "${longdate}|${level:uppercase=true}|${message}"
};
config.AddRule(LogLevel.Info, LogLevel.Fatal, logconsole);
LogManager.Configuration = config;
var logger = LogManager.GetCurrentClassLogger();

const double TunedFrequencyHz = 251_000_000; // 251.0 MHz
const Modulation TunedModulation = Modulation.AM;
const string EamPassword = "atc123"; // debe coincidir con EXTERNAL_AWACS_MODE_BLUE_PASSWORD en server.cfg
const int Coalition = 2; // 1 = Red, 2 = Blue
const int Port = 5002;
const int VoiceSampleRate = 16000; // misma tasa que usa SRS para voz (Constants.MIC_SAMPLE_RATE)

var guid = ShortGuid.NewGuid();
var endpoint = new IPEndPoint(IPAddress.Loopback, Port);

var radioInfo = new PlayerRadioInfoBase { unitId = 100001 };
radioInfo.radios[1].freq = TunedFrequencyHz;
radioInfo.radios[1].modulation = TunedModulation;

var srClient = new SRClientBase
{
    ClientGuid = guid,
    Name = "ATC-GCI-Listener",
    Coalition = Coalition,
    AllowRecord = false,
    LatLngPosition = new LatLngPosition { lat = 33.5, lng = 36.3, alt = 500 },
    RadioInfo = radioInfo
};

if (args.Contains("--test-grpc"))
{
    using var dcs = new DcsWorldClient();
    try
    {
        var time = await dcs.GetScenarioCurrentTimeAsync();
        logger.Info($"[GRPC] Conectado. Hora del escenario: {time}");

        var (heading, strength) = await dcs.GetWindAsync(33.5, 36.3, 500);
        logger.Info($"[GRPC] Viento en (33.5, 36.3): rumbo {heading:F0} grados, {strength:F1} m/s");
    }
    catch (Exception ex)
    {
        logger.Error($"[GRPC] No se pudo conectar - ¿hay una mision corriendo con DCS-gRPC activo? Detalle: {ex.Message}");
    }

    return;
}

var recognizer = SpeechSetup.CreateRecognizer(logger);
if (recognizer == null)
{
    logger.Error("No hay ningun motor de reconocimiento de voz instalado en Windows. Instala uno desde Configuracion > Hora e idioma > Voz.");
    return;
}

if (args.Contains("--test-grammar"))
{
    string[] samples =
    [
        "viper uno solicito rodaje",
        "viper dos listo para despegue",
        "viper uno listo para despegue",
        "enfield uno uno solicito rodaje",
        "enfield uno uno solicito aproximacion",
        "enfield uno uno en final"
    ];

    foreach (var sample in samples)
    {
        var r = recognizer.EmulateRecognize(sample);
        if (r == null)
        {
            logger.Info($"[TEST] \"{sample}\" -> SIN MATCH");
            continue;
        }

        var cs = r.Semantics.ContainsKey("callsign") ? r.Semantics["callsign"].Value as string : "(null)";
        var it = r.Semantics.ContainsKey("intent") ? r.Semantics["intent"].Value as string : "(null)";
        logger.Info($"[TEST] \"{sample}\" -> texto=\"{r.Text}\" callsign={cs} intent={it}");
    }

    return;
}

var listener = new AtcListenerClient(guid, srClient, endpoint, EamPassword, TunedFrequencyHz, TunedModulation,
    VoiceSampleRate, recognizer, logger);
await listener.RunAsync();

// ---

internal static class SpeechSetup
{
    public static SpeechRecognitionEngine? CreateRecognizer(Logger logger)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        logger.Info($"Motores de reconocimiento instalados: {string.Join(", ", installed.Select(r => $"{r.Culture} ({r.Name})"))}");

        var chosen = installed.FirstOrDefault(r => r.Culture.Name == "en-US")
                     ?? installed.FirstOrDefault(r => r.Culture.Name.StartsWith("es"))
                     ?? installed.FirstOrDefault();

        if (chosen == null) return null;

        logger.Info($"Usando motor: {chosen.Culture} - {chosen.Name}");

        var engine = new SpeechRecognitionEngine(chosen);
        engine.LoadGrammar(AtcGrammar.Build(chosen.Culture));

        return engine;
    }
}

internal static class WavHelper
{
    public static byte[] BuildWav(byte[] pcm16Mono, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm16Mono.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm16Mono.Length);
        bw.Write(pcm16Mono);
        bw.Flush();

        return ms.ToArray();
    }
}

public class AtcListenerClient(
    string guid,
    SRClientBase srClient,
    IPEndPoint endpoint,
    string eamPassword,
    double tunedFrequencyHz,
    Modulation tunedModulation,
    int voiceSampleRate,
    SpeechRecognitionEngine recognizer,
    Logger logger)
    : IHandle<TCPClientStatusMessage>
{
    private static readonly TimeSpan TransmissionEndGap = TimeSpan.FromMilliseconds(500);

    private TCPClientHandler? _tcpHandler;
    private UDPVoiceHandler? _udpHandler;
    private readonly TaskCompletionSource _stopped = new();

    private readonly OpusDecoder _decoder = OpusDecoder.Create(16000, 1);
    private readonly MemoryStream _pcmBuffer = new();
    private readonly object _bufferLock = new();
    private DateTime _lastPacketAt = DateTime.MinValue;
    private bool _hasAudio;
    private readonly DcsWorldClient _worldClient = new();

    private readonly AtcStateMachine _stateMachine = new(
        srClient.LatLngPosition.lat, srClient.LatLngPosition.lng, srClient.LatLngPosition.alt, logger);

    private AtcVoiceTransmitter? _voiceTransmitter;

    public async Task RunAsync()
    {
        EventBus.Instance.SubscribeOnUIThread(this);

        _tcpHandler = new TCPClientHandler(guid, srClient);
        _tcpHandler.TryConnect(endpoint);

        var listenTask = Task.Run(ListenForVoiceAsync);
        var finalizeTask = Task.Run(WatchForTransmissionEndAsync);

        await _stopped.Task;
        await Task.WhenAll(listenTask, finalizeTask);
    }

    public async Task HandleAsync(TCPClientStatusMessage message, CancellationToken cancellationToken)
    {
        if (message.Connected)
        {
            logger.Info("TCP conectado - solicitando autenticacion EAM (coalicion Azul)...");
            await EventBus.Instance.PublishOnUIThreadAsync(new EAMConnectRequestMessage
            {
                Password = eamPassword,
                Name = srClient.Name
            });

            await Task.Delay(500);

            logger.Info($"Anunciando radio sintonizada: {tunedFrequencyHz / 1_000_000.0:0.000} MHz {tunedModulation}");

            _udpHandler = new UDPVoiceHandler(guid, endpoint);
            _udpHandler.Connect();

            _voiceTransmitter = new AtcVoiceTransmitter(_udpHandler, tunedFrequencyHz, tunedModulation,
                srClient.RadioInfo.unitId, "Microsoft Helena Desktop");
        }
        else
        {
            logger.Info("Desconectado del servidor.");
            _stopped.TrySetResult();
        }
    }

    private async Task ListenForVoiceAsync()
    {
        while (_udpHandler == null)
        {
            await Task.Delay(100);
        }

        logger.Info("Escuchando paquetes de voz entrantes...");

        foreach (var raw in _udpHandler.EncodedAudio.GetConsumingEnumerable())
        {
            var packet = UDPVoicePacket.DecodeVoicePacket(raw);
            if (packet == null || packet.AudioPart1Bytes == null) continue;

            var matches = false;
            for (var i = 0; i < packet.Frequencies.Length; i++)
            {
                if (RadioBase.FreqCloseEnough(packet.Frequencies[i], tunedFrequencyHz)
                    && (Modulation)packet.Modulations[i] == tunedModulation)
                {
                    matches = true;
                    break;
                }
            }

            if (!matches) continue;

            var decoded = _decoder.Decode(packet.AudioPart1Bytes, packet.AudioPart1Length, out var decodedLength);
            if (decodedLength <= 0) continue;

            lock (_bufferLock)
            {
                _pcmBuffer.Write(decoded, 0, decodedLength);
                _lastPacketAt = DateTime.UtcNow;
                _hasAudio = true;
            }
        }
    }

    private async Task WatchForTransmissionEndAsync()
    {
        while (!_stopped.Task.IsCompleted)
        {
            await Task.Delay(150);

            byte[]? pcmToRecognize = null;

            lock (_bufferLock)
            {
                if (_hasAudio && DateTime.UtcNow - _lastPacketAt > TransmissionEndGap)
                {
                    pcmToRecognize = _pcmBuffer.ToArray();
                    _pcmBuffer.SetLength(0);
                    _hasAudio = false;
                }
            }

            if (pcmToRecognize is { Length: > 0 })
            {
                await RecognizeAndRespondAsync(pcmToRecognize);
            }
        }
    }

    private async Task RecognizeAndRespondAsync(byte[] pcm16Mono)
    {
        logger.Info($"Fin de transmision detectado ({pcm16Mono.Length} bytes PCM) - reconociendo...");

        var wavBytes = WavHelper.BuildWav(pcm16Mono, voiceSampleRate);
        using var wavStream = new MemoryStream(wavBytes);

        recognizer.SetInputToWaveStream(wavStream);
        var result = recognizer.Recognize();
        recognizer.SetInputToNull();

        if (result == null)
        {
            logger.Info("[STT] No se reconocio ninguna frase de la gramatica.");
            return;
        }

        var callsign = result.Semantics.ContainsKey("callsign") ? result.Semantics["callsign"].Value as string : null;
        var intentValue = result.Semantics.ContainsKey("intent") ? result.Semantics["intent"].Value as string : null;

        if (callsign == null || intentValue == null || !Enum.TryParse<AtcIntent>(intentValue, out var intent))
        {
            logger.Info($"[STT] Reconocido pero sin semantica valida: \"{result.Text}\"");
            return;
        }

        logger.Info($"[STT] {callsign} -> {intent} (texto: \"{result.Text}\", confianza {result.Confidence:P0})");

        var response = await _stateMachine.HandleAsync(callsign, intent, _worldClient);

        if (_voiceTransmitter == null)
        {
            logger.Error("Transmisor de voz no listo todavia - respuesta perdida.");
            return;
        }

        await _voiceTransmitter.SpeakAsync(response, logger);
    }
}
