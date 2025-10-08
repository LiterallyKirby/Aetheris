using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aetheris
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                bool runServer = args.Contains("--server");
                bool runClient = args.Contains("--client");
                
                // Show graphical menu if no command line flags
                if (!runServer && !runClient)
                {
                    Console.WriteLine("[INFO] Launching main menu...");
                    
                    using (var menu = new MenuWindow())
                    {
                        menu.Run();
                        
                        // Handle menu result
                        switch (menu.Result)
                        {
                            case MenuResult.SinglePlayer:
                                runServer = true;
                                runClient = true;
                                Console.WriteLine("[INFO] Starting Single Player mode...");
                                break;
                            
                            case MenuResult.Multiplayer:
                                runClient = true;
                                Console.WriteLine("[INFO] Starting Multiplayer Client...");
                                break;
                            
                            case MenuResult.Exit:
                            case MenuResult.None:
                                Console.WriteLine("[INFO] Exiting Aetheris.");
                                return;
                        }
                    }
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
                    var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (sender, e) =>
                    {
                        e.Cancel = true;
                        cts.Cancel();
                        Console.WriteLine("\n[INFO] Shutdown requested...");
                    };
                    
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
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crashlog.txt");
                    File.WriteAllText(logPath,
                        $"===== Aetheris Crash Report =====\n" +
                        $"Time: {DateTime.Now}\n" +
                        $"Message: {ex.Message}\n" +
                        $"Source: {ex.Source}\n" +
                        $"Stack Trace:\n{ex.StackTrace}\n\n" +
                        $"Inner Exception:\n{ex.InnerException}");
                    
                    Console.WriteLine($"[ERROR] Program crashed! Crash log saved to: {logPath}");
                    Console.WriteLine($"[ERROR] {ex.Message}");
                }
                catch
                {
                    Console.WriteLine("[FATAL] Crash logging failed.");
                    Console.WriteLine($"[FATAL] {ex.Message}");
                }
            }
        }
    }
}
