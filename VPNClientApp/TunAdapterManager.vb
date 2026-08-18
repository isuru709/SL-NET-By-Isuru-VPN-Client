Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Text.Json
Imports System.Threading

''' <summary>
''' Manages TUN modes using tun2socks and sing-box.
''' Uses route_exclude_address (sing-box) and split routes (tun2socks) to prevent routing loops.
''' </summary>
Public Class TunAdapterManager
    Private Const ADAPTER_NAME As String = "SLNET"
    Private Const TUN_IP As String = "10.0.0.2"
    Private Const TUN_GATEWAY As String = "10.0.0.1"
    Private Const TUN_MASK As String = "255.255.255.0"
    Private Const TUN_CIDR As String = "10.0.0.2/24"
    Private Const DNS_PRIMARY As String = "1.1.1.1"
    Private Const DNS_SECONDARY As String = "8.8.8.8"

    Private _tunProcess As Process
    Private _isRunning As Boolean = False
    Private _currentMode As String = ""
    Private _originalGateway As String = ""
    Private _originalIfIndex As Integer = -1
    Private _vpnServerIP As String = ""
    Private _exeDir As String
    Private _singBoxConfigPath As String

    Public Event LogMessage(message As String, isError As Boolean)

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _isRunning
        End Get
    End Property

    Public Sub New()
        _exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
        _singBoxConfigPath = Path.Combine(_exeDir, "singbox_tun_config.json")
    End Sub

    Private Sub Log(message As String, Optional isError As Boolean = False)
        RaiseEvent LogMessage(message, isError)
    End Sub

    ''' <summary>
    ''' Kill any lingering tun2socks/sing-box processes and remove stale adapters
    ''' </summary>
    Private Sub AggressiveCleanup()
        ' Kill processes
        KillProcessByName("tun2socks")
        KillProcessByName("sing-box")
        ' Wait for WinTUN driver to release adapter after process exit
        Threading.Thread.Sleep(2000)
        ' Try to remove any stale SLNET adapters
        Try
            RunCmd("netsh", $"interface set interface name=""{ADAPTER_NAME}"" admin=disable")
        Catch
        End Try
        Try
            RunCmd("netsh", $"interface set interface name=""{ADAPTER_NAME} 2"" admin=disable")
        Catch
        End Try
        Try
            RunCmd("netsh", $"interface set interface name=""{ADAPTER_NAME} 3"" admin=disable")
        Catch
        End Try
        Threading.Thread.Sleep(500)
    End Sub

    ''' <summary>
    ''' Get the default gateway and its interface index
    ''' </summary>
    Private Function CaptureOriginalRoute() As Boolean
        Try
            ' Method 1: PowerShell (most reliable)
            Dim gwOutput = RunCmd("powershell", "-NoProfile -Command ""Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1 | ForEach-Object { $_.NextHop + '|' + $_.InterfaceIndex }""")
            Dim parts = gwOutput.Trim().Split("|"c)
            If parts.Length >= 2 Then
                _originalGateway = parts(0).Trim()
                Integer.TryParse(parts(1).Trim(), _originalIfIndex)
            End If

            ' Method 2: Fallback to route print
            If String.IsNullOrEmpty(_originalGateway) OrElse _originalGateway.Contains("Exception") Then
                _originalGateway = ""
                Dim routeOutput = RunCmd("route", "print 0.0.0.0")
                For Each line In routeOutput.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                    Dim trimmed = line.Trim()
                    If trimmed.StartsWith("0.0.0.0") AndAlso Not trimmed.Contains("10.0.0.") Then
                        Dim p = trimmed.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                        If p.Length >= 3 Then
                            _originalGateway = p(2)
                            Exit For
                        End If
                    End If
                Next
            End If

            ' Method 3: .NET NetworkInterface API
            If String.IsNullOrEmpty(_originalGateway) Then
                For Each ni In NetworkInterface.GetAllNetworkInterfaces()
                    If ni.OperationalStatus = OperationalStatus.Up AndAlso
                       ni.NetworkInterfaceType <> NetworkInterfaceType.Loopback AndAlso
                       Not ni.Name.StartsWith(ADAPTER_NAME) Then
                        For Each gw In ni.GetIPProperties().GatewayAddresses
                            If gw.Address.AddressFamily = Sockets.AddressFamily.InterNetwork Then
                                _originalGateway = gw.Address.ToString()
                                Return True
                            End If
                        Next
                    End If
                Next
            End If

            Log($"Gateway: {_originalGateway} (IF: {_originalIfIndex})")
            Return Not String.IsNullOrEmpty(_originalGateway)
        Catch ex As Exception
            Log($"Failed to capture route: {ex.Message}", True)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Start TUN Mode 1: tun2socks
    ''' </summary>
    Public Function StartTun1(socksPort As Integer, vpnServerHost As String) As Boolean
        Try
            If _isRunning Then StopTun()

            _currentMode = "TUN1"
            _vpnServerIP = ResolveHost(vpnServerHost)
            Log($"Starting TUN tun2socks (VPN server: {_vpnServerIP})")

            ' 1. Aggressive cleanup of old processes/adapters
            AggressiveCleanup()

            ' 2. Get original gateway
            If Not CaptureOriginalRoute() Then
                Log("Cannot determine gateway", True)
                Return False
            End If

            ' 3. Check prerequisites
            Dim tun2socksPath = Path.Combine(_exeDir, "tun2socks.exe")
            If Not File.Exists(tun2socksPath) OrElse Not File.Exists(Path.Combine(_exeDir, "wintun.dll")) Then
                Log("tun2socks.exe or wintun.dll not found", True)
                Return False
            End If

            ' 4. Add VPN server route through original gateway FIRST (prevents loop)
            AddServerRoute()

            ' 5. Start tun2socks (simple device name — no GUID)
            _tunProcess = New Process()
            _tunProcess.StartInfo.FileName = tun2socksPath
            _tunProcess.StartInfo.Arguments = $"-device tun://{ADAPTER_NAME} -proxy socks5://127.0.0.1:{socksPort} -loglevel error"
            _tunProcess.StartInfo.UseShellExecute = False
            _tunProcess.StartInfo.RedirectStandardOutput = True
            _tunProcess.StartInfo.RedirectStandardError = True
            _tunProcess.StartInfo.CreateNoWindow = True
            _tunProcess.StartInfo.WorkingDirectory = _exeDir

            Dim lastTunError As String = ""
            AddHandler _tunProcess.ErrorDataReceived, Sub(s, e)
                                                          If Not String.IsNullOrEmpty(e.Data) Then
                                                              ' Suppress repeated errors to prevent UI freeze
                                                              Dim shortMsg = If(e.Data.Length > 60, e.Data.Substring(0, 60), e.Data)
                                                              If shortMsg = lastTunError Then Return
                                                              lastTunError = shortMsg
                                                              Log($"tun2socks: {e.Data}")
                                                          End If
                                                      End Sub

            _tunProcess.Start()
            _tunProcess.BeginErrorReadLine()

            ' Wait for adapter to appear (retry up to 10s)
            Dim adapterFound = False
            For i = 1 To 10
                Threading.Thread.Sleep(1000)
                If _tunProcess.HasExited Then
                    Log($"tun2socks exited with code {_tunProcess.ExitCode}", True)
                    RemoveRouting()
                    Return False
                End If
                If GetInterfaceIndex(ADAPTER_NAME) > 0 Then
                    adapterFound = True
                    Exit For
                End If
            Next

            If Not adapterFound Then
                Log("SLNET adapter did not appear after 10s", True)
                StopTun()
                Return False
            End If

            ' 6. Configure adapter IP (gateway=none — we use explicit routes)
            RunCmd("netsh", $"interface ip set address name=""{ADAPTER_NAME}"" static {TUN_IP} {TUN_MASK} gateway=none")
            Threading.Thread.Sleep(500)

            ' Set DNS
            RunCmd("netsh", $"interface ip set dns name=""{ADAPTER_NAME}"" static {DNS_PRIMARY}")
            RunCmd("netsh", $"interface ip add dns name=""{ADAPTER_NAME}"" {DNS_SECONDARY} index=2")

            ' 7. Get TUN adapter interface index for precise routing
            Dim tunIfIndex = GetInterfaceIndex(ADAPTER_NAME)

            ' 8. Add split routes through TUN (covers all IPs, more specific than 0.0.0.0/0)
            ' This is the same technique used by OpenVPN and NetMod
            If tunIfIndex > 0 Then
                RunCmd("route", $"add 0.0.0.0 mask 128.0.0.0 {TUN_GATEWAY} metric 6 IF {tunIfIndex}")
                RunCmd("route", $"add 128.0.0.0 mask 128.0.0.0 {TUN_GATEWAY} metric 6 IF {tunIfIndex}")
            Else
                RunCmd("route", $"add 0.0.0.0 mask 128.0.0.0 {TUN_GATEWAY} metric 6")
                RunCmd("route", $"add 128.0.0.0 mask 128.0.0.0 {TUN_GATEWAY} metric 6")
            End If

            _isRunning = True
            Log("TUN tun2socks started")
            Return True
        Catch ex As Exception
            Log($"StartTun1 failed: {ex.Message}", True)
            StopTun()
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Start TUN Mode 2: sing-box
    ''' sing-box uses auto_route with route_exclude_address to prevent routing loops
    ''' </summary>
    Public Function StartTun2(socksPort As Integer, vpnServerHost As String) As Boolean
        Try
            If _isRunning Then StopTun()

            _currentMode = "TUN2"
            _vpnServerIP = ResolveHost(vpnServerHost)
            Log($"Starting TUN sing-box (VPN server: {_vpnServerIP})")

            ' 1. Aggressive cleanup
            AggressiveCleanup()

            ' 2. Get original gateway
            If Not CaptureOriginalRoute() Then
                Log("Cannot determine gateway", True)
                Return False
            End If

            ' 3. Check prerequisites
            Dim singBoxPath = Path.Combine(_exeDir, "sing-box.exe")
            If Not File.Exists(singBoxPath) Then
                Log("sing-box.exe not found", True)
                Return False
            End If

            ' 4. Generate config with VPN server IP excluded from TUN
            Dim configJson = GenerateSingBoxConfig(socksPort)
            File.WriteAllText(_singBoxConfigPath, configJson)

            ' 5. Start sing-box
            _tunProcess = New Process()
            _tunProcess.StartInfo.FileName = singBoxPath
            _tunProcess.StartInfo.Arguments = $"run -c ""{_singBoxConfigPath}"""
            _tunProcess.StartInfo.UseShellExecute = False
            _tunProcess.StartInfo.RedirectStandardOutput = True
            _tunProcess.StartInfo.RedirectStandardError = True
            _tunProcess.StartInfo.CreateNoWindow = True
            _tunProcess.StartInfo.WorkingDirectory = _exeDir

            Dim lastSbError As String = ""
            AddHandler _tunProcess.ErrorDataReceived, Sub(s, e)
                                                          If Not String.IsNullOrEmpty(e.Data) Then
                                                              ' Suppress repeated errors to prevent UI freeze
                                                              Dim shortMsg = If(e.Data.Length > 60, e.Data.Substring(0, 60), e.Data)
                                                              If shortMsg = lastSbError Then Return
                                                              lastSbError = shortMsg
                                                              Log($"sing-box: {e.Data}")
                                                          End If
                                                      End Sub

            _tunProcess.Start()
            _tunProcess.BeginErrorReadLine()

            ' Wait for adapter to appear (retry up to 10s)
            Dim adapterReady = False
            For i = 1 To 10
                Threading.Thread.Sleep(1000)
                If _tunProcess.HasExited Then
                    Log($"sing-box exited with code {_tunProcess.ExitCode}", True)
                    Return False
                End If
                If GetInterfaceIndex(ADAPTER_NAME) > 0 Then
                    adapterReady = True
                    Exit For
                End If
            Next

            If Not adapterReady Then
                Log("SLNET adapter did not appear after 10s (sing-box)", True)
                StopTun()
                Return False
            End If

            _isRunning = True
            Log("TUN sing-box started")
            Return True
        Catch ex As Exception
            Log($"StartTun2 failed: {ex.Message}", True)
            StopTun()
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Stop TUN and clean up
    ''' </summary>
    Public Sub StopTun()
        Try
            ' 1. Kill the TUN process
            Try
                If _tunProcess IsNot Nothing AndAlso Not _tunProcess.HasExited Then
                    _tunProcess.Kill()
                    _tunProcess.WaitForExit(3000)
                End If
            Catch
            End Try
            _tunProcess = Nothing

            ' 2. Kill any leftover processes
            KillProcessByName("tun2socks")
            KillProcessByName("sing-box")

            ' 3. Wait for WinTUN driver to release adapter
            Threading.Thread.Sleep(2000)

            ' 4. Remove routes
            RemoveRouting()

            ' 5. Disable adapter so it can be reused next time
            Try
                RunCmd("netsh", $"interface set interface name=""{ADAPTER_NAME}"" admin=disable")
            Catch
            End Try

            ' 6. Flush DNS cache
            Try
                RunCmd("ipconfig", "/flushdns")
            Catch
            End Try

            ' 7. Delete config file
            Try
                If File.Exists(_singBoxConfigPath) Then File.Delete(_singBoxConfigPath)
            Catch
            End Try

            _isRunning = False
            _currentMode = ""
        Catch ex As Exception
            Log($"StopTun error: {ex.Message}", True)
            _isRunning = False
        End Try
    End Sub

    Public Sub CleanupAdapter()
        StopTun()
    End Sub

    ' ===== Routing =====

    Private Sub AddServerRoute()
        If String.IsNullOrEmpty(_vpnServerIP) OrElse String.IsNullOrEmpty(_originalGateway) Then Return
        Try
            Try : RunCmd("route", $"delete {_vpnServerIP}") : Catch : End Try
            If _originalIfIndex > 0 Then
                RunCmd("route", $"add {_vpnServerIP} mask 255.255.255.255 {_originalGateway} metric 1 IF {_originalIfIndex}")
            Else
                RunCmd("route", $"add {_vpnServerIP} mask 255.255.255.255 {_originalGateway} metric 1")
            End If
            Log($"VPN route: {_vpnServerIP} → {_originalGateway}")
        Catch
        End Try
    End Sub

    Private Sub RemoveRouting()
        Try : RunCmd("route", $"delete 0.0.0.0 mask 128.0.0.0 {TUN_GATEWAY}") : Catch : End Try
        Try : RunCmd("route", $"delete 128.0.0.0 mask 128.0.0.0 {TUN_GATEWAY}") : Catch : End Try
        If Not String.IsNullOrEmpty(_vpnServerIP) Then
            Try : RunCmd("route", $"delete {_vpnServerIP}") : Catch : End Try
        End If
    End Sub

    Private Function GetInterfaceIndex(adapterName As String) As Integer
        Try
            Dim output = RunCmd("powershell", $"-NoProfile -Command ""Get-NetAdapter -Name '{adapterName}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ifIndex""")
            Dim idx As Integer
            If Integer.TryParse(output.Trim(), idx) Then Return idx
        Catch
        End Try
        Return -1
    End Function

    ' ===== sing-box config =====

    Private Function GenerateSingBoxConfig(socksPort As Integer) As String
        ' Exclude VPN server and localhost from TUN capture
        Dim excludeAddresses As New List(Of String)
        If Not String.IsNullOrEmpty(_vpnServerIP) Then
            excludeAddresses.Add($"{_vpnServerIP}/32")
        End If
        excludeAddresses.Add("127.0.0.0/8")

        Dim config As New Dictionary(Of String, Object) From {
            {"log", New Dictionary(Of String, Object) From {
                {"level", "fatal"},
                {"timestamp", True}
            }},
            {"inbounds", New Object() {
                New Dictionary(Of String, Object) From {
                    {"type", "tun"},
                    {"tag", "tun-in"},
                    {"interface_name", ADAPTER_NAME},
                    {"address", New String() {TUN_CIDR}},
                    {"auto_route", True},
                    {"stack", "gvisor"},
                    {"route_exclude_address", excludeAddresses.ToArray()}
                }
            }},
            {"outbounds", New Object() {
                New Dictionary(Of String, Object) From {
                    {"type", "socks"},
                    {"tag", "proxy"},
                    {"server", "127.0.0.1"},
                    {"server_port", socksPort}
                },
                New Dictionary(Of String, Object) From {
                    {"type", "direct"},
                    {"tag", "direct"}
                }
            }},
            {"dns", New Dictionary(Of String, Object) From {
                {"servers", New Object() {
                    New Dictionary(Of String, Object) From {
                        {"tag", "proxy-dns"},
                        {"address", "8.8.8.8"},
                        {"detour", "proxy"}
                    }
                }},
                {"independent_cache", True}
            }},
            {"route", New Dictionary(Of String, Object) From {
                {"auto_detect_interface", True},
                {"rules", New Object() {
                    New Dictionary(Of String, Object) From {
                        {"action", "hijack-dns"},
                        {"protocol", "dns"}
                    },
                    New Dictionary(Of String, Object) From {
                        {"action", "route"},
                        {"ip_cidr", New String() {"10.0.0.0/24"}},
                        {"outbound", "proxy"}
                    },
                    New Dictionary(Of String, Object) From {
                        {"action", "route"},
                        {"ip_is_private", True},
                        {"outbound", "direct"}
                    }
                }}
            }}
        }

        Return JsonSerializer.Serialize(config, New JsonSerializerOptions With {
            .WriteIndented = True,
            .Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        })
    End Function

    ' ===== Utilities =====

    Private Function ResolveHost(host As String) As String
        Try
            Dim addr As IPAddress = Nothing
            If IPAddress.TryParse(host, addr) Then Return host
            Dim addresses = Dns.GetHostAddresses(host)
            For Each a In addresses
                If a.AddressFamily = Sockets.AddressFamily.InterNetwork Then Return a.ToString()
            Next
            If addresses.Length > 0 Then Return addresses(0).ToString()
        Catch
        End Try
        Return host
    End Function

    Private Function RunCmd(fileName As String, arguments As String) As String
        Try
            Dim proc As New Process()
            proc.StartInfo.FileName = fileName
            proc.StartInfo.Arguments = arguments
            proc.StartInfo.UseShellExecute = False
            proc.StartInfo.RedirectStandardOutput = True
            proc.StartInfo.RedirectStandardError = True
            proc.StartInfo.CreateNoWindow = True
            proc.Start()
            Dim output = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit(10000)
            Return output
        Catch
            Return ""
        End Try
    End Function

    Private Sub KillProcessByName(name As String)
        Try
            For Each proc In Process.GetProcessesByName(name)
                Try : proc.Kill() : proc.WaitForExit(2000) : Catch : End Try
            Next
        Catch
        End Try
    End Sub
End Class
