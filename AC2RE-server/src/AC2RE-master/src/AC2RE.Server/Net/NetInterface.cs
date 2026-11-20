using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AC2RE.Server;

internal class NetInterface {

    private static readonly IPEndPoint ANY_ENDPOINT = new(IPAddress.Any, 0);

    public readonly int port;

    private bool closed;
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public NetInterface(int port = 0) {
        // MOD2: on Windows an ICMP "port unreachable" (e.g. a client that already exited) makes the NEXT receive throw
        // WSAECONNRESET (10054) and spams the log. Turn that behaviour off (SIO_UDP_CONNRESET).
        try {
            socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
        } catch (Exception) {
            // not Windows / not supported - harmless
        }
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        this.port = ((IPEndPoint?)socket.LocalEndPoint)?.Port ?? 0;
    }

    ~NetInterface() {
        if (!closed) {
            Logs.NET.warn("Didn't close NetInterface before destruction!",
                "interface", this);
            close();
        }
    }

    internal bool sendTo(byte[] buffer, int size, EndPoint sendEndpoint) {
        // UDP is all-or-nothing when sending a message
        return socket.SendTo(buffer, size, SocketFlags.None, sendEndpoint) == size;
    }

    internal async Task<SocketReceiveFromResult> receiveFromAsync(byte[] buffer) {
        return await socket.ReceiveFromAsync(buffer, SocketFlags.None, ANY_ENDPOINT);
    }

    internal void close() {
        socket.Close();
        closed = true;
    }

    public override string ToString() {
        return $"{port}";
    }
}
