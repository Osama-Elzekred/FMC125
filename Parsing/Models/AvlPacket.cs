namespace FMC125.Parsing.Models;

/// <summary>
/// A fully parsed Teltonika AVL packet (Codec 8 Extended).
/// Declared as a record to allow non-destructive mutation via 'with' expressions.
/// </summary>
public sealed record AvlPacket
{
  /// <summary>Codec identifier — must be 0x8E for Codec 8 Extended.</summary>
  public byte CodecId { get; init; }

  /// <summary>Number of AVL records in this packet.</summary>
  public byte RecordCount { get; init; }

  /// <summary>Parsed AVL records.</summary>
  public IReadOnlyList<AvlRecord> Records { get; init; } = [];

  /// <summary>CRC-16/IBM checksum read from the packet.</summary>
  public uint ReceivedCrc { get; init; }

  /// <summary>CRC-16/IBM checksum computed over the AVL data bytes.</summary>
  public uint ComputedCrc { get; init; }

  /// <summary>True when the received CRC matches the computed CRC.</summary>
  public bool IsCrcValid => ReceivedCrc == ComputedCrc;

  public override string ToString() =>
      $"Codec=0x{CodecId:X2}, Records={RecordCount}, CRC valid={IsCrcValid}";
}
