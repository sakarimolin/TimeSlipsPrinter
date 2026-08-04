using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TimeSlipsPrinter;

public sealed class MainForm : Form
{
    private readonly TextBox _bindAddress = new() { Text = "0.0.0.0", Dock = DockStyle.Fill };
    private readonly TextBox _captureDirectory = new() { Text = @"D:\Omat\test\TimeSlipsPrinter\captures", Dock = DockStyle.Fill };
    private readonly NumericUpDown _idleSeconds = new() { Minimum = 0.1m, Maximum = 30, DecimalPlaces = 1, Increment = 0.1m, Value = 1.0m, Dock = DockStyle.Left, Width = 110 };
    private readonly TextBox _discoveryReply = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional hex bytes; sent verbatim to UDP discovery requester" };
    private readonly CheckBox _sendSdpProbe = new() { Text = "Send minimal Star SDP probe reply (experimental)", AutoSize = true, Checked = true };
    private readonly TextBox _statusReply = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional hex bytes; sent after TCP data is received" };
    private readonly CheckBox _echoStatus = new() { Text = "Echo TCP 9101 bytes (diagnostic only)", AutoSize = true };
    private readonly Button _start = new() { Text = "Start listening", AutoSize = true };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly Button _openCaptureFolder = new() { Text = "Open captures", AutoSize = true };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, Font = new Font("Consolas", 9), WordWrap = false };

    private CancellationTokenSource? _cancellation;
    private UdpClient? _udp;
    private TcpListener? _printListener;
    private TcpListener? _statusListener;
    private string _activeCaptureDirectory = "";
    private double _activeIdleSeconds;
    private readonly object _eventFileLock = new();

    public MainForm()
    {
        Text = "Time Slips Printer – Star LAN capture";
        MinimumSize = new Size(850, 620);
        Size = new Size(1050, 760);
        BuildLayout();
        FormClosing += async (_, _) => await StopAsync();
    }

    private void BuildLayout()
    {
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), ColumnCount = 2 };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(settings, "Listen address", _bindAddress);
        AddRow(settings, "Capture folder", _captureDirectory);
        AddRow(settings, "Idle timeout (seconds)", _idleSeconds);
        AddRow(settings, "UDP discovery reply", _discoveryReply);
        AddRow(settings, "Discovery test", _sendSdpProbe);
        AddRow(settings, "TCP status reply", _statusReply);
        AddRow(settings, "TCP 9101", _echoStatus);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        buttons.Controls.AddRange([_start, _stop, _openCaptureFolder]);
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += async (_, _) => await StopAsync();
        _openCaptureFolder.Click += (_, _) => OpenCaptureFolder();

        var logLabel = new Label { Text = "Activity log", Dock = DockStyle.Top, Padding = new Padding(10, 5, 10, 5), AutoSize = true };
        Controls.Add(_log);
        Controls.Add(logLabel);
        Controls.Add(buttons);
        Controls.Add(settings);
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control control)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 7, 8, 3) }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private async Task StartAsync()
    {
        if (_cancellation is not null) return;
        if (!IPAddress.TryParse(_bindAddress.Text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            MessageBox.Show(this, "Enter a valid IPv4 listen address, for example 0.0.0.0.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        byte[] discoveryReply;
        byte[] statusReply;
        try
        {
            discoveryReply = ParseHex(_discoveryReply.Text);
            statusReply = ParseHex(_statusReply.Text);
        }
        catch (FormatException exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var useSdpProbe = discoveryReply.Length == 0 && _sendSdpProbe.Checked;

        try
        {
            _activeCaptureDirectory = Path.GetFullPath(_captureDirectory.Text.Trim());
            _activeIdleSeconds = (double)_idleSeconds.Value;
            Directory.CreateDirectory(_activeCaptureDirectory);
            _udp = new UdpClient(new IPEndPoint(address, 22222));
            _printListener = new TcpListener(address, 9100);
            _statusListener = new TcpListener(address, 9101);
            _printListener.Start();
            _statusListener.Start();
        }
        catch (SocketException exception)
        {
            DisposeListeners();
            MessageBox.Show(this, $"Could not open the Star ports.\n\n{exception.Message}\n\nCheck Windows Firewall and whether another program is already using the ports.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _cancellation = new CancellationTokenSource();
        SetRunning(true);
        WriteLog($"Listening on {address}: UDP 22222, TCP 9100, TCP 9101");
        WriteLog($"Saving captures to {_activeCaptureDirectory}");
        if (discoveryReply.Length == 0 && !useSdpProbe) WriteLog("Discovery reply is OFF: capture-only mode.");
        else if (useSdpProbe) WriteLog("Discovery reply is ON: using experimental request-aware Star SDP probe reply.");
        else WriteLog($"Discovery reply is ON: using {discoveryReply.Length} configured hexadecimal bytes.");
        _ = Task.Run(() => ReceiveUdpAsync(discoveryReply, useSdpProbe, _cancellation.Token));
        _ = Task.Run(() => AcceptTcpAsync(_printListener, "tcp9100_print", statusReply, false, _cancellation.Token));
        _ = Task.Run(() => AcceptTcpAsync(_statusListener, "tcp9101_status", statusReply, _echoStatus.Checked, _cancellation.Token));
        await Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        var cancellation = _cancellation;
        if (cancellation is null) return;
        _cancellation = null;
        cancellation.Cancel();
        DisposeListeners();
        cancellation.Dispose();
        SetRunning(false);
        WriteLog("Stopped listening.");
        await Task.CompletedTask;
    }

    private void DisposeListeners()
    {
        _udp?.Dispose(); _udp = null;
        _printListener?.Stop(); _printListener = null;
        _statusListener?.Stop(); _statusListener = null;
    }

    private async Task ReceiveUdpAsync(byte[] configuredReply, bool useSdpProbe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _udp is not null)
            {
                var result = await _udp.ReceiveAsync(cancellationToken);
                var saved = SaveCapture("udp22222", result.RemoteEndPoint, result.Buffer);
                WriteLog($"UDP discovery from {result.RemoteEndPoint} ({result.Buffer.Length} bytes) → {saved.Name}");
                var reply = useSdpProbe ? MinimalSdpProbeReply(result.Buffer) : configuredReply;
                if (reply.Length > 0)
                {
                    await _udp.SendAsync(reply, result.RemoteEndPoint, cancellationToken);
                    var replyCapture = SaveCapture("udp22222_reply", result.RemoteEndPoint, reply, "sent");
                    WriteLog($"Sent configured UDP reply ({reply.Length} bytes) to {result.RemoteEndPoint} → {replyCapture.Name}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { WriteLog($"UDP listener error: {exception.Message}"); }
    }

    private async Task AcceptTcpAsync(TcpListener listener, string kind, byte[] reply, bool echo, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleTcpClientAsync(client, kind, reply, echo, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException exception) when (cancellationToken.IsCancellationRequested) { _ = exception; }
        catch (Exception exception) { WriteLog($"{kind} listener error: {exception.Message}"); }
    }

    private async Task HandleTcpClientAsync(TcpClient client, string kind, byte[] reply, bool echo, CancellationToken cancellationToken)
    {
        using (client)
        {
            var peer = (IPEndPoint?)client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);
            var received = new MemoryStream();
            var sent = new MemoryStream();
            try
            {
                using var stream = client.GetStream();
                var buffer = new byte[65_536];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var count = await ReadWithIdleTimeoutAsync(stream, buffer, cancellationToken);
                    if (count == 0) break;
                    received.Write(buffer, 0, count);
                    if (reply.Length > 0)
                    {
                        await stream.WriteAsync(reply, cancellationToken);
                        sent.Write(reply, 0, reply.Length);
                    }
                    else if (echo)
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        sent.Write(buffer, 0, count);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException exception) { WriteLog($"{kind} client {peer}: {exception.Message}"); }
            finally
            {
                if (received.Length > 0)
                {
                    var saved = SaveCapture(kind, peer, received.ToArray());
                    WriteLog($"Captured {kind} from {peer} ({received.Length} bytes) → {saved.Name}");
                }
                if (sent.Length > 0)
                {
                    var saved = SaveCapture(kind + "_reply", peer, sent.ToArray(), "sent");
                    WriteLog($"Captured reply to {peer} ({sent.Length} bytes) → {saved.Name}");
                }
            }
        }
    }

    private async Task<int> ReadWithIdleTimeoutAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_activeIdleSeconds));
        try { return await stream.ReadAsync(buffer, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return 0; }
    }

    private FileInfo SaveCapture(string kind, IPEndPoint peer, byte[] bytes, string direction = "received")
    {
        var root = _activeCaptureDirectory;
        Directory.CreateDirectory(root);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss_fffffffZ");
        var name = $"{stamp}_{kind}_{peer.Address}_{peer.Port}".Replace(':', '-');
        var dataPath = Path.Combine(root, name + ".bin");
        File.WriteAllBytes(dataPath, bytes);
        var metadata = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            kind,
            direction,
            peer = new { address = peer.Address.ToString(), port = peer.Port },
            bytes = bytes.Length,
            file = Path.GetFileName(dataPath),
            hexPreview = Convert.ToHexString(bytes[..Math.Min(bytes.Length, 96)]),
            asciiPreview = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 96)])
        };
        lock (_eventFileLock)
            File.AppendAllText(Path.Combine(root, "events.jsonl"), JsonSerializer.Serialize(metadata) + Environment.NewLine);
        return new FileInfo(dataPath);
    }

    private void OpenCaptureFolder()
    {
        var path = Path.GetFullPath(_captureDirectory.Text.Trim());
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void SetRunning(bool running)
    {
        _start.Enabled = !running;
        _stop.Enabled = running;
        _bindAddress.Enabled = !running;
        _captureDirectory.Enabled = !running;
        _idleSeconds.Enabled = !running;
        _discoveryReply.Enabled = !running;
        _sendSdpProbe.Enabled = !running;
        _statusReply.Enabled = !running;
        _echoStatus.Enabled = !running;
    }

    private void WriteLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => WriteLog(message)); return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private static byte[] ParseHex(string input)
    {
        var compact = new string(input.Where(Uri.IsHexDigit).ToArray());
        if (compact.Length == 0) return [];
        if (compact.Length != input.Count(Uri.IsHexDigit) || compact.Length % 2 != 0)
            throw new FormatException("Hex replies must contain only hexadecimal digits and whitespace, with an even number of digits.");
        return Convert.FromHexString(compact);
    }

    private static byte[] MinimalSdpProbeReply(byte[] request)
    {
        // SDP starts with a 16-byte magic header, then a null-terminated RQx.0.0
        // revision field. Reply with the corresponding RSx.0.1 field and preserve
        // the request-specific trailing bytes. This is only a TCP-phase probe: it
        // does not yet include a model/IP/MAC identity from a real Star printer.
        if (request.Length < 24 || !request.AsSpan(0, 9).SequenceEqual("STR_BCAST"u8))
            return [];

        var response = new byte[request.Length];
        Encoding.ASCII.GetBytes("STR_RSP").CopyTo(response, 0);
        var revision = request[18]; // RQ1.0.0 or RQ4.0.0
        Encoding.ASCII.GetBytes($"RS{(char)revision}.0.1\0").CopyTo(response, 16);
        request.AsSpan(24).CopyTo(response.AsSpan(24));
        return response;
    }
}
