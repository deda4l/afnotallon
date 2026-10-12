using AfallonCoop.Session;
using AfallonCoop.Transport;
using Xunit;

namespace AfallonCoop.Tests;

public class LiteNetTests
{
    private static SessionConfig Cfg(string n) => new() { ModVersion = "0.1", GameBuild = "B1", PlayerName = n };

    [Fact]
    public async Task Real_udp_handshake_and_discovery_on_localhost()
    {
        const int port = 27599;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double Clock() => sw.Elapsed.TotalSeconds;

        var hostT = LiteNetTransport.Host(port);
        hostT.DiscoveryInfo = () => "Host's game|1/5";
        var host = CoopSession.StartHost(hostT, Cfg("Host"), Clock);

        // Discovery runs on its own socket; keep the host polling meanwhile.
        var discover = LiteNetTransport.DiscoverAsync(port, 600);
        while (!discover.IsCompleted) { host.Tick(); await Task.Delay(10); }
        var found = await discover;
        Assert.Contains(found, h => h.Info == "Host's game|1/5");

        var client = CoopSession.StartClient(LiteNetTransport.Connect("127.0.0.1", port), Cfg("Guest"), Clock);
        for (int i = 0; i < 300 && client.State != SessionState.Connected; i++)
        {
            host.Tick(); client.Tick();
            await Task.Delay(10);
        }
        Assert.Equal(SessionState.Connected, client.State);
        Assert.Equal(2, host.PlayerCount);

        client.Dispose();
        for (int i = 0; i < 100 && host.PlayerCount > 1; i++) { host.Tick(); await Task.Delay(10); }
        Assert.Equal(1, host.PlayerCount);
        host.Dispose();
    }
}
