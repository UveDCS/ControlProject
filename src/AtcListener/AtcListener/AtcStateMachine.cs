using NLog;

namespace AtcListener;

// Maquina de estados por callsign, para UN aerodromo concreto (pista/ruta vienen de su
// configuracion). El viento viene de la mision real via DCS-gRPC, con reserva a
// "viento calma" si no hay conexion (para que el ATC siga funcionando sin DCS abierto).
public class AtcStateMachine(
    string runway,
    string taxiRoute,
    double referenceLat,
    double referenceLon,
    double referenceAlt,
    Logger logger)
{
    private readonly Dictionary<string, AircraftState> _states = new();

    public async Task<string> HandleAsync(string callsign, AtcIntent intent, DcsWorldClient worldClient)
    {
        var state = _states.GetValueOrDefault(callsign, AircraftState.SinContacto);
        var name = Capitalize(callsign);

        switch (intent)
        {
            case AtcIntent.SolicitarRodaje when state == AircraftState.SinContacto:
                _states[callsign] = AircraftState.RodajeAutorizado;
                return $"{name}, ruede a pista {runway} vía {taxiRoute}, mantenga posición en punto de espera";

            case AtcIntent.SolicitarRodaje:
                return $"{name}, ya tiene autorización de rodaje";

            case AtcIntent.ListoParaDespegue when state == AircraftState.RodajeAutorizado:
                _states[callsign] = AircraftState.DespegueAutorizado;
                return $"{name}, {await DescribeWindAsync(worldClient)}, pista {runway}, autorizado despegue";

            case AtcIntent.ListoParaDespegue when state == AircraftState.DespegueAutorizado:
                return $"{name}, ya tiene autorización de despegue";

            case AtcIntent.ListoParaDespegue:
                return $"{name}, negativo, no tiene autorización de rodaje";

            case AtcIntent.SolicitarAproximacion when state == AircraftState.SinContacto:
                _states[callsign] = AircraftState.AproximacionAutorizada;
                return $"{name}, autorizado aproximación pista {runway}, reporte en final";

            case AtcIntent.SolicitarAproximacion:
                return $"{name}, ya tiene autorización de aproximación";

            case AtcIntent.ReporteFinal when state == AircraftState.AproximacionAutorizada:
                _states[callsign] = AircraftState.AutorizadoAterrizaje;
                return $"{name}, {await DescribeWindAsync(worldClient)}, pista {runway}, autorizado a aterrizar";

            case AtcIntent.ReporteFinal when state == AircraftState.AutorizadoAterrizaje:
                return $"{name}, ya tiene autorización de aterrizaje";

            case AtcIntent.ReporteFinal:
                return $"{name}, negativo, no tiene autorización de aproximación";

            default:
                return $"{name}, repita, no entendido";
        }
    }

    private async Task<string> DescribeWindAsync(DcsWorldClient worldClient)
    {
        try
        {
            var (headingDeg, strengthMs) = await worldClient.GetWindAsync(referenceLat, referenceLon, referenceAlt);

            if (strengthMs < 1.0f) return "viento calma";

            return $"viento {Math.Round(headingDeg)} grados, {strengthMs:F0} metros por segundo";
        }
        catch (Exception ex)
        {
            logger.Warn($"No se pudo obtener el viento real via DCS-gRPC, usando valor por defecto: {ex.Message}");
            return "viento calma";
        }
    }

    private static string Capitalize(string callsign) =>
        string.Join(' ', callsign.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
