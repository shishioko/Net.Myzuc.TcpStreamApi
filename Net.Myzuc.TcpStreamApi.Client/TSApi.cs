﻿using Net.Myzuc.UtilLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
            TSApi tsapi = new(socket);
            await tsapi.InitializeAsync();
            return tsapi;
        }
        private DataStream<Stream> Stream;
        private readonly SemaphoreSlim Sync = new(1, 1);
        private readonly Dictionary<Guid, ChannelStream> Streams = [];
        private TSApi(Socket socket)
        {
            Stream = new(new NetworkStream(socket));
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
            byte[] data = Encoding.UTF8.GetBytes(endpoint);
            await Stream.WriteGuidAsync(streamId);
            await Stream.WriteU8AVAsync(data);
            await Stream.WriteU8AAsync(new byte[(32 - ((16 + data.Length) % 32)) & 31]);
            _ = SendAsync(streamId, appStream);
            Sync.Release();
            return userStream;
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
                    if (!Streams.TryGetValue(streamId, out ChannelStream? stream)) throw new ProtocolViolationException("Unregistered stream was written");
                    await stream!.WriteAsync(data);
                    if (data.Length <= 0)
                    {
                        stream.Writer!.Complete();
                        Streams.Remove(streamId);
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
