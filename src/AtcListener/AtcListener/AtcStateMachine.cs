namespace AtcListener;

// Fase 1 - maquina de estados por callsign. Pista, ruta y viento son fijos (mock);
// en Fase 2 vendran del estado real de la mision via DCS-gRPC.
public class AtcStateMachine
{
    private const string Pista = "dos uno";
    private const string RutaRodaje = "alfa";
    private const string Viento = "viento calma";

    private readonly Dictionary<string, AircraftState> _states = new();

    public string Handle(string callsign, AtcIntent intent)
    {
        var state = _states.GetValueOrDefault(callsign, AircraftState.SinContacto);
        var name = Capitalize(callsign);

        switch (intent)
        {
            case AtcIntent.SolicitarRodaje when state == AircraftState.SinContacto:
                _states[callsign] = AircraftState.RodajeAutorizado;
                return $"{name}, ruede a pista {Pista} vía {RutaRodaje}, mantenga posición en punto de espera";

            case AtcIntent.SolicitarRodaje:
                return $"{name}, ya tiene autorización de rodaje";

            case AtcIntent.ListoParaDespegue when state == AircraftState.RodajeAutorizado:
                _states[callsign] = AircraftState.DespegueAutorizado;
                return $"{name}, {Viento}, pista {Pista}, autorizado despegue";

            case AtcIntent.ListoParaDespegue when state == AircraftState.DespegueAutorizado:
                return $"{name}, ya tiene autorización de despegue";

            case AtcIntent.ListoParaDespegue:
                return $"{name}, negativo, no tiene autorización de rodaje";

            case AtcIntent.SolicitarAproximacion when state == AircraftState.SinContacto:
                _states[callsign] = AircraftState.AproximacionAutorizada;
                return $"{name}, autorizado aproximación pista {Pista}, reporte en final";

            case AtcIntent.SolicitarAproximacion:
                return $"{name}, ya tiene autorización de aproximación";

            case AtcIntent.ReporteFinal when state == AircraftState.AproximacionAutorizada:
                _states[callsign] = AircraftState.AutorizadoAterrizaje;
                return $"{name}, {Viento}, pista {Pista}, autorizado a aterrizar";

            case AtcIntent.ReporteFinal when state == AircraftState.AutorizadoAterrizaje:
                return $"{name}, ya tiene autorización de aterrizaje";

            case AtcIntent.ReporteFinal:
                return $"{name}, negativo, no tiene autorización de aproximación";

            default:
                return $"{name}, repita, no entendido";
        }
    }

    private static string Capitalize(string callsign) =>
        string.Join(' ', callsign.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
