# FMC125 Teltonika TCP Server

A .NET 10 console application that accepts TCP connections from Teltonika FMC125
devices, parses their Codec 8 Extended (Codec 8E) AVL packets, decodes RFID data
reported through IO element 109, and saves records to a JSON file.

## Features

- Teltonika IMEI handshake and concurrent TCP device connections.
- Codec 8E AVL frame parsing, including GPS data and fixed- and variable-length
  IO elements.
- CRC-16/IBM verification and record-count acknowledgements. Frames with an
  invalid CRC receive an ACK of `0` so the device can retransmit.
- RFID payload extraction from IO ID 109, with raw payload and extraction details
  included in the output.
- A built-in simulator that sends sample GPS and RFID packets to the server.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows, macOS, or Linux

## Run the server

From the repository directory:

```sh
dotnet run --project FMC125.csproj
```

The server listens on TCP port `5027` on all network interfaces by default. To
choose a different port, pass it as the first argument:

```sh
dotnet run --project FMC125.csproj -- 6000
```

Press `Ctrl+C` to stop the server gracefully. Each valid AVL record is appended
to `avl_records.json` in the current working directory. Set
`TeltonikaTcpServer.JsonOutputPath` to another path when configuring the server
in code; set it to an empty or null value to disable JSON output.

## Try it with the simulator

Start the server in one terminal, then start the simulator in another:

```sh
dotnet run --project FMC125.csproj -- simulate
```

By default, the simulator connects to `127.0.0.1:5027` and sends one GPS-only
record and one record containing sample RFID data. To connect to another host
and port:

```sh
dotnet run --project FMC125.csproj -- simulate <host> <port>
```

For example, `dotnet run --project FMC125.csproj -- simulate 192.0.2.10 5027`.
The simulator waits for you to press Enter when it finishes.

## Configure an FMC125

Configure the device's server connection to use the reachable IP address or
hostname of the machine running this application and the server's TCP port
(default `5027`). Network firewalls and any hosting or tunnel configuration must
allow inbound TCP traffic on that port. For RFID data through IO ID 109, configure
the device's RS-232 serial input in Delimiter mode and use compatible firmware
and reader settings.

## Output

`avl_records.json` contains a JSON array of received records. Each entry includes
the device IMEI, receive time, device timestamp, priority, GPS fields, IO
elements, and (when present) decoded RFID data such as raw hex, tag ID, serial
number, and extraction method. The program also prints connection, packet, CRC,
acknowledgement, and RFID details to the console.

## Project layout

| Path | Purpose |
| --- | --- |
| `Program.cs` | Server and simulator command-line entry point |
| `Server/` | TCP listener, IMEI handshake, packet reception, and acknowledgements |
| `Parsing/` | Codec 8E frame parser and AVL record models |
| `Rfid/` | IO 109 RFID payload decoder |
| `Output/` | JSON record writer |
| `Utils/` | Big-endian byte and network-stream helpers |
| `Tests/` | Device simulator for local protocol exercises |

## Notes

- This repository currently includes a simulator, not a separate automated test
  project.
- The server binds to all network interfaces by default. Restrict network access
  appropriately when running it on a shared or internet-facing host.
- RFID extraction supports heuristic and sample-based reader formats; validate
  decoded tag fields against the specific reader and payload format in use.
- No license is specified yet. Add a license file before redistributing the
  project if you intend to grant reuse rights.
