using System;
using System.IO;
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
                Socket client = await socket.AcceptAsync();
                _ = ServeAsync(client.RemoteEndPoint, new NetworkStream(client));
            }
        }
        public async Task ServeAsync(EndPoint? endpoint, Stream stream)
        {
            TSApiClient tsapi = new(endpoint, stream);
            await tsapi.InitializeAsync();
            await OnRequest(endpoint, tsapi);
        }
        public void Serve(EndPoint? endpoint, Stream stream)
        {
            ServeAsync(endpoint, stream).Wait();
        }
    }
}
