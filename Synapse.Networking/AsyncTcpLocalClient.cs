using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;

namespace Synapse.Networking;

public class AsyncTcpLocalClient : AsyncTcpClient
{
    private readonly IPAddress? _address;
    private readonly string? _hostName;
    private readonly int _port;
    private readonly int _reconnectTries;

    private Socket? _socket;

    public AsyncTcpLocalClient(IPAddress address, int port, int reconnectTries)
    {
        _address = address;
        _port = port;
        _reconnectTries = reconnectTries;
    }

    [PublicAPI]
    public AsyncTcpLocalClient(string hostName, int port, int reconnectTries)
    {
        _hostName = hostName;
        _port = port;
        _reconnectTries = reconnectTries;
    }

    public override Socket Socket => _socket ?? throw new InvalidOperationException("No socket available.");

    protected override async Task ConnectAsync(CancellationToken token)
    {
        int reconnectTry = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
                SendMessage(new AsyncTcpMessageEventArgs(Networking.Message.Connecting, null, reconnectTry));
                Task connect = !string.IsNullOrWhiteSpace(_hostName)
                    ? socket.ConnectAsync(_hostName, _port)
                    : socket.ConnectAsync(_address ?? throw new InvalidOperationException("No hostname or ip"), _port);
                _socket = socket;
                Task timeout = Task.Delay(5000, token);
                if (await Task.WhenAny(connect, timeout) == timeout)
                {
                    throw new AsyncTcpConnectTimeoutException();
                }

                try
                {
                    await connect;
                }
                catch (Exception e)
                {
                    throw new AsyncTcpConnectFailedException(e);
                }

                reconnectTry = 0;

#if NET
                await using NetworkStream stream = new(socket);
#else
                using NetworkStream stream = new(socket);
#endif
                Stream = stream;
                await ReadAsync(token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (Closing)
                {
                    return;
                }

                reconnectTry++;
                if (reconnectTry >= _reconnectTries)
                {
                    throw new AsyncTcpFailedAfterRetriesException(reconnectTry, e);
                }

                SendMessage(new AsyncTcpMessageEventArgs(Networking.Message.ConnectionFailed, e, reconnectTry));
                await Task.Delay(2000, token);
            }
            finally
            {
                SendMessage(new AsyncTcpMessageEventArgs(Networking.Message.ConnectionClosed, null, reconnectTry));
                _socket = null;
                Stream = null;
            }
        }
    }
}
