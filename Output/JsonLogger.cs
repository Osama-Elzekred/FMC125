using System.Text.Json;
using System.Text.Json.Serialization;
using FMC125.Parsing.Models;
using FMC125.Rfid;

namespace FMC125.Output;

/// <summary>
/// Writes AVL records to a pretty-printed JSON array file.
/// Every new record is added to an in-memory list and the whole file is
/// rewritten so the result is always valid, readable JSON:
///
///   [
///     { ... record 1 ... },
///     { ... record 2 ... }
///   ]
///
/// Thread-safe: multiple device connections write concurrently via a semaphore.
/// </summary>
public sealed class JsonLogger : IAsyncDisposable
{
  private readonly string _path;
  private readonly SemaphoreSlim _lock = new(1, 1);
  private readonly List<AvlRecordEntry> _records = [];

  private static readonly JsonSerializerOptions JsonOpts = new()
  {
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
  };

  public JsonLogger(string path)
  {
    _path = path;
    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir))
      Directory.CreateDirectory(dir);
  }

  // -----------------------------------------------------------------------
  // Public API
  // -----------------------------------------------------------------------

  /// <summary>
  /// Adds one AVL record (plus optional RFID result) to the JSON array
  /// and rewrites the output file.
  /// </summary>
  public async Task WriteRecordAsync(
      string imei,
      AvlRecord record,
      RfidDecoder.RfidResult? rfid = null)
  {
    var entry = new AvlRecordEntry
    {
      ReceivedAt = DateTimeOffset.UtcNow,
      Imei = imei,
      TimestampMs = record.TimestampMs,
      Timestamp = record.Timestamp,
      Priority = record.Priority,
      EventIoId = record.EventIoId,
      Gps = new GpsEntry
      {
        Latitude = record.Gps.Latitude,
        Longitude = record.Gps.Longitude,
        Altitude = record.Gps.Altitude,
        Angle = record.Gps.Angle,
        Satellites = record.Gps.Satellites,
        Speed = record.Gps.Speed,
        HasFix = record.Gps.HasFix,
      },
      IoElements = record.IoElements
          .Select(io => new IoEntry
          {
            Id = io.Id,
            HexValue = io.HexValue,
            NumericValue = io.IsVariableLength ? null : io.NumericValue,
            IsVariableLength = io.IsVariableLength,
          })
          .ToList(),
      Rfid = rfid is null ? null : new RfidEntry
      {
        PacketIndex = rfid.PacketIndex,
        DeclaredDataLength = rfid.DeclaredDataLength,
        RawHex = rfid.RawHex,
        AsciiText = rfid.AsciiText,
        TagId = rfid.TagId,
        SerialNumber = rfid.SerialNumber,
        ExtractionMethod = rfid.ExtractionMethod,
      },
    };

    await _lock.WaitAsync();
    try
    {
      _records.Add(entry);
      string json = JsonSerializer.Serialize(_records, JsonOpts);
      await File.WriteAllTextAsync(_path, json);
    }
    finally
    {
      _lock.Release();
    }
  }

  public async ValueTask DisposeAsync()
  {
    _lock.Dispose();
    await ValueTask.CompletedTask;
  }

  // -----------------------------------------------------------------------
  // JSON DTO types
  // -----------------------------------------------------------------------

  private sealed class AvlRecordEntry
  {
    public DateTimeOffset ReceivedAt { get; set; }
    public string Imei { get; set; } = "";
    public long TimestampMs { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public byte Priority { get; set; }
    public ushort EventIoId { get; set; }
    public GpsEntry Gps { get; set; } = new();
    public List<IoEntry> IoElements { get; set; } = [];
    public RfidEntry? Rfid { get; set; }
  }

  private sealed class GpsEntry
  {
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public ushort Altitude { get; set; }
    public ushort Angle { get; set; }
    public byte Satellites { get; set; }
    public ushort Speed { get; set; }
    public bool HasFix { get; set; }
  }

  private sealed class IoEntry
  {
    public ushort Id { get; set; }
    public string HexValue { get; set; } = "";
    public ulong? NumericValue { get; set; }
    public bool IsVariableLength { get; set; }
  }

  private sealed class RfidEntry
  {
    public byte PacketIndex { get; set; }
    public byte DeclaredDataLength { get; set; }
    public string RawHex { get; set; } = "";
    public string? AsciiText { get; set; }
    public string TagId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string ExtractionMethod { get; set; } = "";
  }
}
