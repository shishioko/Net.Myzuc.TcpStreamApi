using Net.Myzuc.UtilLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Myzuc.TcpStreamApi.Client
{
    public sealed class TSApi : IDisposable, IAsyncDisposable
    {
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
        public static TSApi Connect(string host, ushort port)
        {
            return ConnectAsync(host, port).Result;
        }
        public static async Task<TSApi> ConnectAsync(IPEndPoint host)
        {
            Socket socket = new(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host);
            TSApi tsapi = new(socket);
            await tsapi.InitializeAsync();
            return tsapi;
        }
        public static TSApi Connect(IPEndPoint host)
        {
            return ConnectAsync(host).Result;
        }
        private bool Disposed = false;
        private DataStream<Stream> Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly SemaphoreSlim SyncWrite = new(1, 1);
        private readonly Dictionary<Guid, ChannelStream> Streams = [];
        public event Func<Task> OnDisposed = () => Task.CompletedTask;
        private TSApi(Socket socket)
        {
            Stream = new(new NetworkStream(socket), true);
        }
        public async ValueTask DisposeAsync()
        {
            if (Disposed) return;
            Disposed = true;
            await Stream.Stream.DisposeAsync();
            Sync.Dispose();
            SyncWrite.Dispose();
            foreach (ChannelStream stream in Streams.Values) await stream.DisposeAsync();
            await OnDisposed();
        }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Stream.Stream.Dispose();
            Sync.Dispose();
            SyncWrite.Dispose();
            foreach (ChannelStream stream in Streams.Values) stream.Dispose();
            OnDisposed().Wait();
        }
        public async Task<ChannelStream> InteractAsync(string endpoint)
        {
            await Sync.WaitAsync();
            Guid streamId = Guid.NewGuid();
            while (Streams.ContainsKey(streamId)) streamId = Guid.NewGuid();
            (ChannelStream userStream, ChannelStream appStream) = ChannelStream.CreatePair();
            Streams.Add(streamId, appStream);
            Sync.Release();
            _ = SendAsync(streamId, appStream);
            await userStream.WriteAsync(Encoding.UTF8.GetBytes(endpoint));
            return userStream;
        }
        public ChannelStream Interact(string endpoint)
        {
            return InteractAsync(endpoint).Result;
        }
        private async Task InitializeAsync()
        {
            using RSA rsa = RSA.Create();
            rsa.KeySize = 2048;
            await Stream.WriteU8AVAsync(rsa.ExportRSAPublicKey());
            byte[] secret = rsa.Decrypt(await Stream.ReadU8AVAsync(), RSAEncryptionPadding.Pkcs1);
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CFB;
            aes.BlockSize = 128;
            aes.FeedbackSize = 8;
            aes.KeySize = 256;
            aes.Key = secret;
            aes.IV = secret[..16];
            aes.Padding = PaddingMode.PKCS7;
            TwoWayStream<CryptoStream, CryptoStream> stream = new(new(Stream.Stream, aes.CreateDecryptor(), CryptoStreamMode.Read), new(Stream.Stream, aes.CreateEncryptor(), CryptoStreamMode.Write));
            Stream = new(stream, true);
            _ = ReceiveAsync();
        }
        private async Task ReceiveAsync()
        {
            try
            {
                while (!Disposed)
                {
                    Guid streamId = await Stream.ReadGuidAsync();
                    byte[] data = await Stream.ReadU8AAsync(await Stream.ReadS32Async());
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
                    Sync.Release();
                    await Stream.ReadU8AAsync(((16 - ((20 + data.Length) % 16)) & 15) + 16);
                }
            }
            catch (Exception ex)
            {

            }
            finally
            {
                await DisposeAsync();
            }
        }
        private async Task SendAsync(Guid streamId, ChannelStream stream)
        {
            try
            {
                while (!Disposed)
                {
                    bool complete = !await stream.Reader!.WaitToReadAsync();
                    byte[] data = complete ? [] : await stream.Reader!.ReadAsync();
                    if (!complete && data.Length == 0) continue;
                    await SyncWrite.WaitAsync();
                    await Stream.WriteGuidAsync(streamId);
                    await Stream.WriteS32Async(data.Length);
                    await Stream.WriteU8AAsync(data);
                    await Stream.WriteU8AAsync(new byte[((16 - ((20 + data.Length) % 16)) & 15) + 16]);
                    SyncWrite.Release();
                    if (complete) break;
                }
                if (!stream.Reader!.Completion.IsCompleted) await stream.DisposeAsync();
                await Sync.WaitAsync();
                Streams.Remove(streamId);
                Sync.Release();
            }
            catch (Exception)
            {
                await DisposeAsync();
            }
        }
    }
}
