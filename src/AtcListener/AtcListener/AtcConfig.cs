using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtcListener;

// Configuracion editable del programa. Pensada para poder compartir el programa
// sin tener que tocar codigo: servidor SRS, servidor DCS-gRPC, voz, callsigns
// conocidos, y la lista de aerodromos que este ATC controla (cada uno con su
// propia frecuencia - DCS no expone una frecuencia de torre "oficial" por
// aerodromo, asi que la asignamos aqui nosotros, como hace cualquier servidor).
public class AtcConfig
{
    public SrsConfig Srs { get; set; } = new();
    public GrpcConfig Grpc { get; set; } = new();
    public string Voice { get; set; } = "Microsoft Helena Desktop";
    public string[] Callsigns { get; set; } = ["viper uno", "viper dos", "enfield uno uno"];

    // Asigna un callsign fijo a un jugador de multijugador por su nombre real -
    // asi un nick raro no es un problema, y ademas nos deja saber a que jugador
    // real corresponde ese callsign para poder rastrear su posicion (guiado en
    // aproximacion). Requiere DISTANCE_ENABLED=true (o LOS_ENABLED) en el server.cfg
    // de SRS, si no todas las posiciones llegan como 0,0 por privacidad.
    public List<PlayerCallsignConfig> PlayerCallsigns { get; set; } =
    [
        new PlayerCallsignConfig { PlayerName = "Uve", Callsign = "Sierra 7-1" }
    ];
    public List<AirbaseConfig> Airbases { get; set; } =
    [
        new AirbaseConfig
        {
            Name = "EJEMPLO - cambia esto por un nombre real de --list-airbases",
            FrequencyMhz = 251.0,
            Modulation = "AM",
            TaxiRoute = "alfa",
            Runways =
            [
                new RunwayConfig { Name = "dos uno", HeadingDeg = 210 },
                new RunwayConfig { Name = "cero tres", HeadingDeg = 30 }
            ]
        }
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AtcConfig LoadOrCreateDefault(string path, NLog.Logger logger)
    {
        if (!File.Exists(path))
        {
            var defaultConfig = new AtcConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaultConfig, JsonOptions));
            logger.Info($"No existia configuracion - creado archivo de ejemplo en: {path}. Editalo y vuelve a arrancar.");
            return defaultConfig;
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AtcConfig>(json, JsonOptions)
                     ?? throw new InvalidOperationException($"No se pudo leer la configuracion de {path}");

        logger.Info($"Configuracion cargada de {path}: {config.Airbases.Count} aerodromo(s)");
        return config;
    }
}

public class SrsConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5002;
    public string EamPassword { get; set; } = "atc123";
    public int Coalition { get; set; } = 2; // 1 = Rojo, 2 = Azul
}

public class GrpcConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 50051;
}

public class AirbaseConfig
{
    public string Name { get; set; } = "";
    public double FrequencyMhz { get; set; }
    public string Modulation { get; set; } = "AM";
    public string TaxiRoute { get; set; } = "alfa";

    // DCS-gRPC no expone las pistas fisicas de un aerodromo, asi que las declaramos
    // aqui con su rumbo real (se puede sacar de cualquier carta de navegacion). El
    // programa elige la mas alineada con el viento real de la mision en cada momento.
    public List<RunwayConfig> Runways { get; set; } = [new RunwayConfig { Name = "dos uno", HeadingDeg = 210 }];
}

public class RunwayConfig
{
    public string Name { get; set; } = "";
    public double HeadingDeg { get; set; }
}

public class PlayerCallsignConfig
{
    public string PlayerName { get; set; } = "";
    public string Callsign { get; set; } = "";
}
