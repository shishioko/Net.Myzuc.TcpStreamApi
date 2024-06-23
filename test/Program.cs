using Net.Myzuc.TcpStreamApi.Client;
using Net.Myzuc.TcpStreamApi.Server;
using Net.Myzuc.UtilLib;
using System.Net;
using System.Text;

namespace test
{
    public static class Program
    {
        private static async Task Main(string[] parameters)
        {
            //if (parameters.Length == 1)
            {

                TSApiServer server = new();
                server.RegisterEndpoint("balls", input =>
                {
                    (ChannelStream stream, ChannelStream returnStream) = ChannelStream.CreatePair();
                    _ = Balls(stream, Encoding.UTF8.GetString(input));
                    return returnStream;
                });
                _ = server.Listen(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 26353));
            }
            //else
            {
                TSApi api = await TSApi.ConnectAsync(new System.Net.IPEndPoint(IPAddress.Loopback, 26353));
                Console.WriteLine("connected!");
                ChannelStream channelStream = await api.InteractAsync("balls", Encoding.UTF8.GetBytes("This is a Test Text... LOL"));
                using DataStream stream = new(channelStream);
                Console.WriteLine(await stream.ReadStringS32VAsync());
                await stream.WriteStringS32VAsync("Acknowledged!");
                //stream.Dispose();
            }
        }
        private static async Task Balls(ChannelStream channelStream, string text)
        {
            try
            {
                using DataStream stream = new(channelStream);
                await stream.WriteStringS32VAsync(text.ToLower());
                Console.WriteLine(await stream.ReadStringS32VAsync());
            }
            finally
            {
                channelStream.Dispose();
            }
        }
    }
}
