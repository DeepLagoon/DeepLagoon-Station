using Content.Client._Mono.Radar;
using Content.Shared._Mono.Radar;
using Robust.Shared.Network;

namespace Content.Client.Entry;

public sealed partial class EntryPoint
{
    private void InitializeRadarTelemetryNetworking()
    {
        // Register before connecting and receiving the server's message-name table. Entity
        // systems start later on the real client; registering there leaves the incoming ID uncached.
        var net = IoCManager.Resolve<IClientNetManager>();
        net.RegisterNetMessage<MsgRadarPoll>();
        net.RegisterNetMessage<MsgRadarSnapshot>(message =>
        {
            if (_entitySystemManager.TryGetEntitySystem<RadarBlipsSystem>(out var radar))
                radar.ApplyReport(message.Snapshot);
        });
    }
}
