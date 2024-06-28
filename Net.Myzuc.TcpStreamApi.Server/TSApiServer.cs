using Net.Myzuc.UtilLib;
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
        internal readonly Func<string, ChannelStream, Task> Handler;
        public TSApiServer(Func<string, ChannelStream, Task> handler)
        {
            Handler = handler;
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
                _ = ServeAsync(client);
            }
        }
        private async Task ServeAsync(Socket socket)
        {
            try
            {
                TSApiClient client = new(this, socket);
            }
            catch (Exception)
            {

            }
        }
    }
}
