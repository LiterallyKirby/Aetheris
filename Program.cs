using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aetheris
{
    class Program
    {
        static void Main(string[] args)
        {
            bool runServer = args.Contains("--server");
            bool runClient = args.Contains("--client");
            
            // Default: run both if no flags
            if (!runServer && !runClient)
            {
                runServer = true;
                runClient = true;
            }

            Task? serverTask = null;
            Server? server = null;

            // Start server if requested
            if (runServer)
            {
                server = new Server();
                serverTask = Task.Run(() => server.RunServerAsync());
                Console.WriteLine("[INFO] Server started.");
            }

            // Give the server a moment to initialize before starting the client
            if (runServer && runClient)
                Thread.Sleep(500);

            // Start client (OpenTK must stay on the main thread)
            if (runClient)
            {
                Console.WriteLine("[INFO] Launching client...");
                var client = new Client();
                client.Run(); // This blocks until the game window closes
            }
            else if (runServer)
            {
                // Server-only mode: keep it running until Ctrl+C
                Console.WriteLine("[INFO] Server running. Press Ctrl+C to stop.");
                
                // Set up graceful shutdown
                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                    Console.WriteLine("\n[INFO] Shutdown requested...");
                };

                // Wait for cancellation
                try
                {
                    cts.Token.WaitHandle.WaitOne();
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            // Stop the server if it was started
            if (server != null)
            {
                Console.WriteLine("[INFO] Stopping server...");
                server.Stop();
                serverTask?.Wait(TimeSpan.FromSeconds(2));
            }

            Console.WriteLine("[INFO] Program finished.");
        }
    }
}
