using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Forms;
using System.Windows.Media;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Threading;
using Color = System.Windows.Media.Color;

namespace WireGuardTunnel
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private bool _isActive = false;
        private bool _isConnecting = false;
        private bool _internetConnected = false;
        private Process? _vpnProc;
        private NotifyIcon? _trayIcon;
        private System.Windows.Forms.Timer? _connectionTimer;
        private System.Windows.Forms.Timer? _ipCheckTimer;
        private string _currentConfigFile = "wg-config.conf";
        private DateTime _connectionStartTime;
        private int _failedChecks = 0;

        private readonly HttpClient _httpClient;
        private const string IP_CHECK_SERVICE = "https://api.ipify.org";

        public ObservableCollection<AppProcess> Apps { get; set; } = new ObservableCollection<AppProcess>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsConnecting
        {
            get { return _isConnecting; }
            set
            {
                _isConnecting = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsConnecting"));
                UpdateStatusDisplay();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "WireGuardTunnel/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(5);

            RefreshProcessList();
            AppListBox.ItemsSource = Apps;
            SetupTray();
            SetupTimers();
            UpdateStatusDisplay();

            CheckExistingConfig();
        }

        public class AppProcess : INotifyPropertyChanged
        {
            private bool _isSelected;
            public string Name { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string ProcessPath { get; set; } = string.Empty;

            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsSelected"));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public class IPResponse
        {
            public string? ip { get; set; }
        }

        private string GetFileDescription(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return string.Empty;

                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(filePath);

                if (!string.IsNullOrEmpty(versionInfo.FileDescription))
                    return versionInfo.FileDescription;

                if (!string.IsNullOrEmpty(versionInfo.ProductName))
                    return versionInfo.ProductName;

                return Path.GetFileNameWithoutExtension(filePath);
            }
            catch
            {
                return string.Empty;
            }
        }

        private HashSet<uint> GetProcessesWithWindows()
        {
            var processIds = new HashSet<uint>();

            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    int length = GetWindowTextLength(hWnd);
                    if (length > 0)
                    {
                        GetWindowThreadProcessId(hWnd, out uint processId);
                        processIds.Add(processId);
                    }
                }
                return true;
            }, IntPtr.Zero);

            return processIds;
        }

        private string GetProcessPath(uint processId)
        {
            try
            {
                Process proc = Process.GetProcessById((int)processId);
                return proc.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RefreshProcessList()
        {
            Apps.Clear();

            var systemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "svchost", "System", "Idle", "Registry", "smss", "csrss", "wininit",
                "services", "lsass", "winlogon", "spoolsv", "conhost", "dwm", "taskhost",
                "explorer", "RuntimeBroker", "SearchUI", "ShellExperienceHost", "sihost",
                "SecurityHealthService", "WmiPrvSE", "ctfmon", "StartMenuExperienceHost",
                "TextInputHost", "LockApp", "SystemSettings", "ApplicationFrameHost",
                "fontdrvhost", "dllhost", "rundll32", "schedtasks", "userinit",
                "SearchApp", "Widgets", "PhoneExperienceHost", "YourPhone", "HxTsr",
                "backgroundTaskHost", "GameBar", "TextInputHost", "Video.UI",
                "WireGuardTunnel"
            };

            try
            {
                var processesWithWindows = GetProcessesWithWindows();
                var uniqueProcesses = new Dictionary<string, AppProcess>();

                foreach (uint processId in processesWithWindows)
                {
                    try
                    {
                        Process proc = Process.GetProcessById((int)processId);
                        string processName = proc.ProcessName;

                        if (systemProcesses.Contains(processName))
                            continue;

                        string processPath = GetProcessPath(processId);
                        string fileDescription = GetFileDescription(processPath);

                        string displayName;
                        if (!string.IsNullOrEmpty(fileDescription) &&
                            !fileDescription.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = fileDescription;
                        }
                        else
                        {
                            displayName = processName;
                        }

                        string key = !string.IsNullOrEmpty(processPath) ? processPath : processName;

                        if (!uniqueProcesses.ContainsKey(key))
                        {
                            uniqueProcesses[key] = new AppProcess
                            {
                                Name = processName + ".exe",
                                DisplayName = displayName,
                                ProcessPath = processPath,
                                IsSelected = false
                            };
                        }
                    }
                    catch
                    {
                    }
                }

                foreach (var proc in uniqueProcesses.Values.OrderBy(p => p.DisplayName))
                {
                    Apps.Add(proc);
                }

                ConfigStatusText.Text = $"📊 Найдено приложений: {Apps.Count}";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка при обновлении списка процессов: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetupTimers()
        {
            _connectionTimer = new System.Windows.Forms.Timer();
            _connectionTimer.Interval = 5000;
            _connectionTimer.Tick += ConnectionTimer_Tick!;

            _ipCheckTimer = new System.Windows.Forms.Timer();
            _ipCheckTimer.Interval = 1000;
            _ipCheckTimer.Tick += IpCheckTimer_Tick!;
        }

        private async void ConnectionTimer_Tick(object? sender, EventArgs e)
        {
            if (_isActive && _vpnProc != null)
            {
                if (_vpnProc.HasExited)
                {
                    await Dispatcher.Invoke(async () =>
                    {
                        await StopVpnAsync();
                        System.Windows.MessageBox.Show("Соединение было прервано!", "WireGuard Tunnel",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
            }
        }

        private async void IpCheckTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isActive) return;

            bool hasInternet = await CheckInternetAsync();

            Dispatcher.Invoke(() =>
            {
                if (hasInternet != _internetConnected)
                {
                    _internetConnected = hasInternet;

                    if (_internetConnected)
                    {
                        if (_isConnecting)
                        {
                            IsConnecting = false;
                            _failedChecks = 0;
                            ConfigStatusText.Text = $"✅ Подключено";
                            ConfigStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 196, 140));
                        }
                    }
                    else
                    {
                        _failedChecks++;

                        if (!_isConnecting && _isActive && _failedChecks > 3)
                        {
                            IsConnecting = true;
                            ConfigStatusText.Text = $"⚠️ Потеряно соединение";
                            ConfigStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                        }
                    }

                    UpdateStatusDisplay();
                }
            });
        }

        private async Task<bool> CheckInternetAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync(IP_CHECK_SERVICE);

                    if (response.IsSuccessStatusCode)
                    {
                        var ip = await response.Content.ReadAsStringAsync();
                        ip = ip.Trim();

                        if (!string.IsNullOrWhiteSpace(ip) && !IsLocalIP(ip))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool IsLocalIP(string ip)
        {
            return ip.StartsWith("192.168.") ||
                   ip.StartsWith("10.") ||
                   ip.StartsWith("172.16.") ||
                   ip.StartsWith("172.17.") ||
                   ip.StartsWith("172.18.") ||
                   ip.StartsWith("172.19.") ||
                   ip.StartsWith("172.20.") ||
                   ip.StartsWith("172.21.") ||
                   ip.StartsWith("172.22.") ||
                   ip.StartsWith("172.23.") ||
                   ip.StartsWith("172.24.") ||
                   ip.StartsWith("172.25.") ||
                   ip.StartsWith("172.26.") ||
                   ip.StartsWith("172.27.") ||
                   ip.StartsWith("172.28.") ||
                   ip.StartsWith("172.29.") ||
                   ip.StartsWith("172.30.") ||
                   ip.StartsWith("172.31.") ||
                   ip.StartsWith("127.") ||
                   ip == "::1" ||
                   ip == "localhost";
        }

        private void UpdateStatusDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                if (_isActive)
                {
                    if (_isConnecting)
                    {
                        TimeSpan elapsed = DateTime.Now - _connectionStartTime;

                        StatusIcon.Text = "🟡";
                        StatusText.Text = "Connecting...";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));

                        if (elapsed.TotalSeconds < 30)
                            StatusDetail.Text = $"Establishing tunnel ({elapsed.Seconds}s)";
                        else
                            StatusDetail.Text = "Tunnel is taking longer than usual...";
                    }
                    else if (_internetConnected)
                    {
                        StatusIcon.Text = "🟢";
                        StatusText.Text = "Connected";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 196, 140));
                        StatusDetail.Text = "Internet active";
                    }
                    else
                    {
                        StatusIcon.Text = "🟡";
                        StatusText.Text = "Tunnel Up";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                        StatusDetail.Text = "Waiting for internet...";
                    }
                }
                else
                {
                    StatusIcon.Text = "🔴";
                    StatusText.Text = "Disconnected";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                    StatusDetail.Text = "Ready to connect";
                }
            });
        }

        private void CheckExistingConfig()
        {
            if (File.Exists(_currentConfigFile))
            {
                ConfigStatusText.Text = "⚠️ Конфиг существует, готов к подключению";
                ConfigStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
            }
            else
            {
                ConfigStatusText.Text = "⚡ Вставьте WireGuard конфиг";
                ConfigStatusText.Foreground = new SolidColorBrush(Color.FromRgb(74, 111, 165));
            }
        }

        private void RefreshProcessList_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcessList();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            var allSelected = Apps.All(a => a.IsSelected);
            foreach (var app in Apps)
                app.IsSelected = !allSelected;
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            string configText = ConfigTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(configText))
            {
                System.Windows.MessageBox.Show("Введите WireGuard конфигурацию!",
                    "WireGuard Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Проверяем, что это похоже на WireGuard конфиг
                if (!configText.Contains("[Interface]") || !configText.Contains("[Peer]"))
                {
                    System.Windows.MessageBox.Show("Неверный формат конфигурации! Должны быть секции [Interface] и [Peer].",
                        "WireGuard Tunnel", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                File.WriteAllText(_currentConfigFile, configText);
                ConfigStatusText.Text = "✅ Конфиг сохранен";
                ConfigStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 196, 140));
                ConfigTextBox.Text = string.Empty;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка при сохранении конфига: {ex.Message}",
                    "WireGuard Tunnel", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (!_isActive)
                await StartVpnAsync();
            else
                await StopVpnAsync();
        }

        private async Task StartVpnAsync()
        {
            var selected = Apps.Where(a => a.IsSelected).Select(a => a.Name).ToList();
            if (!selected.Any())
            {
                System.Windows.MessageBox.Show("Выберите приложения для туннелирования!",
                    "WireGuard Tunnel", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!File.Exists(_currentConfigFile))
            {
                System.Windows.MessageBox.Show("Сначала сохраните WireGuard конфигурацию!",
                    "WireGuard Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnAction.IsEnabled = false;
            BtnAction.Content = "ЗАПУСК...";

            try
            {
                string configText = await File.ReadAllTextAsync(_currentConfigFile);
                var wgData = ParseWireGuardConfig(configText);
                if (wgData == null)
                {
                    System.Windows.MessageBox.Show("Неверный формат WireGuard конфигурации!",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    BtnAction.IsEnabled = true;
                    BtnAction.Content = "START TUNNEL";
                    return;
                }

                string jsonConfig = GenerateSingBoxJson(wgData, selected);
                await File.WriteAllTextAsync("config.json", jsonConfig);

                try
                {
                    _isActive = true;
                    _isConnecting = true;
                    _internetConnected = false;
                    _failedChecks = 0;
                    _connectionStartTime = DateTime.Now;

                    _connectionTimer?.Start();
                    _ipCheckTimer?.Start();

                    _vpnProc = new Process();
                    _vpnProc.StartInfo = new ProcessStartInfo
                    {
                        FileName = "sing-box.exe",
                        Arguments = "run -c config.json",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    _vpnProc.Start();

                    BtnAction.Content = "STOP TUNNEL";
                    BtnAction.Background = new SolidColorBrush(Color.FromRgb(237, 43, 20));
                    AppListBox.IsEnabled = false;
                    ConfigTextBox.IsEnabled = false;
                    SaveConfigBtn.IsEnabled = false;

                    ConfigStatusText.Text = $"🔒 Туннель запускается";
                    ConfigStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                }
                catch (Exception ex)
                {
                    _isActive = false;
                    _isConnecting = false;
                    _connectionTimer?.Stop();
                    _ipCheckTimer?.Stop();

                    System.Windows.MessageBox.Show($"Ошибка запуска: {ex.Message}",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка: {ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnAction.IsEnabled = true;
            }
        }

        private async Task StopVpnAsync()
        {
            BtnAction.IsEnabled = false;
            BtnAction.Content = "ОСТАНОВКА...";

            try
            {
                if (_vpnProc != null && !_vpnProc.HasExited)
                {
                    try
                    {
                        _vpnProc.Kill();
                        _vpnProc.WaitForExit(3000);
                    }
                    catch { }
                    _vpnProc = null;
                }

                _isActive = false;
                _isConnecting = false;
                _internetConnected = false;
                _connectionTimer?.Stop();
                _ipCheckTimer?.Stop();

                try
                {
                    if (File.Exists("config.json"))
                        File.Delete("config.json");
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка при остановке: {ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                BtnAction.Content = "START TUNNEL";
                BtnAction.Background = new SolidColorBrush(Color.FromRgb(0, 196, 140));
                AppListBox.IsEnabled = true;
                ConfigTextBox.IsEnabled = true;
                SaveConfigBtn.IsEnabled = true;
                BtnAction.IsEnabled = true;

                UpdateStatusDisplay();
                CheckExistingConfig();
            }
        }

        private WireGuardData? ParseWireGuardConfig(string config)
        {
            try
            {
                var data = new WireGuardData
                {
                    DnsServers = new List<string>(),
                    AllowedIPs = new List<string>()
                };

                var interfaceMatch = Regex.Match(config, @"\[Interface\](.*?)(?=\[Peer]|$)", RegexOptions.Singleline);
                if (interfaceMatch.Success)
                {
                    var interfaceSection = interfaceMatch.Groups[1].Value;

                    var privateKeyMatch = Regex.Match(interfaceSection, @"PrivateKey\s*=\s*(\S+)");
                    if (privateKeyMatch.Success)
                        data.PrivateKey = privateKeyMatch.Groups[1].Value;

                    var addressMatch = Regex.Match(interfaceSection, @"Address\s*=\s*(\S+)");
                    if (addressMatch.Success)
                        data.Address = addressMatch.Groups[1].Value;

                    var dnsMatch = Regex.Match(interfaceSection, @"DNS\s*=\s*(.+)");
                    if (dnsMatch.Success)
                    {
                        var dnsList = dnsMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var dns in dnsList)
                            data.DnsServers.Add(dns.Trim());
                    }
                }

                var peerMatch = Regex.Match(config, @"\[Peer\](.*?)(?=\[|$)", RegexOptions.Singleline);
                if (peerMatch.Success)
                {
                    var peerSection = peerMatch.Groups[1].Value;

                    var publicKeyMatch = Regex.Match(peerSection, @"PublicKey\s*=\s*(\S+)");
                    if (publicKeyMatch.Success)
                        data.PublicKey = publicKeyMatch.Groups[1].Value;

                    var endpointMatch = Regex.Match(peerSection, @"Endpoint\s*=\s*([^:]+):(\d+)");
                    if (endpointMatch.Success)
                    {
                        data.Server = endpointMatch.Groups[1].Value;
                        data.Port = int.Parse(endpointMatch.Groups[2].Value);
                    }

                    var allowedIPsMatch = Regex.Match(peerSection, @"AllowedIPs\s*=\s*(.+)");
                    if (allowedIPsMatch.Success)
                    {
                        var ips = allowedIPsMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var ip in ips)
                            data.AllowedIPs.Add(ip.Trim());
                    }
                }

                if (string.IsNullOrEmpty(data.PrivateKey) || string.IsNullOrEmpty(data.PublicKey) ||
                    string.IsNullOrEmpty(data.Server) || data.Port == 0)
                    return null;

                // Если DNS не указаны, добавляем стандартные
                if (data.DnsServers.Count == 0)
                {
                    data.DnsServers.Add("1.1.1.1");
                }

                return data;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Parse error: {ex.Message}");
                return null;
            }
        }

        private string GenerateSingBoxJson(WireGuardData wgData, List<string> apps)
        {
            var formattedApps = apps.Select(a => a.ToLower().EndsWith(".exe") ? a : a + ".exe").ToList();
            string appList = string.Join("\",\"", formattedApps);

            string dnsServers = string.Join(",\n      ", wgData.DnsServers.Select(dns =>
                $"{{\n        \"address\": \"{dns}\",\n        \"detour\": \"direct\"\n      }}"));

            string allowedIPs;
            if (wgData.AllowedIPs.Count > 0)
            {
                allowedIPs = string.Join(",\n            ", wgData.AllowedIPs.Select(ip => $"\"{ip}\""));
            }
            else
            {
                allowedIPs = "\"0.0.0.0/0\",\n            \"::/0\"";
            }

            return $@"{{
  ""log"": {{
    ""level"": ""info"",
    ""output"": ""sing-box.log""
  }},
  ""dns"": {{
    ""servers"": [
      {dnsServers}
    ]
  }},
  ""inbounds"": [
    {{
      ""type"": ""tun"",
      ""tag"": ""tun-in"",
      ""interface_name"": ""WireGuard Tunnel"",
      ""inet4_address"": ""172.19.0.1/30"",
      ""auto_route"": true,
      ""strict_route"": false,
      ""stack"": ""system"",
      ""sniff"": true
    }}
  ],
  ""outbounds"": [
    {{
      ""type"": ""wireguard"",
      ""tag"": ""wireguard-out"",
      ""local_address"": [
        ""{wgData.Address}""
      ],
      ""private_key"": ""{wgData.PrivateKey}"",
      ""peers"": [
        {{
          ""server"": ""{wgData.Server}"",
          ""server_port"": {wgData.Port},
          ""public_key"": ""{wgData.PublicKey}"",
          ""allowed_ips"": [
            {allowedIPs}
          ]
        }}
      ]
    }},
    {{
      ""type"": ""direct"",
      ""tag"": ""direct""
    }}
  ],
  ""route"": {{
    ""auto_detect_interface"": true,
    ""rules"": [
      {{
        ""process_name"": [
          ""{appList}""
        ],
        ""outbound"": ""wireguard-out""
      }}
    ],
    ""final"": ""direct""
  }}
}}";
        }

        private void SetupTray()
        {
            try
            {
                _trayIcon = new NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName ?? ""),
                    Visible = true,
                    Text = "WireGuard Tunnel"
                };

                _trayIcon.DoubleClick += (s, e) =>
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                };

                var menu = new ContextMenuStrip();
                menu.Items.Add("Развернуть", null, (s, e) =>
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                });
                menu.Items.Add("-");
                menu.Items.Add("Выход", null, (s, e) => ExitApp());

                _trayIcon.ContextMenuStrip = menu;
            }
            catch { }
        }

        private async void ExitApp()
        {
            try
            {
                if (_isActive)
                {
                    await StopVpnAsync();
                }
            }
            catch { }
            finally
            {
                _trayIcon?.Dispose();
                Environment.Exit(0);
            }
        }

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isActive)
            {
                e.Cancel = true;
                Hide();
                _trayIcon?.ShowBalloonTip(3000, "WireGuard Tunnel",
                    "Приложение свернуто в трей. Нажмите правой кнопкой для выхода.",
                    ToolTipIcon.Info);
            }
            else
            {
                _trayIcon?.Dispose();
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private async void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isActive)
            {
                Hide();
                _trayIcon?.ShowBalloonTip(3000, "WireGuard Tunnel",
                    "Туннель активен. Приложение в трее.",
                    ToolTipIcon.Info);
            }
            else
            {
                Close();
            }
        }
    }

    public class WireGuardData
    {
        public string PrivateKey { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public List<string> DnsServers { get; set; } = new List<string>();
        public string Server { get; set; } = string.Empty;
        public int Port { get; set; }
        public List<string> AllowedIPs { get; set; } = new List<string>();
    }
}