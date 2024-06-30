using Net.Myzuc.UtilLib;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace Net.Myzuc.TcpStreamApi.Server
{
    internal sealed class TSApiClient : IDisposable
    {
        private readonly TSApiServer Server;
        private DataStream<Stream> Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Dictionary<Guid, ChannelStream> Streams = [];
        public TSApiClient(TSApiServer server, Socket socket)
        {
            Server = server;
            Stream = new(new NetworkStream(socket));
        }
        public void Dispose()
        {
            Stream.Dispose();
            Sync.Dispose();
            foreach (ChannelStream stream in Streams.Values) stream.Dispose();
        }
        internal async Task InitializeAsync()
        {
            using RSA rsa = RSA.Create();
            rsa.KeySize = 2048;
            rsa.ImportRSAPublicKey(await Stream.ReadU8AVAsync(), out int _);
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            await Stream.WriteU8AVAsync(rsa.Encrypt(secret, RSAEncryptionPadding.Pkcs1));
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CFB;
            aes.BlockSize = 128;
            aes.FeedbackSize = 8;
            aes.KeySize = 256;
            aes.Key = secret;
            aes.IV = secret[..16];
            aes.Padding = PaddingMode.PKCS7;
            TwoWayStream<CryptoStream, CryptoStream> stream = new(new(Stream.Stream, aes.CreateDecryptor(), CryptoStreamMode.Read, false), new(Stream.Stream, aes.CreateEncryptor(), CryptoStreamMode.Write, false));
            Stream = new(stream);
            _ = ReceiveAsync();
        }
        private async Task ReceiveAsync()
        {
            try
            {
                while (true)
                {
                    Guid streamId = await Stream.ReadGuidAsync();
                    byte[] data = await Stream.ReadU8AVAsync();
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
                    await Stream.ReadU8AAsync((32 - ((16 + data.Length) % 32)) & 31);
                }
            }
            catch (Exception)
            {
                Dispose();
            }
        }
        private async Task SendAsync(Guid streamId, ChannelStream stream)
        {
            try
            {
                while (true)
                {
                    bool complete = !await stream.Reader!.WaitToReadAsync();
                    byte[] data = complete ? [] : await stream.Reader!.ReadAsync();
                    await Sync.WaitAsync();
                    await Stream.WriteGuidAsync(streamId);
                    await Stream.WriteU8AVAsync(data);
                    await Stream.WriteU8AAsync(new byte[(32 - ((16 + data.Length) % 32)) & 31]);
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
