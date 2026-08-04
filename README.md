# Time Slips Printer

Windows Forms application for capturing Star Micronics LAN printer traffic from FHRA Slips.

Open `TimeSlipsPrinter.csproj` in Visual Studio 2022 (or newer), press **F5**, and then press
**Start listening** in the application. It listens on UDP `22222`, TCP `9100`, and TCP `9101`.
Every packet and print job is saved in the configured capture folder, alongside `events.jsonl`.
Both received packets/jobs and the emulator's sent replies are written to `events.jsonl`; use the
`direction` property to distinguish them.

## First test

1. Put the Android phone and PC on the same Wi-Fi network.
2. Allow inbound UDP 22222 and TCP 9100/9101 on the Windows **Private** firewall profile.
3. Start the listener and attempt printer discovery or printing in FHRA Slips.
4. Inspect the activity log and capture folder.

Initially leave both reply fields blank. The first run is capture-only and establishes the exact
discovery/status data expected by this version of FHRA Slips. If the app permits a manual printer IP,
enter the PC's IPv4 address and it may send a raw job directly to TCP 9100.

After you have seen a `STR_BCAST` request, you can enable **Send minimal Star SDP probe reply
(experimental)** and test again. It is enabled by default and replies with the SDP header/version only. If the app then opens a
TCP 9100 connection, the network path is confirmed; it will still require a model-specific SDP identity
and printer-status replies before it can consider the virtual printer usable.

## Reply fields

The optional reply fields accept hexadecimal bytes, with spaces allowed. A configured **UDP discovery
reply** is returned verbatim to every UDP request. A configured **TCP status reply** is returned after
each incoming TCP chunk. This supports protocol tuning from a packet capture without rebuilding the
application.

Do not run the listener on an untrusted or public network. Captured `.bin` files are raw Star printer
command streams, not necessarily PDFs. A later decoding step can convert known receipt command/raster
formats to images or PDF.
