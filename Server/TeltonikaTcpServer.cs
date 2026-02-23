using System.Net;
using System.Net.Sockets;
using System.Text;
using FMC125.Output;
using FMC125.Parsing;
using FMC125.Rfid;
using FMC125.Utils;

namespace FMC125.Server;

/// <summary>
/// Asynchronous TCP server that accepts connections from Teltonika FMC125 devices,
/// performs the IMEI handshake, receives AVL data packets, and processes them.
///
/// Lifecycle:
///   1. <see cref="StartAsync"/> binds and starts listening.
///   2. Each connecting device gets its own <see cref="HandleClientAsync"/> task.
///   3. <see cref="StopAsync"/> (or disposing the CancellationTokenSource) tears
///      everything down cleanly.
/// </summary>
public sealed class TeltonikaTcpServer
{
  // -----------------------------------------------------------------------
  // Configuration
  // -----------------------------------------------------------------------

  private readonly IPAddress _listenAddress;
  private readonly int _port;

  /// <summary>
  /// Maximum bytes allowed for a single AVL data section.
  /// Protects against memory exhaustion on malformed packets.
  /// Default: 64 KB.
  /// </summary>
  public int MaxAvlDataBytes { get; init; } = 64 * 1024;

  /// <summary>
  /// Path of the NDJSON output file.  Each AVL record is appended as one JSON line.
  /// Set to null or empty to disable JSON output.
  /// Default: "avl_records.jsonl" in the working directory.
  /// </summary>
  public string JsonOutputPath { get; init; } = "avl_records.json";

  // -----------------------------------------------------------------------
  // State
  // -----------------------------------------------------------------------

  private TcpListener? _listener;
  private CancellationTokenSource? _cts;
  private JsonLogger? _jsonLogger;

  // -----------------------------------------------------------------------
  // Constructor
  // -----------------------------------------------------------------------

  /// <param name="listenAddress">Interface to bind on (use <see cref="IPAddress.Any"/> for all).</param>
  /// <param name="port">TCP port to listen on (default: 5027, Teltonika standard).</param>
  public TeltonikaTcpServer(IPAddress listenAddress, int port = 5027)
  {
    _listenAddress = listenAddress;
    _port = port;
  }

  // -----------------------------------------------------------------------
  // Start / Stop
  // -----------------------------------------------------------------------

  /// <summary>Binds the socket and starts accepting clients in the background.</summary>
  public Task StartAsync(CancellationToken externalCt = default)
  {
    _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

    _listener = new TcpListener(_listenAddress, _port);
    _listener.Start();

    if (!string.IsNullOrWhiteSpace(JsonOutputPath))
    {
      _jsonLogger = new JsonLogger(JsonOutputPath);
      Log($"JSON output → {Path.GetFullPath(JsonOutputPath)}");
    }

    Log($"Server listening on {_listenAddress}:{_port}");

    // Accept loop runs on its own task so StartAsync returns immediately.
    _ = AcceptLoopAsync(_cts.Token);

    return Task.CompletedTask;
  }

  /// <summary>
  /// Stops accepting new connections and signals connected clients to finish.
  /// Returns after the listener has been stopped (in-flight client tasks may
  /// still be completing).
  /// </summary>
  public async Task StopAsync()
  {
    Log("Server stopping...");
    _cts?.Cancel();
    _listener?.Stop();
    await Task.Delay(200); // Give active tasks a moment to notice cancellation.
    if (_jsonLogger is not null)
      await _jsonLogger.DisposeAsync();
    Log("Server stopped.");
  }

  // -----------------------------------------------------------------------
  // Accept loop
  // -----------------------------------------------------------------------

  private async Task AcceptLoopAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      TcpClient client;
      try
      {
        client = await _listener!.AcceptTcpClientAsync(ct);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (SocketException ex) when (ct.IsCancellationRequested)
      {
        // Listener was stopped; treat as normal shutdown.
        _ = ex;
        break;
      }
      catch (Exception ex)
      {
        LogError("Accept failed", ex);
        continue;
      }

      // Handle each client concurrently.
      _ = Task.Run(() => HandleClientAsync(client, ct), ct);
    }
  }

  // -----------------------------------------------------------------------
  // Per-client handler
  // -----------------------------------------------------------------------

  private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
  {
    string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    string imei = "unknown";

    Log($"Client connected: {remoteEndpoint}");

    try
    {
      using (client)
      {
        client.NoDelay = true;
        NetworkStream stream = client.GetStream();

        // Step 1 ── IMEI handshake
        imei = await ReceiveImeiAsync(stream, ct);
        if (string.IsNullOrEmpty(imei))
        {
          LogWarn($"[{remoteEndpoint}] Empty or invalid IMEI — rejecting.");
          await stream.WriteAsync(new byte[] { 0x00 }, ct);
          return;
        }

        Log($"IMEI: {imei}");

        // Accept the device.
        await stream.WriteAsync(new byte[] { 0x01 }, ct);
        Log($"[{imei}] IMEI accepted (0x01 sent).");

        // Step 2 ── Receive AVL packets in a loop
        while (!ct.IsCancellationRequested && client.Connected)
        {
          await ReceiveAndProcessAvlFrameAsync(stream, imei, ct);
        }
      }
    }
    catch (EndOfStreamException)
    {
      Log($"[{imei}] Device disconnected (EOF).");
    }
    catch (IOException ex) when (ex.InnerException is SocketException)
    {
      Log($"[{imei}] Connection reset by device.");
    }
    catch (OperationCanceledException)
    {
      Log($"[{imei}] Session cancelled.");
    }
    catch (InvalidDataException ex)
    {
      LogError($"[{imei}] Packet parse error", ex);
    }
    catch (Exception ex)
    {
      LogError($"[{imei}] Unexpected error", ex);
    }
    finally
    {
      Log($"[{imei}] Session ended ({remoteEndpoint}).");
    }
  }

  // -----------------------------------------------------------------------
  // IMEI handshake
  // -----------------------------------------------------------------------

  /// <summary>
  /// Reads the IMEI frame:
  ///   [2]  Length of IMEI string (big-endian uint16)
  ///   [N]  IMEI as ASCII digits
  /// Returns the IMEI string, or empty string on failure.
  /// </summary>
  private static async Task<string> ReceiveImeiAsync(
      NetworkStream stream,
      CancellationToken ct)
  {
    // 2-byte length prefix
    byte[] lengthBytes = await BigEndianReader.ReadExactAsync(stream, 2, ct);
    ushort imeiLength = (ushort)((lengthBytes[0] << 8) | lengthBytes[1]);

    if (imeiLength == 0 || imeiLength > 20)
    {
      LogWarn($"Suspicious IMEI length: {imeiLength}");
      return string.Empty;
    }

    // IMEI bytes
    byte[] imeiBytes = await BigEndianReader.ReadExactAsync(stream, imeiLength, ct);
    string imei = Encoding.ASCII.GetString(imeiBytes).Trim();

    return imei;
  }

  // -----------------------------------------------------------------------
  // AVL frame reception and processing
  // -----------------------------------------------------------------------

  /// <summary>
  /// Reads one complete AVL TCP frame, parses it, processes IO 109, and
  /// acknowledges it with the record count.
  /// </summary>
  private async Task ReceiveAndProcessAvlFrameAsync(
      NetworkStream stream,
      string imei,
      CancellationToken ct)
  {
    // ── Read preamble + data-length header (8 bytes) ──────────────────
    byte[] header = await BigEndianReader.ReadExactAsync(stream, 8, ct);

    uint preamble = ReadUInt32Be(header, 0);
    if (preamble != 0x00000000)
    {
      LogWarn($"[{imei}] Bad preamble: 0x{preamble:X8} — skipping frame.");
      return;
    }

    uint dataLength = ReadUInt32Be(header, 4);

    if (dataLength == 0 || dataLength > (uint)MaxAvlDataBytes)
    {
      LogWarn($"[{imei}] Invalid data length: {dataLength} — skipping.");
      return;
    }

    // ── Read AVL data ─────────────────────────────────────────────────
    byte[] avlData = await BigEndianReader.ReadExactAsync(stream, (int)dataLength, ct);

    // ── Read 4-byte CRC ───────────────────────────────────────────────
    byte[] crcBytes = await BigEndianReader.ReadExactAsync(stream, 4, ct);

    // ── Assemble the full raw frame for the parser ────────────────────
    // Layout: [preamble 4][dataLen 4][avlData N][crc 4]
    byte[] rawFrame = new byte[8 + dataLength + 4];
    Buffer.BlockCopy(header, 0, rawFrame, 0, 8);
    Buffer.BlockCopy(avlData, 0, rawFrame, 8, (int)dataLength);
    Buffer.BlockCopy(crcBytes, 0, rawFrame, 8 + (int)dataLength, 4);

    // Hex dump (useful during testing)
    Log($"[{imei}] Raw frame ({rawFrame.Length} bytes): {BigEndianReader.ToHexString(rawFrame)}");

    // ── Parse ─────────────────────────────────────────────────────────
    var packet = TeltonikaPacketParser.ParseFrame(rawFrame);

    Log($"[{imei}] Codec: 0x{packet.CodecId:X2} | Records: {packet.RecordCount} " +
        $"| CRC valid: {packet.IsCrcValid}");

    // Per Teltonika protocol: server reports the number of records accepted.
    // If CRC is invalid, send ACK=0 — this tells the device the count does not match
    // and causes it to retransmit the packet (spec: "if sent data number and reported
    // by server don't match, module resends").
    if (!packet.IsCrcValid)
    {
      LogWarn($"[{imei}] CRC mismatch (received=0x{packet.ReceivedCrc:X4}, " +
              $"computed=0x{packet.ComputedCrc:X4}) — ACK=0, device will retransmit.");
      await stream.WriteAsync(BigEndianReader.BuildAck(0), ct);
      Log($"[{imei}] ACK sent: 0 (CRC failure, retransmit requested)");
      return;
    }

    // ── Process records ───────────────────────────────────────────────
    foreach (var record in packet.Records)
    {
      Log($"[{imei}] {record}");

      // IO 109 "Serial Packet" is only generated when RS-232 is in Delimiter mode
      // (FMC125 firmware ≥ 03.28.05). The Value bytes carry a 2-byte envelope
      // [Index][DataLength] before the actual RS-232 payload (see RfidDecoder for details).
      // These records always carry HIGH priority (Priority == 1).
      var rfidIo = record.GetIo(RfidDecoder.RfidIoId);
      RfidDecoder.RfidResult? rfidResult = null;
      if (rfidIo is not null && rfidIo.Value.Length >= 2)
      {
        rfidResult = RfidDecoder.Decode(rfidIo.Value);
        rfidResult.PrintToConsole(imei, record.Timestamp);
      }

      if (_jsonLogger is not null)
        await _jsonLogger.WriteRecordAsync(imei, record, rfidResult);
    }

    // ── ACK ──────────────────────────────────────────────────────────
    // Per Teltonika protocol spec: 4-byte big-endian integer = number of records received.
    // Device retransmits if this value does not match the number it sent.
    byte[] ack = BigEndianReader.BuildAck(packet.RecordCount);
    await stream.WriteAsync(ack, ct);
    Log($"[{imei}] ACK sent: {packet.RecordCount}");
  }

  // -----------------------------------------------------------------------
  // Utility
  // -----------------------------------------------------------------------

  private static uint ReadUInt32Be(byte[] buf, int start) =>
      ((uint)buf[start] << 24) |
      ((uint)buf[start + 1] << 16) |
      ((uint)buf[start + 2] << 8) |
      buf[start + 3];

  // -----------------------------------------------------------------------
  // Logging (easily swappable for ILogger<T> in production)
  // -----------------------------------------------------------------------

  private static void Log(string message) =>
      Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");

  private static void LogWarn(string message) =>
      Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] WARN  {message}");

  private static void LogError(string message, Exception ex) =>
      Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] ERROR {message}: {ex.Message}");
}
