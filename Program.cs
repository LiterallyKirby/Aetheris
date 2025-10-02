using System;
using System.Threading;
using System.Threading.Tasks;

namespace Aetheris
{
    class Program
    {
        static void Main()
        {
            // Start server asynchronously in background
            var server = new Server();
            var serverTask = Task.Run(() => server.RunServerAsync());

            // Give server a moment to start (synchronous to stay on main thread)
            Thread.Sleep(500);

            // Start client on main thread (OpenTK REQUIRES the main thread)
            var client = new Client();
            client.Run();


            // Cleanup server
            server.Stop();
            serverTask.Wait(TimeSpan.FromSeconds(2));
        }
    }
}
