using System;
using AetherisClient;

namespace Aetheris
{
    class Program
    {
        static void Main()
        {
            var server = new Server();
            var serverThread = new System.Threading.Thread(server.RunServer);
            serverThread.Start();

            var client = new Client();
            client.Run();
        }
    }
}
