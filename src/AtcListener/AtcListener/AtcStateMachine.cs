using NLog;

namespace AtcListener;

// Maquina de estados por callsign, para UN aerodromo concreto. El viento viene de la mision
// real via DCS-gRPC (con reserva a "viento calma" si no hay conexion), y con el se elige la
// pista activa (la mas alineada con el viento) de entre las declaradas en la configuracion.
// La pista es un recurso compartido de ESTE aerodromo: solo una aeronave puede tenerla
// ocupada a la vez (despegando o aterrizando) - las demas esperan hasta que se reporte libre.
public class AtcStateMachine(
    List<RunwayConfig> runways,
    string taxiRoute,
    double referenceLat,
    double referenceLon,
    double referenceAlt,
    Logger logger)
{
    private readonly Dictionary<string, AircraftState> _states = new();
    private string? _runwayOccupiedBy;

    public async Task<string> HandleAsync(string callsign, AtcIntent intent, DcsWorldClient worldClient)
    {
        var state = _states.GetValueOrDefault(callsign, AircraftState.SinContacto);
        var name = Capitalize(callsign);

        switch (intent)
        {
            case AtcIntent.SolicitarRodaje when state == AircraftState.SinContacto:
                _states[callsign] = AircraftState.RodajeAutorizado;
                var (rodajePista, _) = await GetActiveRunwayAndWindAsync(worldClient);
                return $"{name}, ruede a pista {rodajePista} vía {taxiRoute}, mantenga posición en punto de espera";

            case AtcIntent.SolicitarRodaje:
                return $"{name}, ya tiene autorización de rodaje";

            case AtcIntent.ListoParaDespegue when state == AircraftState.RodajeAutorizado:
            {
                if (_runwayOccupiedBy != null && _runwayOccupiedBy != callsign)
                    return $"{name}, mantenga posición, pista ocupada";

                var (pista, viento) = await GetActiveRunwayAndWindAsync(worldClient);
                _states[callsign] = AircraftState.DespegueAutorizado;
                _runwayOccupiedBy = callsign;
                return $"{name}, {viento}, pista {pista}, autorizado despegue";
            }

            case AtcIntent.ListoParaDespegue when state == AircraftState.DespegueAutorizado:
                return $"{name}, ya tiene autorización de despegue";

            case AtcIntent.ListoParaDespegue:
                return $"{name}, negativo, no tiene autorización de rodaje";

            case AtcIntent.SolicitarAproximacion when state == AircraftState.SinContacto:
                _states[callsign] = AircraftState.AproximacionAutorizada;
                var (aproxPista, _) = await GetActiveRunwayAndWindAsync(worldClient);
                return $"{name}, autorizado aproximación pista {aproxPista}, reporte en final";

            case AtcIntent.SolicitarAproximacion:
                return $"{name}, ya tiene autorización de aproximación";

            case AtcIntent.ReporteFinal when state == AircraftState.AproximacionAutorizada:
            {
                if (_runwayOccupiedBy != null && _runwayOccupiedBy != callsign)
                    return $"{name}, mantenga aproximación, pista ocupada";

                var (pista, viento) = await GetActiveRunwayAndWindAsync(worldClient);
                _states[callsign] = AircraftState.AutorizadoAterrizaje;
                _runwayOccupiedBy = callsign;
                return $"{name}, {viento}, pista {pista}, autorizado a aterrizar";
            }

            case AtcIntent.ReporteFinal when state == AircraftState.AutorizadoAterrizaje:
                return $"{name}, ya tiene autorización de aterrizaje";

            case AtcIntent.ReporteFinal:
                return $"{name}, negativo, no tiene autorización de aproximación";

            case AtcIntent.PistaDespejada when _runwayOccupiedBy == callsign:
                _runwayOccupiedBy = null;
                return $"{name}, recibido";

            case AtcIntent.PistaDespejada:
                return $"{name}, no consta que estuviera en pista";

            default:
                return $"{name}, repita, no entendido";
        }
    }

    private async Task<(string RunwayName, string WindPhrase)> GetActiveRunwayAndWindAsync(DcsWorldClient worldClient)
    {
        var fallbackRunway = runways.FirstOrDefault()?.Name ?? "desconocida";

        try
        {
            var (headingDeg, strengthMs) = await worldClient.GetWindAsync(referenceLat, referenceLon, referenceAlt);

            if (strengthMs < 1.0f || runways.Count == 0)
                return (fallbackRunway, "viento calma");

            var bestRunway = runways.MinBy(r => AngleDifference(r.HeadingDeg, headingDeg))!;
            var windPhrase = $"viento {Math.Round(headingDeg)} grados, {strengthMs:F0} metros por segundo";
            return (bestRunway.Name, windPhrase);
        }
        catch (Exception ex)
        {
            logger.Warn($"No se pudo obtener el viento real via DCS-gRPC, usando valores por defecto: {ex.Message}");
            return (fallbackRunway, "viento calma");
        }
    }

    private static double AngleDifference(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360;
        return diff > 180 ? 360 - diff : diff;
    }

    private static string Capitalize(string callsign) =>
        string.Join(' ', callsign.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
