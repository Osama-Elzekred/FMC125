namespace FMC125.Parsing.Models;

/// <summary>
/// GPS element extracted from an AVL record.
/// Codec 8 / 8E GPS element is always 15 bytes.
/// </summary>
public sealed class GpsElement
{
  /// <summary>Longitude in decimal degrees (positive = East).</summary>
  public double Longitude { get; init; }

  /// <summary>Latitude in decimal degrees (positive = North).</summary>
  public double Latitude { get; init; }

  /// <summary>Altitude in metres above sea level.</summary>
  public ushort Altitude { get; init; }

  /// <summary>Heading angle in degrees (0–360).</summary>
  public ushort Angle { get; init; }

  /// <summary>Number of visible satellites.</summary>
  public byte Satellites { get; init; }

  /// <summary>Speed in km/h.</summary>
  public ushort Speed { get; init; }

  /// <summary>
  /// Indicates whether this record carries a fresh GPS fix.
  /// Per Teltonika protocol: when there is no GPS fix, Longitude, Latitude, and
  /// Altitude hold the last valid fix, while Angle, Satellites <b>and</b> Speed
  /// are all set to 0 by the device.
  /// </summary>
  public bool HasFix => Satellites > 0 || Speed > 0 || Angle > 0;

  public override string ToString() =>
      $"Lat={Latitude:F7}, Lon={Longitude:F7}, Alt={Altitude}m, Angle={Angle}°, Sats={Satellites}, Speed={Speed}km/h, Fix={HasFix}";
}
