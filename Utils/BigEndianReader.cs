using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace FMC125.Utils;

/// <summary>
/// Provides big-endian read helpers over a raw byte span, and an async
/// helper for reading an exact number of bytes from a <see cref="NetworkStream"/>.
/// </summary>
public static class BigEndianReader
{
  // -----------------------------------------------------------------------
  // Span-based readers (advance a ref index into a byte array)
  // -----------------------------------------------------------------------

  public static byte ReadByte(ReadOnlySpan<byte> buffer, ref int offset)
  {
    byte value = buffer[offset];
    offset += 1;
    return value;
  }

  public static ushort ReadUInt16(ReadOnlySpan<byte> buffer, ref int offset)
  {
    ushort value = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
    offset += 2;
    return value;
  }

  public static short ReadInt16(ReadOnlySpan<byte> buffer, ref int offset)
  {
    short value = BinaryPrimitives.ReadInt16BigEndian(buffer[offset..]);
    offset += 2;
    return value;
  }

  public static uint ReadUInt32(ReadOnlySpan<byte> buffer, ref int offset)
  {
    uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer[offset..]);
    offset += 4;
    return value;
  }

  public static int ReadInt32(ReadOnlySpan<byte> buffer, ref int offset)
  {
    int value = BinaryPrimitives.ReadInt32BigEndian(buffer[offset..]);
    offset += 4;
    return value;
  }

  public static ulong ReadUInt64(ReadOnlySpan<byte> buffer, ref int offset)
  {
    ulong value = BinaryPrimitives.ReadUInt64BigEndian(buffer[offset..]);
    offset += 8;
    return value;
  }

  public static long ReadInt64(ReadOnlySpan<byte> buffer, ref int offset)
  {
    long value = BinaryPrimitives.ReadInt64BigEndian(buffer[offset..]);
    offset += 8;
    return value;
  }

  /// <summary>Reads <paramref name="count"/> bytes and advances the offset.</summary>
  public static byte[] ReadBytes(ReadOnlySpan<byte> buffer, int count, ref int offset)
  {
    byte[] value = buffer.Slice(offset, count).ToArray();
    offset += count;
    return value;
  }

  // -----------------------------------------------------------------------
  // Async helpers for NetworkStream
  // -----------------------------------------------------------------------

  /// <summary>
  /// Reads exactly <paramref name="count"/> bytes from <paramref name="stream"/>,
  /// retrying as needed until all bytes have been received.
  /// Throws <see cref="EndOfStreamException"/> if the connection closes early.
  /// </summary>
  public static async Task<byte[]> ReadExactAsync(
      NetworkStream stream,
      int count,
      CancellationToken ct = default)
  {
    byte[] buffer = new byte[count];
    int totalRead = 0;

    while (totalRead < count)
    {
      int bytesRead = await stream.ReadAsync(
          buffer.AsMemory(totalRead, count - totalRead), ct);

      if (bytesRead == 0)
        throw new EndOfStreamException(
            $"Connection closed after reading {totalRead}/{count} bytes.");

      totalRead += bytesRead;
    }

    return buffer;
  }

  // -----------------------------------------------------------------------
  // Encoding helpers
  // -----------------------------------------------------------------------

  /// <summary>
  /// Returns true when <paramref name="bytes"/> contains only printable ASCII
  /// characters (0x20–0x7E).
  /// </summary>
  public static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
  {
    foreach (byte b in bytes)
    {
      if (b < 0x20 || b > 0x7E)
        return false;
    }
    return true;
  }

  /// <summary>Encodes a 4-byte big-endian ACK (record count) for the device.</summary>
  public static byte[] BuildAck(int recordCount)
  {
    byte[] ack = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(ack, recordCount);
    return ack;
  }

  /// <summary>Returns a nicely formatted hex dump string (uppercase, space-separated).</summary>
  public static string ToHexString(ReadOnlySpan<byte> data)
  {
    if (data.IsEmpty) return string.Empty;

    var sb = new StringBuilder(data.Length * 3);
    for (int i = 0; i < data.Length; i++)
    {
      if (i > 0) sb.Append(' ');
      sb.Append(data[i].ToString("X2"));
    }
    return sb.ToString();
  }
}
