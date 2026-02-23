using FMC125.Parsing.Models;
using FMC125.Utils;

namespace FMC125.Parsing;

/// <summary>
/// Parses raw Teltonika TCP frames into <see cref="AvlPacket"/> objects.
///
/// TCP frame layout:
///   [4]  Preamble         = 0x00000000
///   [4]  Data length      (big-endian uint32, number of AVL-data bytes)
///   [N]  AVL data         (codec ID + records + record count)
///   [4]  CRC-16/IBM       (stored in the lower 16 bits of a uint32; upper 2 bytes are always 0x0000)
///
/// Codec 8 Extended (0x8E) AVL data:
///   [1]  Codec ID         = 0x8E
///   [1]  Number of records
///   ... AVL records ...
///   [1]  Number of records (repeated — must equal the first byte)
///
/// Each AVL record:
///   [8]  Timestamp        ms since Unix epoch, big-endian int64
///   [1]  Priority
///   [15] GPS element
///   [?]  IO element       (see ParseIoElement)
///
/// GPS element (15 bytes):
///   [4]  Longitude        signed int32 × 10^-7 → decimal degrees
///   [4]  Latitude         signed int32 × 10^-7 → decimal degrees
///   [2]  Altitude         uint16, metres
///   [2]  Angle            uint16, degrees (0–360)
///   [1]  Satellites       uint8
///   [2]  Speed            uint16, km/h
///
/// IO element (Codec 8E) — all group counts are 2-byte (ushort):
///   [2]  Event IO ID
///   [2]  Total IO count
///   [2]  N1  + N1 × ([2] IO ID + [1] value)
///   [2]  N2  + N2 × ([2] IO ID + [2] value)
///   [2]  N4  + N4 × ([2] IO ID + [4] value)
///   [2]  N8  + N8 × ([2] IO ID + [8] value)
///   [2]  NX  + NX × ([2] IO ID + [2] length + [length] value)
/// </summary>
public static class TeltonikaPacketParser
{
  public const byte Codec8ExtendedId = 0x8E;

  // -----------------------------------------------------------------------
  // Public entry point
  // -----------------------------------------------------------------------

  /// <summary>
  /// Parses a complete TCP AVL frame (preamble + length + data + CRC) from
  /// <paramref name="rawFrame"/>.
  /// </summary>
  /// <exception cref="InvalidDataException">
  /// Thrown when the frame is structurally invalid (bad preamble, wrong codec,
  ///  record count mismatch, etc.).
  /// </exception>
  public static AvlPacket ParseFrame(byte[] rawFrame)
  {
    ArgumentNullException.ThrowIfNull(rawFrame);

    if (rawFrame.Length < 12)
      throw new InvalidDataException(
          $"Frame too short: {rawFrame.Length} bytes (minimum 12).");

    int offset = 0;
    ReadOnlySpan<byte> span = rawFrame;

    // --- Preamble ---
    uint preamble = BigEndianReader.ReadUInt32(span, ref offset);
    if (preamble != 0x00000000)
      throw new InvalidDataException(
          $"Invalid preamble: 0x{preamble:X8} (expected 0x00000000).");

    // --- Data length ---
    uint dataLength = BigEndianReader.ReadUInt32(span, ref offset);
    if (rawFrame.Length < 8 + (int)dataLength + 4)
      throw new InvalidDataException(
          $"Frame buffer too small: declared data length={dataLength}, " +
          $"available={rawFrame.Length - 12} bytes.");

    // --- AVL data ---
    byte[] avlData = BigEndianReader.ReadBytes(span, (int)dataLength, ref offset);

    // --- CRC (stored as uint32, only lower 16 bits are used) ---
    uint receivedCrc = BigEndianReader.ReadUInt32(span, ref offset);
    uint computedCrc = ComputeCrc16(avlData);

    // --- Parse AVL data ---
    var packet = ParseAvlData(avlData);

    // Attach CRC info (create a new packet to avoid mutation on the record)
    return packet with
    {
      ReceivedCrc = receivedCrc,
      ComputedCrc = computedCrc
    };
  }

  // -----------------------------------------------------------------------
  // AVL data parsing
  // -----------------------------------------------------------------------

  private static AvlPacket ParseAvlData(byte[] avlData)
  {
    int offset = 0;
    ReadOnlySpan<byte> span = avlData;

    // Codec ID
    byte codecId = BigEndianReader.ReadByte(span, ref offset);
    if (codecId != Codec8ExtendedId)
      throw new InvalidDataException(
          $"Unsupported codec: 0x{codecId:X2} (only Codec 8E / 0x8E is supported).");

    // Record count (leading)
    byte recordCountLeading = BigEndianReader.ReadByte(span, ref offset);

    // Parse each AVL record
    var records = new List<AvlRecord>(recordCountLeading);
    for (int i = 0; i < recordCountLeading; i++)
    {
      records.Add(ParseAvlRecord(span, ref offset));
    }

    // Record count (trailing — must match)
    byte recordCountTrailing = BigEndianReader.ReadByte(span, ref offset);
    if (recordCountLeading != recordCountTrailing)
      throw new InvalidDataException(
          $"Record count mismatch: leading={recordCountLeading}, " +
          $"trailing={recordCountTrailing}.");

    return new AvlPacket
    {
      CodecId = codecId,
      RecordCount = recordCountLeading,
      Records = records.AsReadOnly()
    };
  }

  // -----------------------------------------------------------------------
  // AVL record parsing
  // -----------------------------------------------------------------------

  private static AvlRecord ParseAvlRecord(ReadOnlySpan<byte> span, ref int offset)
  {
    // Timestamp (8 bytes, signed int64 ms)
    long timestampMs = BigEndianReader.ReadInt64(span, ref offset);

    // Priority (1 byte)
    byte priority = BigEndianReader.ReadByte(span, ref offset);

    // GPS element (15 bytes)
    var gps = ParseGpsElement(span, ref offset);

    // IO element
    ushort eventIoId;
    var ioElements = ParseIoElement(span, ref offset, out eventIoId);

    return new AvlRecord
    {
      TimestampMs = timestampMs,
      Priority = priority,
      Gps = gps,
      EventIoId = eventIoId,
      IoElements = ioElements.AsReadOnly()
    };
  }

  // -----------------------------------------------------------------------
  // GPS element (15 bytes)
  // -----------------------------------------------------------------------

  private static GpsElement ParseGpsElement(ReadOnlySpan<byte> span, ref int offset)
  {
    int lonRaw = BigEndianReader.ReadInt32(span, ref offset);   // signed
    int latRaw = BigEndianReader.ReadInt32(span, ref offset);   // signed
    ushort alt = BigEndianReader.ReadUInt16(span, ref offset);
    ushort angle = BigEndianReader.ReadUInt16(span, ref offset);
    byte sats = BigEndianReader.ReadByte(span, ref offset);
    ushort speed = BigEndianReader.ReadUInt16(span, ref offset);

    return new GpsElement
    {
      Longitude = lonRaw / 10_000_000.0,
      Latitude = latRaw / 10_000_000.0,
      Altitude = alt,
      Angle = angle,
      Satellites = sats,
      Speed = speed
    };
  }

  // -----------------------------------------------------------------------
  // IO element (Codec 8E — all counts are 2-byte ushorts)
  // -----------------------------------------------------------------------

  private static List<IoElement> ParseIoElement(
      ReadOnlySpan<byte> span,
      ref int offset,
      out ushort eventIoId)
  {
    eventIoId = BigEndianReader.ReadUInt16(span, ref offset);

    ushort totalCount = BigEndianReader.ReadUInt16(span, ref offset);
    var elements = new List<IoElement>(totalCount);

    // Group: 1-byte values
    ushort n1 = BigEndianReader.ReadUInt16(span, ref offset);
    for (int i = 0; i < n1; i++)
    {
      ushort ioId = BigEndianReader.ReadUInt16(span, ref offset);
      byte[] value = BigEndianReader.ReadBytes(span, 1, ref offset);
      elements.Add(new IoElement { Id = ioId, Value = value });
    }

    // Group: 2-byte values
    ushort n2 = BigEndianReader.ReadUInt16(span, ref offset);
    for (int i = 0; i < n2; i++)
    {
      ushort ioId = BigEndianReader.ReadUInt16(span, ref offset);
      byte[] value = BigEndianReader.ReadBytes(span, 2, ref offset);
      elements.Add(new IoElement { Id = ioId, Value = value });
    }

    // Group: 4-byte values
    ushort n4 = BigEndianReader.ReadUInt16(span, ref offset);
    for (int i = 0; i < n4; i++)
    {
      ushort ioId = BigEndianReader.ReadUInt16(span, ref offset);
      byte[] value = BigEndianReader.ReadBytes(span, 4, ref offset);
      elements.Add(new IoElement { Id = ioId, Value = value });
    }

    // Group: 8-byte values
    ushort n8 = BigEndianReader.ReadUInt16(span, ref offset);
    for (int i = 0; i < n8; i++)
    {
      ushort ioId = BigEndianReader.ReadUInt16(span, ref offset);
      byte[] value = BigEndianReader.ReadBytes(span, 8, ref offset);
      elements.Add(new IoElement { Id = ioId, Value = value });
    }

    // Group: variable-length ("string type") values
    //
    // FMC125 RS-232 spec note: this group counter is ONLY written into the record
    // when the record was generated by the RS-232 Delimiter mode (EventIoId == 109).
    // Regular periodic/event records omit this byte entirely.
    // Firmware that strictly follows the general Codec 8E spec always writes it
    // (as 0x0000 when empty); FMC125-specific firmware may not.
    // We read it conditionally so that non-delimiter records parse cleanly.
    if (eventIoId == 109)
    {
      ushort nX = BigEndianReader.ReadUInt16(span, ref offset);
      for (int i = 0; i < nX; i++)
      {
        ushort ioId = BigEndianReader.ReadUInt16(span, ref offset);
        ushort length = BigEndianReader.ReadUInt16(span, ref offset);
        byte[] value = BigEndianReader.ReadBytes(span, length, ref offset);
        elements.Add(new IoElement { Id = ioId, Value = value });
      }
    }

    return elements;
  }

  // -----------------------------------------------------------------------
  // CRC-16/IBM (also called CRC-16/ARC — poly 0x8005, reflected, init 0x0000)
  // Teltonika stores this as the lower 16 bits of a 4-byte field.
  // -----------------------------------------------------------------------

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table()
  {
    const ushort poly = 0xA001; // reflected 0x8005
    var table = new ushort[256];

    for (int i = 0; i < 256; i++)
    {
      ushort crc = (ushort)i;
      for (int j = 0; j < 8; j++)
      {
        if ((crc & 1) != 0)
          crc = (ushort)((crc >> 1) ^ poly);
        else
          crc >>= 1;
      }
      table[i] = crc;
    }

    return table;
  }

  public static uint ComputeCrc16(ReadOnlySpan<byte> data)
  {
    ushort crc = 0;
    foreach (byte b in data)
    {
      byte index = (byte)(crc ^ b);
      crc = (ushort)((crc >> 8) ^ Crc16Table[index]);
    }
    return crc;
  }
}
