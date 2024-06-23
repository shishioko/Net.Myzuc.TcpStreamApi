using Net.Myzuc.UtilLib;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Net.Myzuc.TcpStreamApi.Server
{
    internal sealed class TSApiClient : IDisposable
    {
        private readonly TSApiServer Server;
        private readonly Socket Socket;
        private readonly DataStream Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Dictionary<int, ChannelWriter<byte[]>?> Streams = [];
        public TSApiClient(TSApiServer server, Socket socket)
        {
            Server = server;
            Socket = socket;
            Stream = new(new NetworkStream(socket));
            _ = ReceiveAsync();
        }
        public void Dispose()
        {
            Socket.Dispose();
            Stream.Dispose();
        }
        private async Task ReceiveAsync()
        {
            try
            {
                while (Socket.Connected)
                {
                    _ = HandleAsync(await Stream.ReadU8AAsync(await Stream.ReadS32Async()));
                }
            }
            catch (Exception ex)
            {

            }
            finally
            {
                Dispose();
            }
        }
        private async Task HandleAsync(byte[] inputBuffer)
        {
            using DataStream packetStream = new(inputBuffer);
            int streamId = packetStream.ReadS32();
            byte[] data = packetStream.ReadU8A(packetStream.ReadS32());
            await Sync.WaitAsync();
            if (Streams.TryGetValue(streamId, out ChannelWriter<byte[]>? stream))
            {
                if (stream is not null)
                {
                    if (data.Length > 0) await stream.WriteAsync(data);
                    else
                    {
                        stream?.Complete();
                        Streams.Remove(streamId);
                    }
                }
            }
            else
            {
                using DataStream requestStream = new(data);
                Server.Sync.Wait();
                using DataStream responseStream = new();
                responseStream.WriteS32(streamId);
                responseStream.WriteS32(4);
                if (Server.Endpoints.TryGetValue(requestStream.ReadStringS32(), out Func<byte[], ChannelStream>? method))
                {
                    Exception? exception = null;
                    try
                    {
                        ChannelStream communications = method(requestStream.ReadU8A(requestStream.ReadS32()));
                        responseStream.WriteS32((communications.Reader is not null ? 0x01 : 0x00) | (communications.Writer is not null ? 0x02 : 0x00));
                        if (communications.Writer is not null) Streams.Add(streamId, communications.Writer);
                        if (communications.Reader is not null) _ = SendAsync(streamId, communications.Reader);
                    }
                    catch(Exception ex)
                    {
                        exception = ex;
                        if (exception is ArgumentException) responseStream.WriteS32(-3);
                        else responseStream.WriteS32(-1);
                    }
                }
                else responseStream.WriteS32(-2);
                byte[] responseBuffer = responseStream.Get();
                await Stream.WriteS32Async(responseBuffer.Length);
                await Stream.WriteU8AAsync(responseBuffer);
                Server.Sync.Release();
            }
            Sync.Release();
        }
        private async Task SendAsync(int streamId, ChannelReader<byte[]> reader)
        {
            try
            {
                while (!reader.Completion.IsCompleted)
                {
                    byte[] data = await reader.ReadAsync();
                    using DataStream outputStream = new();
                    outputStream.WriteS32(streamId);
                    outputStream.WriteS32(data.Length);
                    outputStream.WriteU8A(data);
                    byte[] outputBuffer = outputStream.Get();
                    await Sync.WaitAsync();
                    await Stream.WriteS32Async(outputBuffer.Length);
                    await Stream.WriteU8AAsync(outputBuffer);
                    Sync.Release();
                }
                {
                    using DataStream outputStream = new();
                    outputStream.WriteS32(streamId);
                    outputStream.WriteS32(0);
                    byte[] outputBuffer = outputStream.Get();
                    await Sync.WaitAsync();
                    await Stream.WriteS32Async(outputBuffer.Length);
                    await Stream.WriteU8AAsync(outputBuffer);
                    Streams.Remove(streamId);
                    Sync.Release();
                }
            }
            catch (Exception)
            {
                Dispose();
            }
        }
    }
}
