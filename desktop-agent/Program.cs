using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

internal static class Program
{
    private const int HttpPort = 8789;
    private const int DiscoveryPort = 38901;
    private const string DiscoveryMagic = "MIRA_MONITOR_DISCOVER_V1";
    private const string StartupTaskName = "Mira System Monitor";
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiraSystemMonitor", "settings.json");
    private static readonly CpuSampler Cpu = new();
    private static readonly HardwareReader Hardware = new();
    private static readonly NetworkSampler Network = new();
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly object StatusGate = new();
    private static StatusPayload? latestStatus;

    public static void Main(string[] args)
    {
        var launchInBackground = args.Any(x => string.Equals(x, "--startup", StringComparison.OrdinalIgnoreCase));
        Hardware.Open();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Shutdown.Cancel();
            Hardware.Close();
        };

        _ = Task.Run(DiscoveryLoop, Shutdown.Token);
        _ = Task.Run(StatusLoop, Shutdown.Token);
        _ = Task.Run(HttpLoop, Shutdown.Token);

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MonitorForm(launchInBackground));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Mira 启动失败：{ex.Message}", "Mira System Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Shutdown.Cancel();
            Hardware.Close();
        }
    }

    private static async Task StatusLoop()
    {
        while (!Shutdown.IsCancellationRequested)
        {
            try
            {
                var value = ReadStatus();
                lock (StatusGate) latestStatus = value;
            }
            catch { }
            try { await Task.Delay(1000, Shutdown.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static StatusPayload GetLatestStatus()
    {
        lock (StatusGate) return latestStatus ?? ReadStatus();
    }

    private static async Task HttpLoop()
    {
        var listener = new TcpListener(IPAddress.Any, HttpPort);
        try
        {
            listener.Start();
            while (!Shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(Shutdown.Token);
                _ = Task.Run(() => Handle(client), Shutdown.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally { listener.Stop(); }
    }

    private static async Task DiscoveryLoop()
    {
        using var udp = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
        while (true)
        {
            try
            {
                var received = await udp.ReceiveAsync();
                if (Encoding.UTF8.GetString(received.Buffer).Trim() != DiscoveryMagic) continue;
                var advertisedIp = Network.GetSelectedLocalIp();
                var response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    service = "mira-system-monitor",
                    port = HttpPort,
                    ip = advertisedIp
                }));
                await udp.SendAsync(response, received.RemoteEndPoint);
            }
            catch (SocketException) { await Task.Delay(100); }
            catch (ObjectDisposedException) { return; }
        }
    }

    private static async Task Handle(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true))
        {
            try
            {
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = parts.Length > 1 ? parts[1].Split('?', 2)[0] : "/";
                var body = path is "/" or "/api/v1/status"
                    ? JsonSerializer.Serialize(GetLatestStatus())
                    : "{\"ok\":false}";
                var status = path is "/" or "/api/v1/status" ? "200 OK" : "404 Not Found";
                var bytes = Encoding.UTF8.GetBytes(body);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n" +
                    $"Content-Length: {bytes.Length}\r\n\r\n");
                await stream.WriteAsync(headers);
                await stream.WriteAsync(bytes);
            }
            catch { }
        }
    }

    private static StatusPayload ReadStatus()
    {
        var cpu = Cpu.Read();

        var memory = ReadMemory();
        var network = Network.Read();
        var sensors = Hardware.Read();
        return new StatusPayload(
            true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Environment.MachineName,
            "Windows",
            cpu,
            memory,
            network,
            sensors.TemperatureC,
            sensors.FanRpm,
            sensors.FanPercent,
            sensors.ChassisRpm,
            sensors.PsuRpm,
            sensors.GpuRpm,
            sensors.GpuPercent,
            sensors.FanSensors);
    }

    private static MemoryPayload ReadMemory()
    {
        var info = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(info)) return new MemoryPayload(0, 0, 0);
        var total = (long)info.TotalPhysical;
        var used = Math.Max(0L, total - (long)info.AvailablePhysical);
        var percent = total > 0 ? Math.Round(used * 100d / total, 1) : 0;
        return new MemoryPayload(percent, used, total);
    }

    private sealed record StatusPayload(
        bool ok,
        long timestamp,
        string host,
        string platform,
        double cpuPercent,
        MemoryPayload memory,
        NetworkPayload network,
        double? temperatureC,
        double? fanRpm,
        double? fanPercent,
        double? chassisRpm,
        double? psuRpm,
        double? gpuRpm,
        double? gpuPercent,
        IReadOnlyList<FanSensorPayload> fanSensors);

    private sealed record FanSensorPayload(string hardware, string name, double rpm, double? percent);

    private sealed record MemoryPayload(double percent, long usedBytes, long totalBytes);

    private sealed record NetworkPayload(
        long downloadBytesPerSecond,
        long uploadBytesPerSecond,
        string type,
        string name,
        long speedMbps,
        string localIp);

    private sealed class NetworkSampler
    {
        private readonly object gate = new();
        private NetworkMode mode = LoadMode();
        private long lastRx;
        private long lastTx;
        private long lastAt = Stopwatch.GetTimestamp();
        private string lastInterface = "";

        public string ModeName
        {
            get { lock (gate) return mode.ToString(); }
        }

        public void SetMode(NetworkMode value)
        {
            lock (gate)
            {
                if (mode == value) return;
                mode = value;
                lastRx = 0;
                lastTx = 0;
                lastInterface = "";
                SaveMode(value);
            }
        }

        public string GetSelectedLocalIp()
        {
            var selected = SelectInterface();
            return GetIpv4(selected) ?? "";
        }

        public NetworkPayload Read()
        {
            var selected = SelectInterface();
            if (selected is null) return new NetworkPayload(0, 0, "UNKNOWN", "", 0, "");

            long rx = 0, tx = 0;
            try
            {
                var stats = selected.GetIPStatistics();
                rx = stats.BytesReceived;
                tx = stats.BytesSent;
            }
            catch { }

            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                var seconds = Math.Max(0.2, (now - lastAt) / (double)Stopwatch.Frequency);
                var interfaceChanged = !string.Equals(lastInterface, selected.Id, StringComparison.Ordinal);
                var down = lastRx == 0 || interfaceChanged ? 0 : Math.Max(0, (long)((rx - lastRx) / seconds));
                var up = lastTx == 0 || interfaceChanged ? 0 : Math.Max(0, (long)((tx - lastTx) / seconds));
                lastRx = rx;
                lastTx = tx;
                lastAt = now;
                lastInterface = selected.Id;
                var wireless = selected.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                var localIp = GetIpv4(selected) ?? "";
                return new NetworkPayload(down, up, wireless ? "WI-FI" : "ETHERNET", selected.Name, Math.Max(0, selected.Speed / 1_000_000), localIp);
            }
        }

        private NetworkInterface? SelectInterface()
        {
            NetworkMode selectedMode;
            lock (gate) selectedMode = mode;
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsable)
                .Where(item => selectedMode switch
                {
                    NetworkMode.Wired => item.NetworkInterfaceType == NetworkInterfaceType.Ethernet,
                    NetworkMode.WiFi => item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    _ => true
                })
                .OrderByDescending(Score)
                .FirstOrDefault();
        }

        private static string? GetIpv4(NetworkInterface? item) => item?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();

        private static NetworkMode LoadMode()
        {
            try
            {
                if (LoadSettings() is { } settings &&
                    Enum.TryParse<NetworkMode>(settings.networkMode, true, out var parsed)) return parsed;
            }
            catch { }
            return NetworkMode.Auto;
        }

        private static void SaveMode(NetworkMode value)
        {
            try
            {
                SaveSettings(LoadSettings() with { networkMode = value.ToString() });
            }
            catch { }
        }

        internal enum NetworkMode { Auto, Wired, WiFi }

        private static bool IsUsable(NetworkInterface item)
        {
            if (item.OperationalStatus != OperationalStatus.Up || item.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
            var identity = $"{item.Name} {item.Description}";
            if (identity.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("hyper-v", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("default switch", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("vmware", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("virtualbox", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("zerotier", StringComparison.OrdinalIgnoreCase)) return false;
            return item.GetIPProperties().UnicastAddresses.Any(x => x.Address.AddressFamily == AddressFamily.InterNetwork);
        }

        private static int Score(NetworkInterface item)
        {
            var hasGateway = item.GetIPProperties().GatewayAddresses.Any(x => x.Address.AddressFamily == AddressFamily.InterNetwork);
            var physical = item.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
            return (hasGateway ? 1000 : 0) + (physical ? 100 : 0) + (item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 20 : 40);
        }
    }

    private sealed class CpuSampler
    {
        private readonly object gate = new();
        private ulong lastIdle;
        private ulong lastKernel;
        private ulong lastUser;

        public double Read()
        {
            lock (gate)
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
                var idleValue = ToUInt64(idle);
                var kernelValue = ToUInt64(kernel);
                var userValue = ToUInt64(user);
                if (lastKernel == 0 && lastUser == 0)
                {
                    lastIdle = idleValue;
                    lastKernel = kernelValue;
                    lastUser = userValue;
                    return 0;
                }

                var idleDelta = idleValue - lastIdle;
                var totalDelta = kernelValue - lastKernel + userValue - lastUser;
                lastIdle = idleValue;
                lastKernel = kernelValue;
                lastUser = userValue;
                if (totalDelta == 0) return 0;
                return Math.Round(Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100), 1);
            }
        }

        private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;
    }

    private sealed class HardwareReader
    {
        private readonly object gate = new();
        private readonly Computer computer = new()
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };

        public void Open()
        {
            try
            {
                computer.Open();
                var pawnIoVersion = LibreHardwareMonitor.PawnIo.PawnIo.Version.ToString();
                Console.WriteLine($"PawnIO: installed={LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled}, version={pawnIoVersion}");
            }
            catch (Exception ex) { Console.WriteLine($"硬件监控初始化失败: {ex.GetType().Name}: {ex.Message}"); }
        }

        public void Close()
        {
            try { computer.Close(); } catch { }
        }

        public SensorValues Read()
        {
            lock (gate)
            {
                try
                {
                    var sensors = new SensorCollector();
                    foreach (var hardware in computer.Hardware) Visit(hardware, sensors);
                    return sensors.Build();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"硬件监控读取失败: {ex.GetType().Name}: {ex.Message}");
                    return new SensorValues(null, null, null);
                }
            }
        }

        private static void Visit(IHardware hardware, SensorCollector collector)
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                if (!sensor.Value.HasValue) continue;
                collector.Add(hardware, sensor);
            }
            foreach (var child in hardware.SubHardware) Visit(child, collector);
        }

        private static bool IsUsefulTemperature(float value) => value > -40 && value < 130;

        private sealed class SensorCollector
        {
            private readonly List<float> cpuTemperatures = new();
            private readonly List<float> gpuCoreTemperatures = new();
            private readonly List<float> otherTemperatures = new();
            private readonly List<float> fanRpms = new();
            private readonly List<float> fanPercents = new();
            private readonly List<FanReading> fanReadings = new();
            private readonly List<FanReading> gpuFanReadings = new();
            private readonly List<FanReading> motherboardFanReadings = new();
            private readonly List<FanReading> psuFanReadings = new();

            public void Add(IHardware hardware, ISensor sensor)
            {
                var value = sensor.Value!.Value;
                if (sensor.SensorType == SensorType.Temperature && IsUsefulTemperature(value))
                {
                    if (hardware.HardwareType == HardwareType.Cpu) cpuTemperatures.Add(value);
                    else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel &&
                             sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) gpuCoreTemperatures.Add(value);
                    else otherTemperatures.Add(value);
                    return;
                }

                if (sensor.SensorType == SensorType.Fan && value >= 0 && value <= 100000)
                {
                    fanRpms.Add(value);
                    var reading = new FanReading(hardware.HardwareType.ToString(), sensor.Name, value);
                    fanReadings.Add(reading);
                    if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                        gpuFanReadings.Add(reading);
                    else if (hardware.HardwareType == HardwareType.Psu || sensor.Name.Contains("PSU", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Power", StringComparison.OrdinalIgnoreCase))
                        psuFanReadings.Add(reading);
                    else
                        motherboardFanReadings.Add(reading);
                    return;
                }

                if (sensor.SensorType == SensorType.Control &&
                    sensor.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase) &&
                    value >= 0 && value <= 100)
                {
                    fanPercents.Add(value);
                }
            }

            public SensorValues Build()
            {
                var temperature = cpuTemperatures.Count > 0 ? cpuTemperatures.Max()
                    : gpuCoreTemperatures.Count > 0 ? gpuCoreTemperatures.Max()
                    : otherTemperatures.Count > 0 ? otherTemperatures.Max()
                    : (float?)null;
                // A zero-RPM GPU fan is normally Zero RPM mode. Prefer its control
                // percentage so the client can distinguish "stopped" from "missing".
                var runningRpms = fanRpms.Where(value => value > 0).ToList();
                var rpm = runningRpms.Count > 0 ? runningRpms.Max() : (float?)null;
                var percent = fanPercents.Count > 0 ? fanPercents.Max() : (float?)null;
                var gpuRpm = PickRpm(gpuFanReadings);
                // Some Super I/O chips expose the PSU tachometer as an unnamed
                // motherboard fan channel (for this machine it is Fan #3), so
                // use the second motherboard channel when no explicit PSU sensor
                // exists. Keep the value missing when that channel is absent.
                var psuRpm = PickPsuRpm(psuFanReadings, motherboardFanReadings);
                var chassisRpm = PickChassisRpm(motherboardFanReadings);
                var gpuPercent = gpuFanReadings.Count > 0 ? percent : null;
                var payload = fanReadings
                    .Select(x => new FanSensorPayload(x.Hardware, x.Name, Math.Round(x.Rpm, 1), null))
                    .ToArray();
                return new SensorValues(temperature, rpm, percent, chassisRpm, psuRpm, gpuRpm, gpuPercent, payload);
            }

            private static float? PickRpm(IReadOnlyList<FanReading> readings) => readings.Count == 0 ? null : readings.Max(x => x.Rpm);

            private static float? PickPsuRpm(
                IReadOnlyList<FanReading> explicitPsuReadings,
                IReadOnlyList<FanReading> motherboardReadings)
            {
                if (explicitPsuReadings.Count > 0) return PickRpm(explicitPsuReadings);
                return motherboardReadings.Count > 1 ? motherboardReadings[1].Rpm : null;
            }

            private static float? PickChassisRpm(IReadOnlyList<FanReading> readings)
            {
                if (readings.Count == 0) return null;
                var named = readings.FirstOrDefault(x =>
                    x.Name.Contains("Chassis", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("Case", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("System", StringComparison.OrdinalIgnoreCase));
                return (named ?? readings[0]).Rpm;
            }

            private sealed record FanReading(string Hardware, string Name, float Rpm);
        }
    }

    private sealed record SensorValues(
        double? TemperatureC,
        double? FanRpm,
        double? FanPercent,
        double? ChassisRpm,
        double? PsuRpm,
        double? GpuRpm,
        double? GpuPercent,
        IReadOnlyList<FanSensorPayload> FanSensors)
    {
        public SensorValues(double? temperatureC, double? fanRpm, double? fanPercent)
            : this(temperatureC, fanRpm, fanPercent, null, null, null, null, Array.Empty<FanSensorPayload>()) { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    private sealed class MonitorForm : Form
    {
        private readonly Label connectionLabel = new();
        private readonly Label endpointLabel = new();
        private readonly ComboBox networkModeBox = new();
        private readonly CheckBox startupBox = new();
        private readonly CheckBox backgroundBox = new();
        private readonly Dictionary<string, Label> values = new();
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
        private readonly NotifyIcon trayIcon = new();
        private bool backgroundKeepAlive;
        private bool closingForExit;
        private bool updatingStartupBox;

        public MonitorForm(bool launchInBackground)
        {
            var settings = LoadSettings();
            backgroundKeepAlive = settings.backgroundKeepAlive;
            Text = "Mira System Monitor";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 500);
            MinimumSize = new Size(720, 500);
            BackColor = Color.FromArgb(13, 18, 23);
            ForeColor = Color.FromArgb(235, 242, 248);
            Font = new Font("Segoe UI", 10f);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(28),
                ColumnCount = 2,
                RowCount = 5,
                BackColor = BackColor
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var title = new Label
            {
                Text = "MIRA SYSTEM MONITOR",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 18f),
                ForeColor = Color.FromArgb(75, 217, 255)
            };
            root.Controls.Add(title, 0, 0);
            root.SetColumnSpan(title, 2);

            connectionLabel.Dock = DockStyle.Fill;
            connectionLabel.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(connectionLabel, 0, 1);
            root.SetColumnSpan(connectionLabel, 2);

            var endpointRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = Padding.Empty
            };
            endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
            endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            endpointLabel.Dock = DockStyle.Fill;
            endpointLabel.TextAlign = ContentAlignment.MiddleLeft;
            endpointLabel.ForeColor = Color.FromArgb(145, 165, 182);
            endpointRow.Controls.Add(endpointLabel, 0, 0);
            networkModeBox.Dock = DockStyle.Fill;
            networkModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            networkModeBox.FlatStyle = FlatStyle.Flat;
            networkModeBox.BackColor = Color.FromArgb(20, 27, 34);
            networkModeBox.ForeColor = Color.FromArgb(235, 242, 248);
            networkModeBox.Items.AddRange(new object[] { "自动选择", "有线连接", "Wi-Fi 连接" });
            networkModeBox.SelectedIndex = ModeToIndex(Network.ModeName);
            networkModeBox.SelectedIndexChanged += (_, _) =>
            {
                Network.SetMode(IndexToMode(networkModeBox.SelectedIndex));
                RefreshValues();
            };
            endpointRow.Controls.Add(networkModeBox, 1, 0);
            root.Controls.Add(endpointRow, 0, 2);
            root.SetColumnSpan(endpointRow, 2);

            var optionsRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = Padding.Empty
            };
            optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            ConfigureOption(startupBox, "开机启动", settings.autoStart && AutoStartTaskExists());
            ConfigureOption(backgroundBox, "后台保活", backgroundKeepAlive);
            startupBox.CheckedChanged += OnStartupChanged;
            backgroundBox.CheckedChanged += (_, _) =>
            {
                backgroundKeepAlive = backgroundBox.Checked;
                trayIcon.Visible = backgroundKeepAlive;
                SaveSettings(LoadSettings() with { backgroundKeepAlive = this.backgroundKeepAlive });
            };
            optionsRow.Controls.Add(startupBox, 0, 0);
            optionsRow.Controls.Add(backgroundBox, 1, 0);
            root.Controls.Add(optionsRow, 0, 3);
            root.SetColumnSpan(optionsRow, 2);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 3,
                BackColor = Color.FromArgb(20, 27, 34),
                Padding = new Padding(18, 14, 18, 14)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            for (var i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333f));
            root.Controls.Add(grid, 0, 4);
            root.SetColumnSpan(grid, 2);

            AddMetric(grid, "CPU", 0, 0, Color.FromArgb(101, 240, 112));
            AddMetric(grid, "内存", 1, 0, Color.FromArgb(255, 170, 57));
            AddMetric(grid, "下载", 2, 0, Color.FromArgb(75, 217, 255));
            AddMetric(grid, "上传", 3, 0, Color.FromArgb(255, 92, 100));
            AddMetric(grid, "网络", 0, 1, Color.FromArgb(173, 202, 224));
            AddMetric(grid, "温度", 1, 1, Color.FromArgb(75, 217, 255));
            AddMetric(grid, "机箱风扇", 2, 1, Color.FromArgb(255, 170, 57));
            AddMetric(grid, "电源风扇", 3, 1, Color.FromArgb(255, 170, 57));
            AddMetric(grid, "GPU 风扇", 0, 2, Color.FromArgb(255, 170, 57));
            AddMetric(grid, "主机", 1, 2, Color.FromArgb(173, 202, 224));
            AddMetric(grid, "接口", 2, 2, Color.FromArgb(173, 202, 224));

            timer.Tick += (_, _) => RefreshValues();
            timer.Start();
            SetupTray();
            FormClosing += HandleFormClosing;
            FormClosed += (_, _) =>
            {
                timer.Stop();
                Shutdown.Cancel();
                trayIcon.Visible = false;
                trayIcon.Dispose();
            };
            Shown += (_, _) =>
            {
                if (launchInBackground && backgroundKeepAlive)
                {
                    BeginInvoke(new Action(Hide));
                }
            };
            RefreshValues();
        }

        private void OnStartupChanged(object? sender, EventArgs e)
        {
            if (updatingStartupBox) return;
            var enabled = startupBox.Checked;
            if (!ConfigureAutoStart(enabled))
            {
                updatingStartupBox = true;
                startupBox.Checked = !enabled;
                updatingStartupBox = false;
                return;
            }
            SaveSettings(LoadSettings() with { autoStart = enabled });
        }

        private void ConfigureOption(CheckBox box, string text, bool isChecked)
        {
            box.Text = text;
            box.Checked = isChecked;
            box.AutoSize = true;
            box.Dock = DockStyle.Left;
            box.ForeColor = Color.FromArgb(173, 202, 224);
            box.Margin = new Padding(0, 6, 18, 2);
        }

        private void SetupTray()
        {
            trayIcon.Text = "Mira System Monitor";
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Visible = backgroundKeepAlive;
            trayIcon.DoubleClick += (_, _) => ShowWindow();
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开监控窗口", null, (_, _) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 Mira", null, (_, _) =>
            {
                closingForExit = true;
                Close();
            });
            trayIcon.ContextMenuStrip = menu;
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void HandleFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (backgroundKeepAlive && !closingForExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                trayIcon.Visible = true;
            }
        }

        private void AddMetric(TableLayoutPanel grid, string name, int column, int row, Color color)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(6, 3, 6, 3),
                BackColor = Color.Transparent
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var caption = new Label
            {
                Text = name,
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.FromArgb(145, 165, 182),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var value = new Label
            {
                Text = "--",
                Dock = DockStyle.Fill,
                ForeColor = color,
                AutoSize = false,
                Font = new Font("Segoe UI Semibold", 13f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            panel.Controls.Add(caption, 0, 0);
            panel.Controls.Add(value, 0, 1);
            grid.Controls.Add(panel, column, row);
            values[name] = value;
        }

        private void RefreshValues()
        {
            var status = GetLatestStatus();
            connectionLabel.Text = status.ok ? "● 已运行  ·  HTTP 服务正常" : "○ 服务异常";
            connectionLabel.ForeColor = status.ok ? Color.FromArgb(101, 240, 112) : Color.FromArgb(255, 92, 100);
            var ip = string.IsNullOrWhiteSpace(status.network.localIp) ? "电脑IP" : status.network.localIp;
            endpointLabel.Text = $"http://{ip}:{HttpPort}/api/v1/status    ·    UDP 自动发现 {DiscoveryPort}";
            Set("CPU", $"{status.cpuPercent:0.0}%");
            Set("内存", $"{status.memory.percent:0.0}%");
            Set("下载", FormatRate(status.network.downloadBytesPerSecond));
            Set("上传", FormatRate(status.network.uploadBytesPerSecond));
            Set("网络", status.network.type);
            Set("温度", status.temperatureC is null ? "--" : $"{status.temperatureC:0} °C");
            Set("机箱风扇", FormatRpm(status.chassisRpm));
            Set("电源风扇", FormatRpm(status.psuRpm));
            Set("GPU 风扇", FormatRpm(status.gpuRpm));
            Set("主机", status.host);
            Set("接口", status.network.name);
        }

        private static int ModeToIndex(string mode) => mode switch
        {
            "Wired" => 1,
            "WiFi" => 2,
            _ => 0
        };

        private static NetworkSampler.NetworkMode IndexToMode(int index) => index switch
        {
            1 => NetworkSampler.NetworkMode.Wired,
            2 => NetworkSampler.NetworkMode.WiFi,
            _ => NetworkSampler.NetworkMode.Auto
        };

        private void Set(string name, string value)
        {
            if (values.TryGetValue(name, out var label)) label.Text = value;
        }

        private static string FormatRate(long bytesPerSecond)
        {
            var bytes = Math.Max(0, bytesPerSecond);
            if (bytes < 1_000_000)
            {
                var kb = bytes / 1_000d;
                return kb < 1 ? "0.0 KB/s" : $"{kb:0.0} KB/s";
            }

            return $"{bytes / 1_000_000d:0.0} MB/s";
        }

        private static string FormatRpm(double? rpm) => rpm is null ? "--" : $"{rpm:0} RPM";
    }

    private sealed record AgentSettings(
        string networkMode = "Auto",
        bool autoStart = false,
        bool backgroundKeepAlive = true);

    private static AgentSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(SettingsFile)) is { } settings)
                return settings;
        }
        catch { }
        return new AgentSettings();
    }

    private static void SaveSettings(AgentSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings));
        }
        catch { }
    }

    private static bool ConfigureAutoStart(bool enabled)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(enabled ? "/Create" : "/Delete");
        process.StartInfo.ArgumentList.Add("/TN");
        process.StartInfo.ArgumentList.Add(StartupTaskName);
        if (enabled)
        {
            process.StartInfo.ArgumentList.Add("/F");
            process.StartInfo.ArgumentList.Add("/SC");
            process.StartInfo.ArgumentList.Add("ONLOGON");
            process.StartInfo.ArgumentList.Add("/RL");
            process.StartInfo.ArgumentList.Add("HIGHEST");
            process.StartInfo.ArgumentList.Add("/TR");
            process.StartInfo.ArgumentList.Add($"\"{executable}\" --startup");
        }
        else
        {
            process.StartInfo.ArgumentList.Add("/F");
        }

        try
        {
            process.Start();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool AutoStartTaskExists()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "/Query", "/TN", StartupTaskName }
            });
            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }
}
