namespace FMC125.Parsing.Models;

/// <summary>
/// A single AVL record contained in a Codec 8E packet.
/// </summary>
public sealed class AvlRecord
{
  /// <summary>UTC timestamp (Unix epoch milliseconds).</summary>
  public long TimestampMs { get; init; }

  /// <summary>Record priority (0 = low, 1 = high, 2 = panic).</summary>
  public byte Priority { get; init; }

  /// <summary>GPS fix for this record.</summary>
  public GpsElement Gps { get; init; } = new();

  /// <summary>Event IO ID that triggered this record (0 = none).</summary>
  public ushort EventIoId { get; init; }

  /// <summary>All IO elements parsed from this record.</summary>
  public IReadOnlyList<IoElement> IoElements { get; init; } = [];

  /// <summary>UTC timestamp as a <see cref="DateTimeOffset"/>.</summary>
  public DateTimeOffset Timestamp =>
      DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

  /// <summary>Convenience: returns the IoElement with the specified ID, or null.</summary>
  public IoElement? GetIo(ushort id) =>
      IoElements.FirstOrDefault(io => io.Id == id);

  public override string ToString() =>
      $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Priority={Priority}, {Gps}, IOs={IoElements.Count}";
}
