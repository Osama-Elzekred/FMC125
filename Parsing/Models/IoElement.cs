namespace FMC125.Parsing.Models;

/// <summary>
/// A single IO element (one property reported by the device).
/// </summary>
public sealed class IoElement
{
  /// <summary>Two-byte IO identifier (Codec 8E).</summary>
  public ushort Id { get; init; }

  /// <summary>Raw value bytes (big-endian for fixed-length, raw for variable-length).</summary>
  public byte[] Value { get; init; } = [];

  /// <summary>Decoded unsigned 64-bit value for fixed-length IOs (1/2/4/8 bytes).</summary>
  public ulong NumericValue => Value.Length switch
  {
    1 => Value[0],
    2 => (ulong)((Value[0] << 8) | Value[1]),
    4 => (ulong)((uint)((Value[0] << 24) | (Value[1] << 16) | (Value[2] << 8) | Value[3])),
    8 => ((ulong)Value[0] << 56) | ((ulong)Value[1] << 48) |
         ((ulong)Value[2] << 40) | ((ulong)Value[3] << 32) |
         ((ulong)Value[4] << 24) | ((ulong)Value[5] << 16) |
         ((ulong)Value[6] << 8) | (ulong)Value[7],
    _ => 0UL
  };

  /// <summary>Raw bytes as an uppercase hex string.</summary>
  public string HexValue => Convert.ToHexString(Value);

  public bool IsVariableLength => Value.Length > 8;

  public override string ToString() =>
      $"IO[{Id}] = 0x{HexValue} ({Value.Length} bytes)";
}
