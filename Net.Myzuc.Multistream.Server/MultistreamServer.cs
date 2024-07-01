using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Myzuc.Multistream.Server
{
    /// <summary>
    /// Representative of a listener on a <see cref="System.Net.Sockets.Socket"/> that initializes incoming connections as <see cref="Net.Myzuc.Multistream.Server.MultistreamClient"/>.
    /// </summary>
    public sealed class MultistreamServer : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Creates and initializes a new <see cref="Net.Myzuc.Multistream.Server.MultistreamServer"/> asynchronously.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <returns></returns>
        public static async Task<MultistreamServer> CreateAsync(EndPoint endpoint)
        {
            MultistreamServer multistream = new(endpoint.AddressFamily);
            _ = multistream.ListenAsync(endpoint);
            return multistream;
        }
        /// <summary>
        /// Creates and initializes a new <see cref="Net.Myzuc.Multistream.Server.MultistreamServer"/> synchronously.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <returns></returns>
        public static MultistreamServer Create(EndPoint endpoint)
        {
            return CreateAsync(endpoint).Result;
        }
        private bool Disposed = false;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Socket Socket;
        /// <summary>
        /// Fired whenever a new <see cref="Net.Myzuc.Multistream.Server.MultistreamClient"/> is opened.
        /// </summary>
        public event Func<EndPoint?, MultistreamClient, Task> OnRequest = (EndPoint? endpoint, MultistreamClient client) => Task.CompletedTask;
        /// <summary>
        /// Fired once after disposal of the object <see cref="Net.Myzuc.Multistream.Server.MultistreamServer"/> finished.
        /// </summary>
        public event Func<Task> OnDisposed = () => Task.CompletedTask;
        private MultistreamServer(AddressFamily addressFamily)
        {
            Socket = new(addressFamily, SocketType.Stream, ProtocolType.Tcp);
        }
        /// <summary>
        /// Closes the underlying listening <see cref="System.Net.Sockets.Socket"/> asynchronously.
        /// </summary>
        /// <returns></returns>
        public async ValueTask DisposeAsync()
        {
            if (Disposed) return;
            Disposed = true;
            Sync.Dispose();
            Socket.Dispose();
            await OnDisposed();
        }
        /// <summary>
        /// Closes the underlying listening <see cref="System.Net.Sockets.Socket"/> synchronously.
        /// </summary>
        /// <returns></returns>
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Sync.Dispose();
            Socket.Dispose();
            OnDisposed().Wait();
        }
        private async Task ListenAsync(EndPoint endpoint)
        {
            try
            {
                Socket.Bind(endpoint);
                Socket.Listen();
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
        /// <summary>
        /// Tries to inserts a foreign <see cref="System.Net.Sockets.Socket"/> into the <see cref="Net.Myzuc.Multistream.Server.MultistreamServer"/> asynchronously.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        public async Task ServeAsync(EndPoint? endpoint, Stream stream)
        {
            try
            {
                MultistreamClient multistream = new(stream);
                await multistream.InitializeAsync();
                await OnRequest(endpoint, multistream);
            }
            catch (Exception)
            {

            }
        }
        /// <summary>
        /// Tries to insert a foreign <see cref="System.Net.Sockets.Socket"/> into the <see cref="Net.Myzuc.Multistream.Server.MultistreamServer"/> synchronously.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        public void Serve(EndPoint? endpoint, Stream stream)
        {
            ServeAsync(endpoint, stream).Wait();
        }
    }
}
