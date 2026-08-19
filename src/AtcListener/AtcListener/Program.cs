// Escucha audio entrante en SRS via External AWACS Mode (EAM) para uno o varios aerodromos
// a la vez (cada uno en su propia frecuencia), decodifica Opus -> PCM, reconoce fraseologia
// ATC con Windows Speech Recognition, y responde por voz en la frecuencia de ese aerodromo.
// La configuracion (servidor SRS, DCS-gRPC, voz, callsigns, aerodromos) es editable en
// atc-config.json, junto al ejecutable.

using System.Net;
using System.Speech.Recognition;
using System.Text;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.EventMessages;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network.Client;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network.Singletons;
using Caliburn.Micro;
using NLog;
using AtcListener;
using LogManager = NLog.LogManager;

var loggingConfig = new NLog.Config.LoggingConfiguration();
var logconsole = new NLog.Targets.ConsoleTarget("logconsole")
{
    Layout = "${longdate}|${level:uppercase=true}|${message}"
};
loggingConfig.AddRule(LogLevel.Info, LogLevel.Fatal, logconsole);
LogManager.Configuration = loggingConfig;
var logger = LogManager.GetCurrentClassLogger();

const int VoiceSampleRate = 16000; // misma tasa que usa SRS para voz (Constants.MIC_SAMPLE_RATE)
const int MaxAirbases = 10; // Constants.MAX_RADIOS de SRS es 11 (radios[0] va sin usar)

var configPath = Path.Combine(AppContext.BaseDirectory, "atc-config.json");
var config = AtcConfig.LoadOrCreateDefault(configPath, logger);

if (args.Contains("--list-airbases"))
{
    using var dcs = new DcsWorldClient(config.Grpc.Host, config.Grpc.Port);
    try
    {
        var airbases = await dcs.GetAirbasesAsync();
        foreach (var ab in airbases.OrderBy(a => a.Name))
        {
            logger.Info($"[AIRBASE] {ab.Name} | coalicion={ab.Coalition} | lat={ab.Position.Lat:F4} lon={ab.Position.Lon:F4} alt={ab.Position.Alt:F0}");
        }
        logger.Info($"Total: {airbases.Count} aerodromos");
    }
    catch (Exception ex)
    {
        logger.Error($"[GRPC] No se pudo conectar - ¿hay una mision corriendo con DCS-gRPC activo? Detalle: {ex.Message}");
    }

    return;
}

if (args.Contains("--test-grpc"))
{
    using var dcs = new DcsWorldClient(config.Grpc.Host, config.Grpc.Port);
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

if (config.Airbases.Count > MaxAirbases)
{
    logger.Warn($"La configuracion tiene {config.Airbases.Count} aerodromos, pero SRS solo admite {MaxAirbases} radios simultaneas por cliente. Se usaran solo los primeros {MaxAirbases}.");
}

var worldClient = new DcsWorldClient(config.Grpc.Host, config.Grpc.Port);
var airbaseContexts = new List<AirbaseContext>();

try
{
    var realAirbases = await worldClient.GetAirbasesAsync();

    foreach (var abConfig in config.Airbases.Take(MaxAirbases))
    {
        var match = realAirbases.FirstOrDefault(a =>
            string.Equals(a.Name, abConfig.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.DisplayName, abConfig.Name, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            logger.Info($"Aerodromo '{abConfig.Name}' resuelto en la mision -> {match.Name} ({match.Position.Lat:F4}, {match.Position.Lon:F4})");
            airbaseContexts.Add(new AirbaseContext(abConfig, match.Position.Lat, match.Position.Lon, match.Position.Alt, logger));
        }
        else
        {
            logger.Warn($"No se encontro '{abConfig.Name}' en la mision actual - el viento de ese aerodromo caera a 'viento calma'. Revisa el nombre en {configPath} (usa --list-airbases para ver los nombres reales).");
            airbaseContexts.Add(new AirbaseContext(abConfig, 0, 0, 0, logger));
        }
    }
}
catch (Exception ex)
{
    logger.Warn($"No se pudo conectar a DCS-gRPC para resolver aerodromos ({ex.Message}). El ATC seguira funcionando, pero el viento sera siempre 'viento calma' hasta que haya una mision corriendo.");
    foreach (var abConfig in config.Airbases.Take(MaxAirbases))
        airbaseContexts.Add(new AirbaseContext(abConfig, 0, 0, 0, logger));
}

if (airbaseContexts.Count == 0)
{
    logger.Error($"No hay ningun aerodromo configurado en {configPath}. Añade al menos uno y vuelve a arrancar.");
    return;
}

var livePlayerNames = Array.Empty<string>();
try
{
    livePlayerNames = (await worldClient.GetConnectedPlayerNamesAsync()).ToArray();
    logger.Info(livePlayerNames.Length > 0
        ? $"Jugadores conectados detectados como callsigns: {string.Join(", ", livePlayerNames)}"
        : "No hay jugadores conectados todavia - se usaran solo los callsigns fijos de la configuracion.");
}
catch (Exception ex)
{
    logger.Warn($"No se pudo obtener la lista de jugadores via DCS-gRPC ({ex.Message}) - se usaran solo los callsigns fijos de la configuracion.");
}

var allCallsigns = config.Callsigns
    .Concat(livePlayerNames)
    .Select(CallsignNormalizer.ForGrammar)
    .Where(c => c.Length > 0)
    .Distinct()
    .ToArray();

var recognizer = SpeechSetup.CreateRecognizer(logger, allCallsigns);
if (recognizer == null)
{
    logger.Error("No hay ningun motor de reconocimiento de voz instalado en Windows. Instala uno desde Configuracion > Hora e idioma > Voz.");
    return;
}

if (args.Contains("--test-grammar"))
{
    var firstCallsign = allCallsigns.FirstOrDefault() ?? "viper uno";
    string[] samples =
    [
        $"{firstCallsign} solicito rodaje",
        $"{firstCallsign} listo para despegue",
        $"{firstCallsign} solicito aproximacion",
        $"{firstCallsign} en final",
        "viper uno uno solicito rodaje" // prueba: callsign configurado como "Viper 1-1" pronunciado como palabras
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

var guid = ShortGuid.NewGuid();
var endpoint = new IPEndPoint(IPAddress.Parse(config.Srs.Host), config.Srs.Port);

var radioInfo = new PlayerRadioInfoBase { unitId = 100001 };
for (var i = 0; i < airbaseContexts.Count; i++)
{
    radioInfo.radios[i + 1].freq = airbaseContexts[i].FrequencyHz;
    radioInfo.radios[i + 1].modulation = airbaseContexts[i].Modulation;
    logger.Info($"Radio {i + 1}: {airbaseContexts[i].Name} en {airbaseContexts[i].FrequencyHz / 1_000_000.0:0.000} MHz {airbaseContexts[i].Modulation}");
}

var srClient = new SRClientBase
{
    ClientGuid = guid,
    Name = "ATC-GCI",
    Coalition = config.Srs.Coalition,
    AllowRecord = false,
    LatLngPosition = new LatLngPosition(),
    RadioInfo = radioInfo
};

var listener = new AtcListenerClient(guid, srClient, endpoint, config.Srs.EamPassword, airbaseContexts,
    VoiceSampleRate, recognizer, worldClient, config.Voice, logger);
await listener.RunAsync();

// ---

internal static class SpeechSetup
{
    public static SpeechRecognitionEngine? CreateRecognizer(Logger logger, string[] callsigns)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        logger.Info($"Motores de reconocimiento instalados: {string.Join(", ", installed.Select(r => $"{r.Culture} ({r.Name})"))}");

        var chosen = installed.FirstOrDefault(r => r.Culture.Name == "en-US")
                     ?? installed.FirstOrDefault(r => r.Culture.Name.StartsWith("es"))
                     ?? installed.FirstOrDefault();

        if (chosen == null) return null;

        logger.Info($"Usando motor: {chosen.Culture} - {chosen.Name}");

        var engine = new SpeechRecognitionEngine(chosen);
        engine.LoadGrammar(AtcGrammar.Build(chosen.Culture, callsigns));

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
    List<AirbaseContext> airbases,
    int voiceSampleRate,
    SpeechRecognitionEngine recognizer,
    DcsWorldClient worldClient,
    string voiceName,
    Logger logger)
    : IHandle<TCPClientStatusMessage>
{
    private TCPClientHandler? _tcpHandler;
    private UDPVoiceHandler? _udpHandler;
    private readonly TaskCompletionSource _stopped = new();
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
            logger.Info("TCP conectado - solicitando autenticacion EAM...");
            await EventBus.Instance.PublishOnUIThreadAsync(new EAMConnectRequestMessage
            {
                Password = eamPassword,
                Name = srClient.Name
            });

            await Task.Delay(500);

            _udpHandler = new UDPVoiceHandler(guid, endpoint);
            _udpHandler.Connect();

            _voiceTransmitter = new AtcVoiceTransmitter(_udpHandler, srClient.RadioInfo.unitId, voiceName);

            logger.Info($"Listo. Controlando {airbases.Count} aerodromo(s).");
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

            var airbase = FindMatchingAirbase(packet);
            if (airbase == null) continue;

            airbase.AppendAudio(packet.AudioPart1Bytes, packet.AudioPart1Length);
        }
    }

    private AirbaseContext? FindMatchingAirbase(UDPVoicePacket packet)
    {
        for (var i = 0; i < packet.Frequencies.Length; i++)
        {
            var modulation = (Modulation)packet.Modulations[i];
            var airbase = airbases.FirstOrDefault(a =>
                RadioBase.FreqCloseEnough(packet.Frequencies[i], a.FrequencyHz) && a.Modulation == modulation);

            if (airbase != null) return airbase;
        }

        return null;
    }

    private async Task WatchForTransmissionEndAsync()
    {
        while (!_stopped.Task.IsCompleted)
        {
            await Task.Delay(150);

            foreach (var airbase in airbases)
            {
                var pcm = airbase.TakeCompletedTransmission();
                if (pcm is { Length: > 0 })
                {
                    await RecognizeAndRespondAsync(airbase, pcm);
                }
            }
        }
    }

    // Por debajo de esto se considera ruido/clic de PTT, no un intento real de hablar
    // (unos 300ms a 16kHz mono 16-bit) - evita que el ATC diga "repita" por cualquier ruido.
    private const int MinMeaningfulPcmBytes = 10_000;

    private async Task RecognizeAndRespondAsync(AirbaseContext airbase, byte[] pcm16Mono)
    {
        logger.Info($"[{airbase.Name}] Fin de transmision detectado ({pcm16Mono.Length} bytes PCM) - reconociendo...");

        var wavBytes = WavHelper.BuildWav(pcm16Mono, voiceSampleRate);
        using var wavStream = new MemoryStream(wavBytes);

        recognizer.SetInputToWaveStream(wavStream);
        var result = recognizer.Recognize();
        recognizer.SetInputToNull();

        string? callsign = null;
        AtcIntent? intent = null;

        if (result != null)
        {
            callsign = result.Semantics.ContainsKey("callsign") ? result.Semantics["callsign"].Value as string : null;
            var intentValue = result.Semantics.ContainsKey("intent") ? result.Semantics["intent"].Value as string : null;
            if (intentValue != null && Enum.TryParse<AtcIntent>(intentValue, out var parsedIntent))
                intent = parsedIntent;
        }

        if (callsign == null || intent == null)
        {
            logger.Info(result == null
                ? $"[{airbase.Name}] [STT] No se reconocio ninguna frase de la gramatica."
                : $"[{airbase.Name}] [STT] Reconocido pero sin semantica valida: \"{result!.Text}\"");

            if (pcm16Mono.Length < MinMeaningfulPcmBytes)
            {
                logger.Info($"[{airbase.Name}] Transmision demasiado corta para ser un intento real - se ignora sin responder.");
                return;
            }

            await SpeakAsync(airbase, "Repita, no se ha entendido");
            return;
        }

        logger.Info($"[{airbase.Name}] [STT] {callsign} -> {intent} (texto: \"{result!.Text}\", confianza {result.Confidence:P0})");

        var response = await airbase.StateMachine.HandleAsync(callsign, intent.Value, worldClient);
        await SpeakAsync(airbase, response);
    }

    private async Task SpeakAsync(AirbaseContext airbase, string text)
    {
        if (_voiceTransmitter == null)
        {
            logger.Error("Transmisor de voz no listo todavia - respuesta perdida.");
            return;
        }

        await _voiceTransmitter.SpeakAsync(text, airbase.FrequencyHz, airbase.Modulation, logger);
    }
}
