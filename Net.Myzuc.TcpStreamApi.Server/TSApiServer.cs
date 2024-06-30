using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Myzuc.TcpStreamApi.Server
{
    public sealed class TSApiServer
    {
        internal readonly SemaphoreSlim Sync = new(1, 1);
        public event Func<EndPoint?, TSApiClient, Task> OnRequest = (EndPoint? endpoint, TSApiClient client) => Task.CompletedTask;
        public event Func<Task> OnDisposed = () => Task.CompletedTask;
        public TSApiServer()
        {

        }
        public void Listen(IPEndPoint host)
        {
            ListenAsync(host).Wait();
        }
        public async Task ListenAsync(IPEndPoint host)
        {
            using Socket socket = new(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(host);
            socket.Listen();
            while (true)
            {
                _ = ServeAsync(await socket.AcceptAsync());
            }
        }
        public async Task ServeAsync(Socket socket)
        {
            TSApiClient tsapi = new(socket);
            await tsapi.InitializeAsync();
            await OnRequest(socket.RemoteEndPoint, tsapi);
        }
    }
}
