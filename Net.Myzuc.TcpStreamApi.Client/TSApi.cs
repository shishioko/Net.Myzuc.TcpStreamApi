using Net.Myzuc.UtilLib;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
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
            List<Exception> exceptions = new List<Exception>();
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
        private readonly Socket Socket;
        private readonly DataStream Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Dictionary<int, ChannelWriter<byte[]>?> Streams = [];
        private readonly Dictionary<int, ChannelWriter<byte[]>> InitialStreams = [];
        private int NextStream = 0;
        private TSApi(Socket socket)
        {
            Socket = socket;
            Stream = new(new NetworkStream(socket, false));
            _ = ReceiveAsync();
        }
        public void Dispose()
        {
            Socket.Dispose();
            Stream.Dispose();
        }
        public ChannelStream Interact(string endpoint, byte[] input)
        {
            return InteractAsync(endpoint, input).Result;
        }
        public async Task<ChannelStream> InteractAsync(string endpoint, byte[] input)
        {
            bool failed = false;
            try
            {
                await Sync.WaitAsync();
                while (Streams.ContainsKey(NextStream) || InitialStreams.ContainsKey(NextStream)) NextStream++;
                int streamId = NextStream;
                Channel<byte[]> inputChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions() { SingleReader = false, SingleWriter = false });
                InitialStreams.Add(streamId, inputChannel.Writer);
                Sync.Release();
                using DataStream requestStream = new();
                requestStream.WriteS32(streamId);
                using DataStream requestDataStream = new();
                requestDataStream.WriteStringS32(endpoint);
                requestDataStream.WriteS32(input.Length);
                requestDataStream.WriteU8A(input);
                byte[] requestDataBuffer = requestDataStream.Get();
                requestStream.WriteS32(requestDataBuffer.Length);
                requestStream.WriteU8A(requestDataBuffer);
                byte[] requestBuffer = requestStream.Get();
                await Sync.WaitAsync();
                await Stream.WriteS32Async(requestBuffer.Length);
                await Stream.WriteU8AAsync(requestBuffer);
                Sync.Release();
                ChannelStream responseChannel = new(inputChannel.Reader, null);
                using DataStream responseStream = new(responseChannel);
                int result = await responseStream.ReadS32Async();
                await Sync.WaitAsync();
                InitialStreams.Remove(streamId);
                if (result < 0)
                {
                    Sync.Release();
                    failed = true;
                    switch (result)
                    {
                        case -1: throw new AggregateException("Internal Host Error");
                        case -2: throw new ArgumentOutOfRangeException(nameof(endpoint));
                        case -3: throw new ArgumentException(nameof(endpoint));
                        default: throw new ProtocolViolationException();
                    }
                }
                Channel<byte[]> outputChannel = Channel.CreateUnbounded<byte[]>(new() { SingleReader = false, SingleWriter = false });
                ChannelStream communications = new((result & 0x01) != 0x00 ? inputChannel.Reader : null, (result & 0x02) != 0x00 ? outputChannel.Writer : null);
                if (communications.Reader is not null || communications.Writer is not null) Streams.Add(streamId, inputChannel.Writer);
                _ = SendAsync(streamId, outputChannel.Reader);
                Sync.Release();
                return communications;
            }
            catch(Exception)
            {
                if (failed) Dispose();
                throw;
            }
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
            using DataStream inputStream = new(inputBuffer);
            int streamId = inputStream.ReadS32();
            byte[] data = inputStream.ReadU8A(inputStream.ReadS32());
            await Sync.WaitAsync();
            if (InitialStreams.TryGetValue(streamId, out ChannelWriter<byte[]>? writer))
            {
                await writer.WriteAsync(data);
            }
            else if (Streams.TryGetValue(streamId, out ChannelWriter<byte[]>? reader))
            {
                if (reader is not null)
                {
                    if (data.Length > 0) await reader.WriteAsync(data);
                    else
                    {
                        reader.Complete();
                        Streams.Remove(streamId);
                    }
                }
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
                    Sync.Release();
                }
            }
            catch (Exception)
            {
                Dispose();
            }
            finally
            {
                
            }
        }
    }
}
