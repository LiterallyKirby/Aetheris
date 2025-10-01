using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Aetheris
{
    class Server
    {
        private TcpListener? listener;
        private bool running;

        public void RunServer()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, Config.SERVER_PORT);
                listener.Start();
                running = true;

                Console.WriteLine("[[Server]] Server started on port: " + Config.SERVER_PORT);

                // Accept clients in background thread
                while (running)
                {
                    if (listener.Pending())
                    {
                        var client = listener.AcceptTcpClient();
                        Console.WriteLine("[[Server]] Client connected!");

                        // Handle client on new thread
                        var thread = new Thread(HandleClient);
                        thread.Start(client);
                    }

                    Thread.Sleep(10); // small delay so loop isn’t tight
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[[Server]] Error: " + ex.Message);
            }
        }

        private void HandleClient(object? obj)
        {
            var client = (TcpClient)obj!;
            var stream = client.GetStream();
            var buffer = new byte[1024];

            try
            {
                while (client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0) break;

                    string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Console.WriteLine("[[Server]] Received: " + msg);

                    // Echo back
                    byte[] response = Encoding.UTF8.GetBytes("Echo: " + msg);
                    stream.Write(response, 0, response.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[[Server]] Client error: " + ex.Message);
            }

            Console.WriteLine("[[Server]] Client disconnected.");
            client.Close();
        }
    }
}
