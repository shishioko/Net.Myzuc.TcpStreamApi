using Net.Myzuc.UtilLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Myzuc.Multistream.Client
{
    /// <summary>
    /// Representative of a connection to a Multistream server.
    /// </summary>
    public sealed class Multistream : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Establishes and initializes a new Multistream connection asynchronously.
        /// </summary>
        /// <param name="host">DNS Hostname to be resolved</param>
        /// <param name="port">Port to connect to</param>
        /// <returns>A new Multistream connection</returns>
        /// <exception cref="AggregateException"></exception>
        public static async Task<Multistream> ConnectAsync(string host, ushort port)
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
        /// <summary>
        /// Establishes and initializes a new Multistream connection synchronously.
        /// </summary>
        /// <param name="host">DNS Hostname to be resolved</param>
        /// <param name="port">Port to connect to</param>
        /// <returns>A new Multistream connection</returns>
        /// <exception cref="AggregateException"></exception>
        public static Multistream Connect(string host, ushort port)
        {
            return ConnectAsync(host, port).Result;
        }
        /// <summary>
        /// Creates and initializes a new <see cref="Net.Myzuc.Multistream.Client.Multistream"/> asynchronously.
        /// </summary>
        /// <param name="host">The <see cref="System.Net.EndPoint"/> to connect to</param>
        /// <returns>A new <see cref="Net.Myzuc.Multistream.Client.Multistream"/></returns>
        public static async Task<Multistream> ConnectAsync(EndPoint host)
        {
            Socket socket = new(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host);
            Multistream multistream = new(socket);
            await multistream.InitializeAsync();
            return multistream;
        }
        /// <summary>
        /// Creates and initializes a new <see cref="Net.Myzuc.Multistream.Client.Multistream"/> synchronously.
        /// </summary>
        /// <param name="host">The <see cref="System.Net.EndPoint"/> to connect to</param>
        /// <returns>A new <see cref="Net.Myzuc.Multistream.Client.Multistream"/></returns>
        public static Multistream Connect(IPEndPoint host)
        {
            return ConnectAsync(host).Result;
        }
        private bool Disposed = false;
        private DataStream<Stream> Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly SemaphoreSlim SyncWrite = new(1, 1);
        private readonly Dictionary<Guid, ChannelStream> Streams = [];
        /// <summary>
        /// Fired once after disposal of the <see cref="Net.Myzuc.Multistream.Client.Multistream"/> has finished.
        /// </summary>
        public event Func<Task> OnDisposed = () => Task.CompletedTask;
        private Multistream(Socket socket)
        {
            Stream = new(new NetworkStream(socket), true);
        }
        /// <summary>
        /// Closes all <see cref="Net.Myzuc.UtilLib.ChannelStream"/> and disposes the underlying <see cref="System.Net.Sockets.Socket"/> asynchronously.
        /// </summary>
        /// <returns></returns>
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
        /// <summary>
        /// Closes all <see cref="Net.Myzuc.UtilLib.ChannelStream"/> and disposes the underlying <see cref="System.Net.Sockets.Socket"/> synchronously.
        /// </summary>
        /// <returns></returns>
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
        /// <summary>
        /// Opens a new <see cref="Net.Myzuc.UtilLib.ChannelStream"/> on the <see cref="Net.Myzuc.Multistream.Client.Multistream"/> asynchronously.
        /// </summary>
        /// <returns>The newly opened <see cref="Net.Myzuc.UtilLib.ChannelStream"/></returns>
        public async Task<ChannelStream> OpenAsync()
        {
            await Sync.WaitAsync();
            Guid streamId = Guid.NewGuid();
            while (Streams.ContainsKey(streamId)) streamId = Guid.NewGuid();
            (ChannelStream userStream, ChannelStream appStream) = ChannelStream.CreatePair();
            Streams.Add(streamId, appStream);
            Sync.Release();
            _ = SendAsync(streamId, appStream);
            return userStream;
        }
        /// <summary>
        /// Opens a new <see cref="Net.Myzuc.UtilLib.ChannelStream"/> on the <see cref="Net.Myzuc.Multistream.Client.Multistream"/> synchronously.
        /// </summary>
        /// <returns>The newly opened <see cref="Net.Myzuc.UtilLib.ChannelStream"/></returns>
        public ChannelStream Open()
        {
            return OpenAsync().Result;
        }
        private async Task InitializeAsync()
        {
            if (Assembly.GetExecutingAssembly().GetName().Version is not Version version)
            {
                await DisposeAsync();
                throw new NotSupportedException("Version undefined");
            }
            await Stream.WriteStringAsync(version.ToString(), SizePrefix.S32V);
            if (version.ToString() != await Stream.ReadStringAsync(SizePrefix.S32V))
            {
                await DisposeAsync();
                throw new NotSupportedException("Version mismatch");
            }
            using RSA rsa = RSA.Create();
            rsa.KeySize = 2048;
            await Stream.WriteU8AAsync(rsa.ExportRSAPublicKey(), SizePrefix.S32V);
            byte[] secret = rsa.Decrypt(await Stream.ReadU8AAsync(SizePrefix.S32V), RSAEncryptionPadding.Pkcs1);
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
                    byte[] data = await Stream.ReadU8AAsync(SizePrefix.S32);
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
            catch (Exception)
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
                    await Stream.WriteU8AAsync(data, SizePrefix.S32);
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
