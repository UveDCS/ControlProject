using Grpc.Net.Client;
using RurouniJones.Dcs.Grpc.V0.Atmosphere;
using RurouniJones.Dcs.Grpc.V0.Common;
using RurouniJones.Dcs.Grpc.V0.Mission;
using RurouniJones.Dcs.Grpc.V0.Net;
using RurouniJones.Dcs.Grpc.V0.World;

namespace AtcListener;

// Fase 2 - puente hacia el estado real de la mision via DCS-gRPC.
// Sustituye los valores fijos (pista, viento) de AtcStateMachine por datos reales.
public sealed class DcsWorldClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly MissionService.MissionServiceClient _mission;
    private readonly AtmosphereService.AtmosphereServiceClient _atmosphere;
    private readonly WorldService.WorldServiceClient _world;
    private readonly NetService.NetServiceClient _net;

    public DcsWorldClient(string host = "127.0.0.1", int port = 50051)
    {
        _channel = GrpcChannel.ForAddress($"http://{host}:{port}");
        _mission = new MissionService.MissionServiceClient(_channel);
        _atmosphere = new AtmosphereService.AtmosphereServiceClient(_channel);
        _world = new WorldService.WorldServiceClient(_channel);
        _net = new NetService.NetServiceClient(_channel);
    }

    // Nombres reales de los jugadores conectados en ese momento - se usan como callsigns
    // reconocibles ademas de los fijos de la configuracion.
    public async Task<IReadOnlyList<string>> GetConnectedPlayerNamesAsync()
    {
        var response = await _net.GetPlayersAsync(new GetPlayersRequest(), deadline: DateTime.UtcNow.AddSeconds(5));
        return response.Players.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
    }

    public async Task<IReadOnlyList<Airbase>> GetAirbasesAsync()
    {
        var response = await _world.GetAirbasesAsync(new GetAirbasesRequest { Coalition = Coalition.All },
            deadline: DateTime.UtcNow.AddSeconds(5));
        return response.Airbases;
    }

    // Prueba de conectividad minima - confirma que hay una mision corriendo con DCS-gRPC activo.
    public async Task<string> GetScenarioCurrentTimeAsync()
    {
        var response = await _mission.GetScenarioCurrentTimeAsync(new GetScenarioCurrentTimeRequest(),
            deadline: DateTime.UtcNow.AddSeconds(5));
        return response.Datetime;
    }

    public async Task<(float HeadingDeg, float StrengthMs)> GetWindAsync(double lat, double lon, double altMeters)
    {
        var response = await _atmosphere.GetWindAsync(new GetWindRequest
        {
            Position = new InputPosition { Lat = lat, Lon = lon, Alt = altMeters }
        }, deadline: DateTime.UtcNow.AddSeconds(5));

        return (response.Heading, response.Strength);
    }

    public void Dispose() => _channel.Dispose();
}
