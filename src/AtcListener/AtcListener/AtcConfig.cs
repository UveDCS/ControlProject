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
    public List<AirbaseConfig> Airbases { get; set; } =
    [
        new AirbaseConfig
        {
            Name = "EJEMPLO - cambia esto por un nombre real de --list-airbases",
            FrequencyMhz = 251.0,
            Modulation = "AM",
            Runway = "dos uno",
            TaxiRoute = "alfa"
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
    public string Runway { get; set; } = "dos uno";
    public string TaxiRoute { get; set; } = "alfa";
}
