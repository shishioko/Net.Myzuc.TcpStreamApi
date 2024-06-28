using Net.Myzuc.UtilLib;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text;

namespace Net.Myzuc.TcpStreamApi.Server
{
    internal sealed class TSApiClient : IDisposable
    {
        private readonly TSApiServer Server;
        private readonly DataStream<NetworkStream> Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Dictionary<Guid, ChannelStream> Streams = [];
        public TSApiClient(TSApiServer server, Socket socket)
        {
            Server = server;
            Stream = new(new NetworkStream(socket));
            _ = ReceiveAsync();
        }
        public void Dispose()
        {
            Stream.Dispose();
            Sync.Dispose();
            foreach (ChannelStream stream in Streams.Values) stream.Dispose();
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
            using DataStream<MemoryStream> packetStream = new(new(inputBuffer));
            Guid streamId = packetStream.ReadGuid();
            byte[] data = inputBuffer[16..];
            await Sync.WaitAsync();
            if (Streams.TryGetValue(streamId, out ChannelStream? stream))
            {
                await stream!.WriteAsync(data);
                if (data.Length <= 0) 
                {
                    stream.Writer!.Complete();
                    Streams.Remove(streamId);
                }
            }
            else
            {
                (ChannelStream userStream, ChannelStream appStream) = ChannelStream.CreatePair();
                Streams.Add(streamId, appStream);
                _ = SendAsync(streamId, appStream);
                _ = Server.Handler(Encoding.UTF8.GetString(data), userStream);
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
