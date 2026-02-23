using System.Net;
using FMC125.Server;
using FMC125.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  FMC125 Teltonika TCP Server — entry point
//
//  Normal mode:    dotnet run [port]
//  Simulate mode:  dotnet run simulate [port]   (connects a fake FMC125 device)
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Teltonika FMC125 TCP Server  |  Codec 8E + RFID    ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

// ── Simulate mode ─────────────────────────────────────────────────────────
// Run: dotnet run simulate [host] [port]
//   dotnet run simulate                          → 127.0.0.1:5027 (local)
//   dotnet run simulate 6.tcp.eu.ngrok.io 16007  → via ngrok tunnel
if (args.Length > 0 && args[0].Equals("simulate", StringComparison.OrdinalIgnoreCase))
{
  string simHost = args.Length > 1 && !int.TryParse(args[1], out _) ? args[1] : "127.0.0.1";
  int simPort = args.Length > 2 && int.TryParse(args[2], out int sp2) ? sp2
              : args.Length > 1 && int.TryParse(args[1], out int sp1) ? sp1
              : 5027;
  Console.WriteLine("╔══════════════════════════════════════════════════════╗");
  Console.WriteLine("║          DEVICE SIMULATOR MODE                       ║");
  Console.WriteLine("╚══════════════════════════════════════════════════════╝");
  Console.WriteLine();
  await DeviceSimulator.RunAsync(simHost, simPort);
  return;
}

// Read optional port from the first command-line argument (default: 5027).
int port = args.Length > 0 && int.TryParse(args[0], out int cliPort) ? cliPort : 5027;

// Graceful-shutdown via Ctrl+C.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
  e.Cancel = true;                   // Don't terminate the process immediately.
  Console.WriteLine("\nShutting down...");
  cts.Cancel();
};

var server = new TeltonikaTcpServer(IPAddress.Any, port)
{
  MaxAvlDataBytes = 128 * 1024       // 128 KB safety cap per AVL data section.
};

await server.StartAsync(cts.Token);

Console.WriteLine($"Listening on port {port}. Press Ctrl+C to stop.");
Console.WriteLine();

try
{
  // Block until cancellation.
  await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
  // Expected when Ctrl+C is pressed.
}

await server.StopAsync();
