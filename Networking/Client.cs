
using System;
using System.Net.Sockets;


namespace AetherisClient
{
    class Client
    {
        private Game game;

        public void Run()
        {
            ConnectToServer();

            game = new Game();
            game.RunGame(); // explicitly starts the window
        }

        private void ConnectToServer()
        {
            Console.WriteLine("[Client] Connecting to server...");
            try
            {
                TcpClient tcpClient = new TcpClient("127.0.0.1", Config.SERVER_PORT);
                Console.WriteLine("[Client] Connected to server!");
            }
            catch (Exception e)
            {
                Console.WriteLine("[Client] Connection failed: " + e.Message);
                Environment.Exit(1);
            }
        }
    }
}

