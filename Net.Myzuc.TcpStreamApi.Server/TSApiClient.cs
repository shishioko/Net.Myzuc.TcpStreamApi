﻿using Net.Myzuc.UtilLib;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace Net.Myzuc.TcpStreamApi.Server
{
    public sealed class TSApiClient : IDisposable, IAsyncDisposable
    {
        private bool Disposed = false;
        private DataStream<Stream> Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly SemaphoreSlim SyncWrite = new(1, 1);
        private readonly Dictionary<Guid, ChannelStream> Streams = [];
        public event Func<string, ChannelStream, Task> OnRequest = (string endpoint, ChannelStream stream) => Task.CompletedTask;
        public event Func<Task> OnDisposed = () => Task.CompletedTask;
        internal TSApiClient(Stream stream)
        {
            Stream = new(stream, true);
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
                    else if (data.Length > 0)
                    {
                        
                        (ChannelStream userStream, ChannelStream appStream) = ChannelStream.CreatePair();
                        Streams.Add(streamId, appStream);
                        _ = SendAsync(streamId, appStream);
                        await OnRequest(Encoding.UTF8.GetString(data), userStream);
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
