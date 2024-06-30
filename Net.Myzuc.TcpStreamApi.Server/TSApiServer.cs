using Net.Myzuc.UtilLib;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Myzuc.TcpStreamApi.Server
{
    public sealed class TSApiServer : IDisposable, IAsyncDisposable
    {
        private bool Disposed = false;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Socket Socket;
        public event Func<EndPoint?, TSApiClient, Task> OnRequest = (EndPoint? endpoint, TSApiClient client) => Task.CompletedTask;
        public event Func<Task> OnDisposed = () => Task.CompletedTask;
        public TSApiServer(EndPoint host)
        {
            Socket = new(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            Socket.Bind(host);
            Socket.Listen();
        }
        public async ValueTask DisposeAsync()
        {
            if (Disposed) return;
            Disposed = true;
            Sync.Dispose();
            Socket.Dispose();
            await OnDisposed();
        }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Sync.Dispose();
            Socket.Dispose();
            OnDisposed().Wait();
        }
        public void Listen()
        {
            ListenAsync().Wait();
        }
        public async Task ListenAsync()
        {
            try
            {
                while (true)
                {
                    Socket client = await Socket.AcceptAsync();
                    _ = ServeAsync(client.RemoteEndPoint, new NetworkStream(client));
                }
            }
            catch (Exception)
            {
                await DisposeAsync();
            }
        }
        public async Task ServeAsync(EndPoint? endpoint, Stream stream)
        {
            try
            {
                TSApiClient tsapi = new(stream);
                await tsapi.InitializeAsync();
                await OnRequest(endpoint, tsapi);
            }
            catch (Exception)
            {

            }
        }
        public void Serve(EndPoint? endpoint, Stream stream)
        {
            ServeAsync(endpoint, stream).Wait();
        }
    }
}
