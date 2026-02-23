using System.Net.Sockets;
using FMC125.Utils;

namespace FMC125.Tests;

/// <summary>
/// Simulates a Teltonika FMC125 device for local testing.
///
/// Sends two AVL packets to the local server:
///   Packet 1 — A plain GPS record (no IO events).
///   Packet 2 — A GPS record with IO 109 (RS-232 Delimiter RFID payload)
///              using the sample frame from the CF691 reader.
///
/// Usage (two terminals):
///   Terminal 1:  dotnet run
///   Terminal 2:  dotnet run simulate [port]
/// </summary>
public static class DeviceSimulator
{
  private const string TestImei = "353976010337900";

  // -----------------------------------------------------------------------
  // Entry point
  // -----------------------------------------------------------------------

  public static async Task RunAsync(string host = "127.0.0.1", int port = 5027)
  {
    Console.WriteLine($"[Simulator] Connecting to {host}:{port} …");

    using var tcp = new TcpClient();
    try
    {
      await tcp.ConnectAsync(host, port);
    }
    catch (SocketException ex)
    {
      Console.WriteLine($"[Simulator] Cannot connect: {ex.Message}");
      Console.WriteLine($"[Simulator] Make sure the server is running (dotnet run) before running the simulator.");
      return;
    }

    await using var stream = tcp.GetStream();
    Console.WriteLine("[Simulator] Connected.");

    // ── Step 1: IMEI Handshake ─────────────────────────────────────────
    // Wire format:  [2-byte big-endian IMEI length] [IMEI ASCII bytes]
    byte[] imeiBytes = System.Text.Encoding.ASCII.GetBytes(TestImei);
    byte[] imeiFrame = new byte[2 + imeiBytes.Length];
    imeiFrame[0] = (byte)(imeiBytes.Length >> 8);
    imeiFrame[1] = (byte)(imeiBytes.Length & 0xFF);
    Array.Copy(imeiBytes, 0, imeiFrame, 2, imeiBytes.Length);

    await stream.WriteAsync(imeiFrame);
    Console.WriteLine($"[Simulator] Sent IMEI: {TestImei}");

    byte[] imeiResp = await BigEndianReader.ReadExactAsync(stream, 1, CancellationToken.None);
    if (imeiResp[0] != 0x01)
    {
      Console.WriteLine($"[Simulator] IMEI rejected (0x{imeiResp[0]:X2}). Aborting.");
      return;
    }
    Console.WriteLine("[Simulator] IMEI accepted (server replied 0x01).");

    // ── Step 2: Plain GPS packet ───────────────────────────────────────
    Console.WriteLine("\n[Simulator] ── Packet 1: plain GPS record ──────────────────");
    byte[] pkt1 = BuildPacket(includeRfid: false);
    PrintHex("Sending", pkt1);
    await stream.WriteAsync(pkt1);

    uint ack1 = await ReadAck(stream);
    Console.WriteLine($"[Simulator] ACK = {ack1} record(s)");

    await Task.Delay(500);

    // ── Step 3: GPS + IO 109 RFID packet ──────────────────────────────
    Console.WriteLine("\n[Simulator] ── Packet 2: GPS + IO 109 (RFID delimiter) ────");
    byte[] pkt2 = BuildPacket(includeRfid: true);
    PrintHex("Sending", pkt2);
    await stream.WriteAsync(pkt2);

    uint ack2 = await ReadAck(stream);
    Console.WriteLine($"[Simulator] ACK = {ack2} record(s)");

    Console.WriteLine("\n[Simulator] Done. Press Enter to exit.");
    Console.ReadLine();
  }

  // -----------------------------------------------------------------------
  // Packet builders
  // -----------------------------------------------------------------------

  /// <summary>
  /// Builds a full TCP AVL frame (preamble + length + AVL data + CRC).
  /// Contains one Codec 8E record.
  /// </summary>
  private static byte[] BuildPacket(bool includeRfid)
  {
    byte[] avlData = BuildAvlData(includeRfid);
    uint crc = ComputeCrc16(avlData);

    // Frame: [4 preamble][4 dataLen][avlData][4 CRC]
    byte[] frame = new byte[4 + 4 + avlData.Length + 4];
    int off = 0;

    // Preamble
    frame[off++] = 0x00; frame[off++] = 0x00; frame[off++] = 0x00; frame[off++] = 0x00;

    // Data length
    WriteUInt32(frame, ref off, (uint)avlData.Length);

    // AVL data
    Array.Copy(avlData, 0, frame, off, avlData.Length);
    off += avlData.Length;

    // CRC as uint32 big-endian (upper 2 bytes always 0x0000)
    frame[off++] = 0x00;
    frame[off++] = 0x00;
    frame[off++] = (byte)(crc >> 8);
    frame[off++] = (byte)(crc & 0xFF);

    return frame;
  }

  /// <summary>
  /// Builds the AVL data section (Codec ID + records + trailing count).
  /// </summary>
  private static byte[] BuildAvlData(bool includeRfid)
  {
    using var ms = new System.IO.MemoryStream();

    // Codec 8E
    ms.WriteByte(0x8E);
    // Record count (leading = 1)
    ms.WriteByte(0x01);

    // ── AVL Record ─────────────────────────────────────────────────────

    // Timestamp: current UTC in milliseconds (int64 big-endian)
    long tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    WriteInt64BE(ms, tsMs);

    // Priority: 1 = HIGH (required for IO 109 RFID records, fine for GPS too)
    ms.WriteByte(0x01);

    // ── GPS element (15 bytes) ──────────────────────────────────────────
    // Coordinates: Beirut city centre (~33.8958°N, 35.4784°E)
    WriteInt32BE(ms, 354784000);   // Longitude ×10^7 (35.4784 °E)
    WriteInt32BE(ms, 338958000);   // Latitude  ×10^7 (33.8958 °N)
    WriteUInt16BE(ms, 46);         // Altitude: 46 m
    WriteUInt16BE(ms, 90);         // Angle: 90° (east)
    ms.WriteByte(9);               // Satellites: 9
    WriteUInt16BE(ms, 60);         // Speed: 60 km/h

    // ── IO element ─────────────────────────────────────────────────────
    ushort eventIoId = includeRfid ? (ushort)109 : (ushort)0;
    WriteUInt16BE(ms, eventIoId);              // Event IO ID
    WriteUInt16BE(ms, (ushort)(includeRfid ? 1 : 0)); // Total IO count

    WriteUInt16BE(ms, 0);   // N1 count = 0
    WriteUInt16BE(ms, 0);   // N2 count = 0
    WriteUInt16BE(ms, 0);   // N4 count = 0
    WriteUInt16BE(ms, 0);   // N8 count = 0

    if (includeRfid)
    {
      // NX group (only written when eventIoId == 109 per FMC125 spec)
      WriteUInt16BE(ms, 1); // NX count = 1

      // IO 109 element:
      //   ID     = 109 (0x006D)
      //   Length = 2 (envelope header) + 25 (payload) = 27 bytes
      //   Value  = [Index=0x00][DeclaredLen=0x19][CF 00 00 01 12 00 FD BC
      //             01 00 0C DA 20 23 00 00 00 00 00 22 51 91 CA 24 92]
      //
      // The sample payload is the raw RS-232 frame from the CF691 reader
      // as reported in the original project specification.
      byte[] rfidPayload = new byte[]
      {
        0xCF, 0x00, 0x00, 0x01, 0x12, 0x00, 0xFD, 0xBC,
        0x01, 0x00, 0x0C, 0xDA, 0x20, 0x23, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x22, 0x51, 0x91, 0xCA, 0x24, 0x92
      };

      // Build the IO 109 value envelope: [Index][DeclaredLen][Payload]
      byte[] ioValue = new byte[2 + rfidPayload.Length];
      ioValue[0] = 0x00;                        // Index (sequence 0)
      ioValue[1] = (byte)rfidPayload.Length;   // DeclaredLen = 25
      Array.Copy(rfidPayload, 0, ioValue, 2, rfidPayload.Length);

      WriteUInt16BE(ms, 109);                    // NX IO ID = 109
      WriteUInt16BE(ms, (ushort)ioValue.Length); // NX value length = 27
      ms.Write(ioValue);                         // NX value bytes
    }

    // Record count (trailing = 1, must match leading)
    ms.WriteByte(0x01);

    return ms.ToArray();
  }

  // -----------------------------------------------------------------------
  // ACK reader
  // -----------------------------------------------------------------------

  private static async Task<uint> ReadAck(NetworkStream stream)
  {
    byte[] buf = await BigEndianReader.ReadExactAsync(stream, 4, CancellationToken.None);
    return (uint)(buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3]);
  }

  // -----------------------------------------------------------------------
  // CRC-16/IBM  (poly 0x8005, reflected = 0xA001; same as TeltonikaPacketParser)
  // -----------------------------------------------------------------------

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table()
  {
    const ushort poly = 0xA001;
    var table = new ushort[256];
    for (int i = 0; i < 256; i++)
    {
      ushort crc = (ushort)i;
      for (int j = 0; j < 8; j++)
        crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ poly) : (ushort)(crc >> 1);
      table[i] = crc;
    }
    return table;
  }

  private static uint ComputeCrc16(byte[] data)
  {
    ushort crc = 0;
    foreach (byte b in data)
    {
      byte idx = (byte)(crc ^ b);
      crc = (ushort)((crc >> 8) ^ Crc16Table[idx]);
    }
    return crc;
  }

  // -----------------------------------------------------------------------
  // Big-endian write helpers
  // -----------------------------------------------------------------------

  private static void WriteUInt32(byte[] buf, ref int off, uint v)
  {
    buf[off++] = (byte)(v >> 24); buf[off++] = (byte)(v >> 16);
    buf[off++] = (byte)(v >> 8); buf[off++] = (byte)(v & 0xFF);
  }

  private static void WriteInt64BE(System.IO.Stream s, long v)
  {
    ulong u = (ulong)v;
    s.WriteByte((byte)(u >> 56)); s.WriteByte((byte)(u >> 48));
    s.WriteByte((byte)(u >> 40)); s.WriteByte((byte)(u >> 32));
    s.WriteByte((byte)(u >> 24)); s.WriteByte((byte)(u >> 16));
    s.WriteByte((byte)(u >> 8)); s.WriteByte((byte)(u & 0xFF));
  }

  private static void WriteInt32BE(System.IO.Stream s, int v) =>
      WriteUInt32BE(s, (uint)v);

  private static void WriteUInt32BE(System.IO.Stream s, uint v)
  {
    s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
    s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v & 0xFF));
  }

  private static void WriteUInt16BE(System.IO.Stream s, ushort v)
  {
    s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v & 0xFF));
  }

  // -----------------------------------------------------------------------
  // Diagnostic helpers
  // -----------------------------------------------------------------------

  private static void PrintHex(string label, byte[] data)
  {
    const int lineLen = 16;
    Console.WriteLine($"[Simulator] {label} ({data.Length} bytes):");
    for (int i = 0; i < data.Length; i += lineLen)
    {
      int count = Math.Min(lineLen, data.Length - i);
      Console.Write($"  {i:X4}  ");
      for (int j = 0; j < count; j++)
        Console.Write($"{data[i + j]:X2} ");
      Console.WriteLine();
    }
  }
}
