using Net.Myzuc.UtilLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Myzuc.TcpStreamApi.Client
{
    public sealed class TSApi : IDisposable
    {
        public static TSApi Connect(string host, ushort port)
        {
            return ConnectAsync(host, port).Result;
        }
        public static async Task<TSApi> ConnectAsync(string host, ushort port)
        {
            List<Exception> exceptions = [];
            foreach(IPAddress ip in (await Dns.GetHostEntryAsync(host)).AddressList)
            {
                try
                {
                    return await ConnectAsync(new IPEndPoint(ip, port));
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
            throw new AggregateException(exceptions);
        }
        public static TSApi Connect(IPEndPoint host)
        {
            return ConnectAsync(host).Result;
        }
        public static async Task<TSApi> ConnectAsync(IPEndPoint host)
        {
            Socket socket = new(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host);
            return new(socket);
        }
        private readonly DataStream<NetworkStream> Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Dictionary<Guid, ChannelStream> Streams = [];
        private TSApi(Socket socket)
        {
            Stream = new(new NetworkStream(socket));
            _ = ReceiveAsync();
        }
        public void Dispose()
        {
            Stream.Dispose();
            Sync.Dispose();
            foreach (ChannelStream stream in Streams.Values) stream.Dispose();
        }
        public ChannelStream Interact(string endpoint)
        {
            return InteractAsync(endpoint).Result;
        }
        public async Task<ChannelStream> InteractAsync(string endpoint)
        {
            await Sync.WaitAsync();
            Guid streamId = Guid.NewGuid();
            while (Streams.ContainsKey(streamId)) streamId = Guid.NewGuid();
            (ChannelStream userStream, ChannelStream appStream) = ChannelStream.CreatePair();
            Streams.Add(streamId, appStream);
            _ = SendAsync(streamId, appStream);
            Sync.Release();
            using DataStream<MemoryStream> requestStream = new(new());
            requestStream.WriteGuid(streamId);
            requestStream.WriteU8A(Encoding.UTF8.GetBytes(endpoint));
            await Sync.WaitAsync();
            await Stream.WriteU8AVAsync(requestStream.Stream.ToArray());
            Sync.Release();
            return userStream;
        }
        private async Task ReceiveAsync()
        {
            try
            {
                while (Stream.Stream.Socket.Connected)
                {
                    _ = HandleAsync(await Stream.ReadU8AVAsync());
                }
            }
            catch (Exception)
            {

            }
            finally
            {
                Dispose();
            }
        }
        private async Task HandleAsync(byte[] inputBuffer)
        {
            using DataStream<MemoryStream> inputStream = new(new(inputBuffer));
            Guid streamId = inputStream.ReadGuid();
            byte[] data = inputBuffer[16..];
            await Sync.WaitAsync();
            if (!Streams.TryGetValue(streamId, out ChannelStream? stream)) throw new ProtocolViolationException("Unregistered stream was written");
            await stream!.WriteAsync(data);
            if (data.Length <= 0)
            {
                stream.Writer!.Complete();
                Streams.Remove(streamId);
            }
            Sync.Release();
        }
        private async Task SendAsync(Guid streamId, ChannelStream stream)
        {
            try
            {
                while (true)
                {
                    bool complete = !await stream.Reader!.WaitToReadAsync();
                    byte[] data = complete ? [] : await stream.Reader!.ReadAsync();
                    using DataStream<MemoryStream> outputStream = new(new());
                    outputStream.WriteGuid(streamId);
                    outputStream.WriteU8A(data);
                    await Sync.WaitAsync();
                    await Stream.WriteU8AVAsync(outputStream.Stream.ToArray());
                    Sync.Release();
                    if (complete) break;
                }
                stream.Writer!.Complete();
                Streams.Remove(streamId);
            }
            catch (Exception)
            {
                Dispose();
            }
        }
    }
}
