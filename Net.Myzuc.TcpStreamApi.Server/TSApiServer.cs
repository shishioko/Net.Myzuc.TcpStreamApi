﻿using Net.Myzuc.UtilLib;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Myzuc.TcpStreamApi.Server
{
    public sealed class TSApiServer
    {
        internal readonly SemaphoreSlim Sync = new(1, 1);
        internal readonly Dictionary<string, Func<byte[], Task<ChannelStream>>> Endpoints = [];
        public TSApiServer()
        {

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
        public bool RegisterEndpoint(string endpoint, Func<byte[], Task<ChannelStream>> method)
        {
            Sync.Wait();
            bool result = Endpoints.TryAdd(endpoint, method);
            Sync.Release();
            return result;
        }
        public bool UnregisterEndpoint(string endpoint)
        {
            Sync.Wait();
            bool result = Endpoints.Remove(endpoint);
            Sync.Release();
            return result;
        }
    }
}
