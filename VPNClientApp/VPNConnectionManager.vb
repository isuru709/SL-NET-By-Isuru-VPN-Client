Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Net.Security
Imports System.Security.Authentication
Imports System.Threading
Imports System.Threading.Tasks
Imports Renci.SshNet

Public Class VPNConnectionManager
    ' === Add the missing Win32 priority constants and P/Invoke declarations ===
    Private Const PROCESS_SET_INFORMATION As UInteger = &H200UI
    Private Const HIGH_PRIORITY_CLASS As UInteger = &H80UI

    <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function OpenProcess(dwDesiredAccess As UInteger, bInheritHandle As Boolean, dwProcessId As Integer) As IntPtr
    End Function

    <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetPriorityClass(hProcess As IntPtr, dwPriorityClass As UInteger) As Boolean
    End Function

    <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    ' (rest of fields remain unchanged)
    Private _xrayProcess As Process
    Private _sshProcess As Process
    Private _stunnelProcess As Process
    Private _isConnected As Boolean = False
    Private _currentConfig As Object
    Private _configFilePath As String
    Private _xrayCorePath As String
    Private _bytesDownloaded As Long = 0
    Private _bytesUploaded As Long = 0
    Private _sshClient As SshClient
    Private _sshDynamic As ForwardedPortDynamic
    Private _tlsListener As TcpListener
    Private _tlsCts As CancellationTokenSource
    Private _tlsLocalPort As Integer = 0
    Private _tlsAcceptLoopTask As Task
    Private _socksPort As Integer = 0
    Private _httpPort As Integer = 0
    Private _gamingModeEnabled As Boolean = True
    Private _currentProxyMode As String = "System"
    Private _previousProxySettings As Dictionary(Of String, Object) = Nothing
    Private _blockerSettings As New BlockerSettings()
    Private _externalBlockDomains As List(Of String) = New List(Of String)()
    Private _splitTunnelSettings As New SplitTunnelSettings()
    Private _tunManager As TunAdapterManager
    Private _vpnServerHost As String = ""
    Public Property EnableLowLatencySSHMode As Boolean = False
    Public Event LogMessage(message As String, isError As Boolean)

    ' Expose allocated local proxy ports for sharing functionality
    Public ReadOnly Property LocalHttpPort As Integer
        Get
            Return _httpPort
        End Get
    End Property

    Public ReadOnly Property LocalSocksPort As Integer
        Get
            Return _socksPort
        End Get
    End Property

    Public Sub New()
        Dim exePath = System.Reflection.Assembly.GetExecutingAssembly().Location
        Dim exeDir = Path.GetDirectoryName(exePath)

        _configFilePath = Path.Combine(exeDir, "xray_config.json")
        _xrayCorePath = Path.Combine(exeDir, "xray.exe")

        If Not File.Exists(_xrayCorePath) Then
            Dim altPath = Path.Combine(exeDir, "xray-core.exe")
            If File.Exists(altPath) Then
                _xrayCorePath = altPath
            End If
        End If

        ' Initialize TUN adapter manager
        _tunManager = New TunAdapterManager()
        AddHandler _tunManager.LogMessage, Sub(msg, isErr) Log(msg, isErr)
    End Sub

    Public Sub UpdateBlockerSettings(settings As BlockerSettings)
        If settings Is Nothing Then Return
        _blockerSettings = settings
    End Sub

    Public Function GetBlockerSettings() As BlockerSettings
        Return _blockerSettings
    End Function

    Public Sub UpdateExternalBlockDomains(domains As IEnumerable(Of String))
        Try
            If domains Is Nothing Then
                _externalBlockDomains = New List(Of String)()
            Else
                ' limit size for performance
                _externalBlockDomains = domains.Distinct(StringComparer.OrdinalIgnoreCase).Take(8000).ToList()
            End If
            Log($"External block domains loaded: {_externalBlockDomains.Count}")
        Catch ex As Exception
            Log($"UpdateExternalBlockDomains error: {ex.Message}")
        End Try
    End Sub

    Public Async Function ApplyBlockerRulesAsync() As Task
        Try
            If _isConnected Then
                ' Regenerate and reload Xray config based on current connection type
                If TypeOf _currentConfig Is VLessConfig Then
                    Dim cfg = DirectCast(_currentConfig, VLessConfig)
                    Dim xrayConfig = GenerateXrayConfig(cfg)
                    File.WriteAllText(_configFilePath, xrayConfig)
                ElseIf TypeOf _currentConfig Is SSHTLSConfig Then
                    Dim cfg = DirectCast(_currentConfig, SSHTLSConfig)
                    Dim xrayConfig = GenerateSSHTLSXrayConfig(cfg, EnableLowLatencySSHMode)
                    File.WriteAllText(_configFilePath, xrayConfig)
                Else
                    Return
                End If

                ' Soft restart xray
                Try
                    If _xrayProcess IsNot Nothing AndAlso Not _xrayProcess.HasExited Then
                        _xrayProcess.Kill()
                        _xrayProcess.WaitForExit(2000)
                    End If
                Catch
                End Try

                Await StartXrayCoreAsync()
                Log("Applied blocker rules and reloaded proxy core")
            End If
        Catch ex As Exception
            Log($"ApplyBlockerRules failed: {ex.Message}", True)
        End Try
    End Function

    Public Sub UpdateSplitTunnelSettings(settings As SplitTunnelSettings)
        If settings Is Nothing Then Return
        _splitTunnelSettings = settings
    End Sub

    Public Function GetSplitTunnelSettings() As SplitTunnelSettings
        Return _splitTunnelSettings
    End Function

    Public Async Function ApplySplitTunnelRulesAsync() As Task
        Try
            If _isConnected Then
                ' Regenerate and reload Xray config based on current connection type
                If TypeOf _currentConfig Is VLessConfig Then
                    Dim cfg = DirectCast(_currentConfig, VLessConfig)
                    Dim xrayConfig = GenerateXrayConfig(cfg)
                    File.WriteAllText(_configFilePath, xrayConfig)
                ElseIf TypeOf _currentConfig Is SSHTLSConfig Then
                    Dim cfg = DirectCast(_currentConfig, SSHTLSConfig)
                    Dim xrayConfig = GenerateSSHTLSXrayConfig(cfg, EnableLowLatencySSHMode)
                    File.WriteAllText(_configFilePath, xrayConfig)
                Else
                    Return
                End If

                ' Soft restart xray
                Try
                    If _xrayProcess IsNot Nothing AndAlso Not _xrayProcess.HasExited Then
                        _xrayProcess.Kill()
                        _xrayProcess.WaitForExit(2000)
                    End If
                Catch
                End Try

                Await StartXrayCoreAsync()
                Log("Applied split tunnel rules and reloaded proxy core")
            End If
        Catch ex As Exception
            Log($"ApplySplitTunnelRules failed: {ex.Message}", True)
        End Try
    End Function

    Private Sub Log(message As String, Optional isError As Boolean = False)
        RaiseEvent LogMessage(message, isError)
        Debug.WriteLine($"[VPNManager] {message}")
    End Sub

    ''' <summary>
    ''' Enable or disable gaming optimization mode
    ''' </summary>
    Public Sub SetGamingMode(enabled As Boolean)
        _gamingModeEnabled = enabled
        Log($"Gaming mode: {If(enabled, "ENABLED", "DISABLED")}")
    End Sub

    ''' <summary>
    ''' Find an available port on the system
    ''' </summary>
    Private Function FindAvailablePort(Optional startPort As Integer = 10800) As Integer
        Try
            Dim properties = IPGlobalProperties.GetIPGlobalProperties()
            Dim activePorts = properties.GetActiveTcpListeners().Select(Function(p) p.Port).ToList()

            Dim connections = properties.GetActiveTcpConnections()
            For Each conn In connections
                If Not activePorts.Contains(conn.LocalEndPoint.Port) Then
                    activePorts.Add(conn.LocalEndPoint.Port)
                End If
            Next

            For port = startPort To startPort + 1000
                If Not activePorts.Contains(port) Then
                    Try
                        Dim listener = New TcpListener(IPAddress.Loopback, port)
                        listener.Start()
                        listener.Stop()
                        Return port
                    Catch
                        Continue For
                    End Try
                End If
            Next

            Dim tempListener = New TcpListener(IPAddress.Loopback, 0)
            tempListener.Start()
            Dim assignedPort = CType(tempListener.LocalEndpoint, IPEndPoint).Port
            tempListener.Stop()
            Return assignedPort
        Catch ex As Exception
            Log($"Port allocation error: {ex.Message}", True)
            Return New Random().Next(20000, 30000)
        End Try
    End Function

    ''' <summary>
    ''' Allocate ports for SOCKS and HTTP proxies - with exclusion list
    ''' </summary>
    Private Sub AllocatePorts(Optional excludePorts As List(Of Integer) = Nothing)
        Try
            If excludePorts Is Nothing Then
                excludePorts = New List(Of Integer)
            End If

            Dim standardPorts As Integer() = {1080, 1081, 8888, 10808, 10809}

            ' Allocate SOCKS port
            For Each port In standardPorts
                If Not excludePorts.Contains(port) AndAlso IsPortAvailable(port) AndAlso _socksPort = 0 Then
                    _socksPort = port
                    Log($"Allocated SOCKS port: {_socksPort}")
                    Exit For
                End If
            Next

            If _socksPort = 0 Then
                _socksPort = FindAvailablePortExcluding(10800, excludePorts)
                Log($"Allocated dynamic SOCKS port: {_socksPort}")
            End If

            ' Allocate HTTP port (exclude SOCKS and any other excluded ports)
            excludePorts.Add(_socksPort)

            For Each port In standardPorts
                If Not excludePorts.Contains(port) AndAlso IsPortAvailable(port) AndAlso _httpPort = 0 Then
                    _httpPort = port
                    Log($"Allocated HTTP port: {_httpPort}")
                    Exit For
                End If
            Next

            If _httpPort = 0 Then
                _httpPort = FindAvailablePortExcluding(_socksPort + 1, excludePorts)
                Log($"Allocated dynamic HTTP port: {_httpPort}")
            End If

            Log($"Ports allocated - SOCKS: {_socksPort}, HTTP: {_httpPort}")
        Catch ex As Exception
            Log($"Port allocation failed: {ex.Message}", True)
            Throw
        End Try
    End Sub

    ''' <summary>
    ''' Find available port excluding specific ports
    ''' </summary>
    Private Function FindAvailablePortExcluding(startPort As Integer, excludePorts As List(Of Integer)) As Integer
        Try
            Dim properties = IPGlobalProperties.GetIPGlobalProperties()
            Dim activePorts = properties.GetActiveTcpListeners().Select(Function(p) p.Port).ToList()

            Dim connections = properties.GetActiveTcpConnections()
            For Each conn In connections
                If Not activePorts.Contains(conn.LocalEndPoint.Port) Then
                    activePorts.Add(conn.LocalEndPoint.Port)
                End If
            Next

            ' Add excluded ports to active ports list
            activePorts.AddRange(excludePorts)

            For port = startPort To startPort + 1000
                If Not activePorts.Contains(port) Then
                    Try
                        Dim listener = New TcpListener(IPAddress.Loopback, port)
                        listener.Start()
                        listener.Stop()
                        Return port
                    Catch
                        Continue For
                    End Try
                End If
            Next

            ' Fallback to system-assigned port
            Dim tempListener = New TcpListener(IPAddress.Loopback, 0)
            tempListener.Start()
            Dim assignedPort = CType(tempListener.LocalEndpoint, IPEndPoint).Port
            tempListener.Stop()
            Return assignedPort
        Catch ex As Exception
            Log($"Port allocation error: {ex.Message}", True)
            Return New Random().Next(20000, 30000)
        End Try
    End Function

    ''' <summary>
    ''' Check if a port is available
    ''' </summary>
    Private Function IsPortAvailable(port As Integer) As Boolean
        Try
            Dim listener = New TcpListener(IPAddress.Loopback, port)
            listener.Start()
            listener.Stop()
            Return True
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Start a lightweight local TLS tunnel (like stunnel) for SSH-over-TLS.
    ''' Returns the local listening port.
    ''' </summary>
    Private Function StartLocalTlsTunnel(config As SSHTLSConfig) As Integer
        Try
            StopLocalTlsTunnel()

            _tlsLocalPort = FindAvailablePort(24000)
            _tlsListener = New TcpListener(IPAddress.Loopback, _tlsLocalPort)
            _tlsListener.Start()
            _tlsCts = New CancellationTokenSource()

            Dim targetSNI As String = If(String.IsNullOrWhiteSpace(config.SNI), config.Host, config.SNI)
            Log($"Starting TLS tunnel: 127.0.0.1:{_tlsLocalPort} -> {config.Host}:{config.Port} (SNI: {targetSNI})")

            Dim listenerRef = _tlsListener
            Dim ctsRef = _tlsCts

            _tlsAcceptLoopTask = Task.Run(Async Function()
                                              Try
                                                  While ctsRef IsNot Nothing AndAlso Not ctsRef.IsCancellationRequested
                                                      Dim localClient As TcpClient = Nothing
                                                      Try
                                                          localClient = Await listenerRef.AcceptTcpClientAsync()
                                                          localClient.NoDelay = True

                                                          Using remote As New TcpClient()
                                                              remote.NoDelay = True
                                                              Await remote.ConnectAsync(config.Host, config.Port)

                                                              Using ssl As New SslStream(remote.GetStream(), False, Function(sender, certificate, chain, errors) True)
                                                                  Try
                                                                      ssl.AuthenticateAsClient(targetSNI, Nothing, SslProtocols.Tls12, False)
                                                                  Catch authEx As Exception
                                                                      Log($"TLS authenticate failed: {authEx.Message}", True)
                                                                      Throw
                                                                  End Try

                                                                  Dim token = ctsRef.Token
                                                                  Dim t1 = PipeAsync(localClient.GetStream(), ssl, token)
                                                                  Dim t2 = PipeAsync(ssl, localClient.GetStream(), token)
                                                                  Await Task.WhenAny(t1, t2)
                                                              End Using
                                                          End Using
                                                      Catch ex As ObjectDisposedException
                                                          Exit While
                                                      Catch ex As Exception
                                                          If ctsRef IsNot Nothing AndAlso Not ctsRef.IsCancellationRequested Then
                                                              Log($"TLS tunnel error: {ex.Message}", True)
                                                          Else
                                                              Exit While
                                                          End If
                                                      Finally
                                                          Try
                                                              If localClient IsNot Nothing Then localClient.Close()
                                                          Catch
                                                          End Try
                                                      End Try
                                                  End While
                                              Catch loopEx As Exception
                                                  If ctsRef IsNot Nothing AndAlso Not ctsRef.IsCancellationRequested Then
                                                      Log($"TLS tunnel loop error: {loopEx.Message}", True)
                                                  End If
                                              End Try
                                          End Function)

            Return _tlsLocalPort
        Catch ex As Exception
            Log($"Failed to start local TLS tunnel: {ex.Message}", True)
            Throw
        End Try
    End Function

    Private Async Function PipeAsync(input As Stream, output As Stream, ct As CancellationToken) As Task
        Dim buffer = New Byte(8192 - 1) {}
        Try
            While Not ct.IsCancellationRequested
                Dim n = Await input.ReadAsync(buffer, 0, buffer.Length)
                If n = 0 Then Exit While
                Await output.WriteAsync(buffer, 0, n)
                Await output.FlushAsync()
            End While
        Catch
        End Try
    End Function

    Private Sub StopLocalTlsTunnel()
        Try
            If _tlsCts IsNot Nothing Then
                _tlsCts.Cancel()
            End If
        Catch
        End Try

        Try
            If _tlsListener IsNot Nothing Then
                _tlsListener.Stop()
            End If
        Catch
        End Try

        Try
            If _tlsAcceptLoopTask IsNot Nothing Then
                Try
                    _tlsAcceptLoopTask.Wait(500)
                Catch
                Finally
                    _tlsAcceptLoopTask = Nothing
                End Try
            End If
        Catch
        End Try

        Try
            If _tlsCts IsNot Nothing Then
                _tlsCts.Dispose()
            End If
        Catch
        End Try

        _tlsCts = Nothing
        _tlsListener = Nothing
        _tlsLocalPort = 0
    End Sub

    Public Async Function ConnectWithSSHTLSAsync(config As SSHTLSConfig, Optional proxyMode As String = "System") As Task(Of Boolean)
        Try
            Log($"SSH+TLS Connection: {config.Host}:{config.Port}")
            Log($"Username: {config.Username}")

            If _isConnected Then
                Disconnect()
            End If

            _currentConfig = config
            _vpnServerHost = config.Host

            ' Track all ports that will be used to avoid conflicts
            Dim reservedPorts As New List(Of Integer)

            ' Step 1: Allocate/validate SOCKS local port for SSH
            If config.LocalPort <= 0 OrElse Not IsPortAvailable(config.LocalPort) Then
                config.LocalPort = FindAvailablePort(10800)
                Log($"Allocated SSH local SOCKS port: {config.LocalPort}")
            End If
            reservedPorts.Add(config.LocalPort)

            ' Step 2: Probe for raw SSH and start TLS tunnel if needed
            Dim targetHost As String = config.Host
            Dim targetPort As Integer = config.Port

            Dim rawSSHDetected As Boolean = IsRawSSHServer(config.Host, config.Port)
            Dim willUseTlsTunnel As Boolean = False

            If config.UseTLS AndAlso Not rawSSHDetected Then
                targetPort = StartLocalTlsTunnel(config)
                targetHost = "127.0.0.1"
                willUseTlsTunnel = True
                reservedPorts.Add(_tlsLocalPort)
                Log($"TLS tunnel enabled on port {_tlsLocalPort} (UseTLS=True and no raw SSH banner).")
                Await Task.Delay(100)
            Else
                Log(If(rawSSHDetected,
                       "Raw SSH banner detected: using direct SSH (no TLS tunnel).",
                       "No SSH banner detected; using direct SSH"))
                StopLocalTlsTunnel()
            End If

            ' Step 3: Start SSH tunnel FIRST (this binds config.LocalPort)
            Dim sshStarted = Await StartOptimizedSSHTunnelAsync(config, targetHost, targetPort)

            If Not sshStarted AndAlso config.UseTLS AndAlso Not willUseTlsTunnel Then
                Log("Direct SSH failed and UseTLS=True. Retrying with TLS tunnel fallback...")
                targetPort = StartLocalTlsTunnel(config)
                targetHost = "127.0.0.1"
                reservedPorts.Add(_tlsLocalPort)
                Await Task.Delay(150)
                sshStarted = Await StartOptimizedSSHTunnelAsync(config, targetHost, targetPort)
            End If

            If Not sshStarted Then
                Log("Failed to start SSH tunnel", True)
                StopLocalTlsTunnel()
                Return False
            End If

            Log("SSH tunnel established successfully")

            ' Step 4: NOW allocate Xray ports (HTTP + SOCKS) - AFTER SSH is running
            ' This ensures port conflict detection works properly
            AllocatePorts(reservedPorts)

            ' Step 5: Generate Xray config
            Dim xrayConfig = GenerateSSHTLSXrayConfig(config, EnableLowLatencySSHMode)
            File.WriteAllText(_configFilePath, xrayConfig)
            Log("Generated Xray configuration for SSH bridge")

            ' Step 6: Start Xray-core
            Dim xrayStarted = Await StartXrayCoreAsync()
            If Not xrayStarted Then
                Log("Failed to start Xray-core", True)
                Disconnect()
                Return False
            End If
            Log("Xray-core started successfully")

            ' Step 7: Set system proxy with selected mode
            SetSystemProxy(True, proxyMode)
            Log($"System proxy configured (mode: {proxyMode})")

            _isConnected = True
            Log("=== SSH CONNECTION ESTABLISHED (LOW LATENCY MODE: " & EnableLowLatencySSHMode.ToString().ToUpper() & ") ===")
            Log($"HTTP Proxy: 127.0.0.1:{_httpPort}")
            Log($"SOCKS (SSH Dynamic): 127.0.0.1:{config.LocalPort}")
            Return True
        Catch ex As Exception
            Log($"SSH+TLS connection error: {ex.Message}", True)
            _isConnected = False
            Throw
        End Try
    End Function

    ' Simplified / low-latency Xray config generator for SSH bridging
    Private Function GenerateSSHTLSXrayConfig(config As SSHTLSConfig, lowLatency As Boolean) As String
        Dim logLevel = If(lowLatency, "error", "warning")

        Dim inbounds As New List(Of Object) From {
            New Dictionary(Of String, Object) From {
                {"tag", "http"},
                {"port", _httpPort},
                {"listen", "127.0.0.1"},
                {"protocol", "http"}
            }
        }

        If Not lowLatency Then
            inbounds.Add(
                New Dictionary(Of String, Object) From {
                    {"tag", "socks"},
                    {"port", _socksPort},
                    {"listen", "127.0.0.1"},
                    {"protocol", "socks"},
                    {"settings", New Dictionary(Of String, Object) From {
                        {"auth", "noauth"},
                        {"udp", False}
                    }}
                }
            )
        End If

        Dim configObj As New Dictionary(Of String, Object) From {
            {"log", New Dictionary(Of String, Object) From {
                {"loglevel", logLevel}
            }},
            {"inbounds", inbounds.ToArray()},
            {"outbounds", New Object() {
                New Dictionary(Of String, Object) From {
                    {"protocol", "socks"},
                    {"settings", New Dictionary(Of String, Object) From {
                        {"servers", New Object() {
                            New Dictionary(Of String, Object) From {
                                {"address", "127.0.0.1"},
                                {"port", config.LocalPort}
                            }
                        }}
                    }},
                    {"tag", "ssh-tunnel"}
                },
                New Dictionary(Of String, Object) From {
                    {"protocol", "freedom"},
                    {"tag", "direct"}
                },
                New Dictionary(Of String, Object) From {
                    {"protocol", "blackhole"},
                    {"tag", "block"}
                }
            }}
        }

        ' Inject routing rules for blocker
        Dim routingRules = BuildRoutingRules()
        If routingRules IsNot Nothing Then
            configObj("routing") = New Dictionary(Of String, Object) From {
                {"domainStrategy", "AsIs"},
                {"rules", routingRules}
            }
        Else
            configObj("routing") = New Dictionary(Of String, Object) From {
                {"domainStrategy", "AsIs"},
                {"rules", New Object() {
                    New Dictionary(Of String, Object) From {
                        {"type", "field"},
                        {"ip", New String() {"geoip:private"}},
                        {"outboundTag", "direct"}
                    }
                }}
            }
        End If

        Dim options As New JsonSerializerOptions With {
            .WriteIndented = True,
            .Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }

        Return JsonSerializer.Serialize(configObj, options)
    End Function

    Private Async Function StartOptimizedSSHTunnelAsync(config As SSHTLSConfig, targetHost As String, targetPort As Integer) As Task(Of Boolean)
        Return Await Task.Run(Function()
                                  Try
                                      Dim methods As New List(Of AuthenticationMethod)
                                      
                                      ' Add authentication methods based on configuration
                                      If config.UseKeyAuth Then
                                          ' Private key authentication
                                          If String.IsNullOrEmpty(config.PrivateKeyPath) OrElse Not IO.File.Exists(config.PrivateKeyPath) Then
                                              Log("SSH private key file not found", True)
                                              Return False
                                          End If
                                          
                                          Try
                                              Dim keyFile As PrivateKeyFile
                                              If Not String.IsNullOrEmpty(config.Passphrase) Then
                                                  ' Encrypted key with passphrase
                                                  keyFile = New PrivateKeyFile(config.PrivateKeyPath, config.Passphrase)
                                                  Log("Using encrypted private key with passphrase")
                                              Else
                                                  ' Unencrypted key
                                                  keyFile = New PrivateKeyFile(config.PrivateKeyPath)
                                                  Log("Using unencrypted private key")
                                              End If
                                              methods.Add(New PrivateKeyAuthenticationMethod(config.Username, keyFile))
                                          Catch ex As Exception
                                              Log($"Failed to load private key: {ex.Message}", True)
                                              Return False
                                          End Try
                                      Else
                                          ' Password authentication
                                          If String.IsNullOrEmpty(config.Password) Then
                                              Log("SSH password is empty", True)
                                              Return False
                                          End If
                                          methods.Add(New PasswordAuthenticationMethod(config.Username, config.Password))
                                          Log("Using password authentication")
                                      End If

                                      If methods.Count = 0 Then
                                          Log("No authentication method configured", True)
                                          Return False
                                      End If

                                      Dim connInfo As New ConnectionInfo(targetHost, targetPort, config.Username, methods.ToArray()) With {
                                          .Timeout = TimeSpan.FromSeconds(20)
                                      }

                                      _sshClient = New SshClient(connInfo) With {
                                          .KeepAliveInterval = TimeSpan.FromSeconds(12)
                                      }
                                      _sshClient.ConnectionInfo.RetryAttempts = 1

                                      Log($"Connecting to SSH server {targetHost}:{targetPort} as {config.Username}...")
                                      _sshClient.Connect()
                                      
                                      If Not _sshClient.IsConnected Then
                                          Log("SSH connection failed (not connected)", True)
                                          Return False
                                      End If

                                      Log("SSH authentication successful")
                                      
                                      _sshDynamic = New ForwardedPortDynamic("127.0.0.1", CUInt(config.LocalPort))
                                      _sshClient.AddForwardedPort(_sshDynamic)
                                      _sshDynamic.Start()

                                      Log($"Optimized SSH dynamic SOCKS started on 127.0.0.1:{config.LocalPort}")
                                      Return True
                                  Catch authEx As Renci.SshNet.Common.SshAuthenticationException
                                      Log($"SSH authentication failed: {authEx.Message}. Check username/password or private key.", True)
                                      Return False
                                  Catch connEx As Renci.SshNet.Common.SshConnectionException
                                      Log($"SSH connection error: {connEx.Message}. Check host/port and network.", True)
                                      Return False
                                  Catch ex As Exception
                                      Log($"SSH.NET tunnel start failed: {ex.Message}", True)
                                      Return False
                                  End Try
                              End Function)
    End Function

    Private Sub SetSystemProxy(enable As Boolean, Optional proxyMode As String = "System")
        Try
            ' Save current proxy mode when enabling
            If enable Then
                _currentProxyMode = proxyMode
                ' Backup current settings before changing
                BackupProxySettings()
            End If

            ' Handle TUN modes separately (no registry proxy needed)
            If enable AndAlso (proxyMode.Equals("TUN1", StringComparison.OrdinalIgnoreCase) OrElse
                               proxyMode.Equals("TUN2", StringComparison.OrdinalIgnoreCase)) Then
                Dim tunStarted As Boolean = False
                If proxyMode.Equals("TUN1", StringComparison.OrdinalIgnoreCase) Then
                    tunStarted = _tunManager.StartTun1(_socksPort, _vpnServerHost)
                Else
                    tunStarted = _tunManager.StartTun2(_socksPort, _vpnServerHost)
                End If

                If Not tunStarted Then
                    Log($"TUN mode {proxyMode} failed to start.", True)
                    _tunManager.StopTun()
                    Return
                Else
                    Log($"TUN mode {proxyMode} active")
                    Return
                End If
            End If

            ' Stop TUN if switching away from TUN mode
            If Not enable AndAlso _tunManager IsNot Nothing AndAlso _tunManager.IsRunning Then
                _tunManager.StopTun()
            End If

            Dim registryKey = "Software\Microsoft\Windows\CurrentVersion\Internet Settings"
            Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey, True)
                If key IsNot Nothing Then
                    If enable Then
                        Select Case proxyMode.ToLower()
                            Case "system"
                                key.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord)
                                key.SetValue("ProxyServer", $"127.0.0.1:{_httpPort}", Microsoft.Win32.RegistryValueKind.String)
                                key.SetValue("ProxyOverride", "<local>", Microsoft.Win32.RegistryValueKind.String)
                                Log($"System proxy enabled: 127.0.0.1:{_httpPort}")

                            Case "global"
                                key.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord)
                                key.SetValue("ProxyServer", $"http=127.0.0.1:{_httpPort};https=127.0.0.1:{_httpPort};socks=127.0.0.1:{_socksPort}", Microsoft.Win32.RegistryValueKind.String)
                                key.DeleteValue("ProxyOverride", False)
                                Log($"Global proxy enabled: HTTP={_httpPort}, SOCKS={_socksPort}")

                            Case Else
                                key.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord)
                                key.SetValue("ProxyServer", $"127.0.0.1:{_httpPort}", Microsoft.Win32.RegistryValueKind.String)
                                key.SetValue("ProxyOverride", "<local>", Microsoft.Win32.RegistryValueKind.String)
                                Log($"System proxy enabled (default): 127.0.0.1:{_httpPort}")
                        End Select
                    Else
                        RestoreProxySettings()
                    End If
                End If
            End Using

            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0)
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0)
        Catch ex As Exception
            Log($"Failed to set system proxy: {ex.Message}", True)
        End Try
    End Sub

    ''' <summary>
    ''' Backup current proxy settings before modifying
    ''' </summary>
    Private Sub BackupProxySettings()
        Try
            _previousProxySettings = New Dictionary(Of String, Object)
            Dim registryKey = "Software\Microsoft\Windows\CurrentVersion\Internet Settings"
            Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey, False)
                If key IsNot Nothing Then
                    Try
                        _previousProxySettings("ProxyEnable") = key.GetValue("ProxyEnable", 0)
                    Catch
                        _previousProxySettings("ProxyEnable") = 0
                    End Try

                    Try
                        _previousProxySettings("ProxyServer") = key.GetValue("ProxyServer", "")
                    Catch
                        _previousProxySettings("ProxyServer") = ""
                    End Try

                    Try
                        _previousProxySettings("ProxyOverride") = key.GetValue("ProxyOverride", "")
                    Catch
                        _previousProxySettings("ProxyOverride") = ""
                    End Try

                    Try
                        _previousProxySettings("AutoConfigURL") = key.GetValue("AutoConfigURL", "")
                    Catch
                        _previousProxySettings("AutoConfigURL") = ""
                    End Try

                    Log("Previous proxy settings backed up")
                End If
            End Using
        Catch ex As Exception
            Log($"Failed to backup proxy settings: {ex.Message}", True)
        End Try
    End Sub

    ''' <summary>
    ''' Restore previous proxy settings
    ''' </summary>
    Private Sub RestoreProxySettings()
        ' Always force proxy OFF and AutoDetect ON instead of restoring backup
        ' This prevents "Use a proxy server" from staying ON after disconnect
        ForceCleanProxyState()
    End Sub

    ''' <summary>
    ''' Force Windows proxy settings to clean defaults:
    ''' - "Use a proxy server" = OFF
    ''' - "Automatically detect settings" = ON
    ''' - Remove ProxyServer, ProxyOverride, AutoConfigURL values
    ''' Call this on disconnect and app shutdown to guarantee clean state.
    ''' </summary>
    Public Sub ForceCleanProxyState()
        Try
            Dim registryKey = "Software\Microsoft\Windows\CurrentVersion\Internet Settings"
            Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey, True)
                If key IsNot Nothing Then
                    ' Force "Use a proxy server" OFF
                    key.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord)
                    ' Remove proxy server address
                    key.DeleteValue("ProxyServer", False)
                    ' Remove proxy bypass list
                    key.DeleteValue("ProxyOverride", False)
                    ' Remove auto-config script URL
                    key.DeleteValue("AutoConfigURL", False)
                End If
            End Using

            ' Also force "Automatically detect settings" ON via Connections registry
            Dim connKey = "Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections"
            Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(connKey, True)
                If key IsNot Nothing Then
                    Dim settings = TryCast(key.GetValue("DefaultConnectionSettings"), Byte())
                    If settings IsNot Nothing AndAlso settings.Length >= 9 Then
                        ' Byte index 8 controls proxy flags:
                        ' Bit 0x01 = unused, Bit 0x02 = unused,
                        ' Bit 0x04 = use manual proxy, Bit 0x08 = auto-detect
                        ' We want: auto-detect ON (0x08), manual proxy OFF (clear 0x04)
                        settings(8) = CByte((settings(8) Or &H8) And (Not CByte(&H4)))
                        ' Increment the revision counter (bytes 4-7 as Int32) so Windows picks up the change
                        Dim revision = BitConverter.ToInt32(settings, 4)
                        revision += 1
                        Dim revBytes = BitConverter.GetBytes(revision)
                        Array.Copy(revBytes, 0, settings, 4, 4)
                        key.SetValue("DefaultConnectionSettings", settings, Microsoft.Win32.RegistryValueKind.Binary)
                    End If
                End If
            End Using

            ' Notify Windows that proxy settings have changed
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0)
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0)

            _previousProxySettings = Nothing
            Log("Proxy forced to clean state: proxy OFF, auto-detect ON")
        Catch ex As Exception
            Log($"Failed to force clean proxy state: {ex.Message}", True)
        End Try
    End Sub

    ''' <summary>
    ''' Generate a simple PAC (Proxy Auto-Config) script
    ''' </summary>
    Private Function GeneratePACScript() As String
        Try
            ' Create a simple PAC script content
            Dim pacContent = $"function FindProxyForURL(url, host) {{
    // Direct connection for local addresses
    if (isPlainHostName(host) ||
        shExpMatch(host, '*.local') ||
        isInNet(dnsResolve(host), '10.0.0.0', '255.0.0.0') ||
        isInNet(dnsResolve(host), '172.16.0.0', '255.240.0.0') ||
        isInNet(dnsResolve(host), '192.168.0.0', '255.255.0.0') ||
        isInNet(dnsResolve(host), '127.0.0.0', '255.0.0.0'))
        return 'DIRECT';
    
    // Use proxy for everything else
    return 'PROXY 127.0.0.1:{_httpPort}; SOCKS5 127.0.0.1:{_socksPort}; DIRECT';
}}"

            ' Save PAC script to a file
            Dim exePath = System.Reflection.Assembly.GetExecutingAssembly().Location
            Dim exeDir = Path.GetDirectoryName(exePath)
            Dim pacFilePath = Path.Combine(exeDir, "proxy.pac")
            File.WriteAllText(pacFilePath, pacContent)
            
            ' Return file:// URL
            Return "file:///" & pacFilePath.Replace("\", "/")
        Catch ex As Exception
            Log($"Failed to generate PAC script: {ex.Message}", True)
            Return ""
        End Try
    End Function

    Private Async Function StartXrayCoreAsync() As Task(Of Boolean)
        Try
            If Not File.Exists(_xrayCorePath) Then
                Dim exeDir = Path.GetDirectoryName(_xrayCorePath)
                Dim errorMsg = "Xray-core not found!" & vbCrLf & vbCrLf &
                    "Please download xray-core from:" & vbCrLf &
                    "https://github.com/XTLS/Xray-core/releases" & vbCrLf & vbCrLf &
                    "Extract 'xray.exe' to:" & vbCrLf &
                    exeDir

                Log(errorMsg, True)
                Throw New FileNotFoundException(errorMsg)
            End If

            Log($"Xray-core path: {_xrayCorePath}")
            Log($"Config file: {_configFilePath}")

            _xrayProcess = New Process()
            _xrayProcess.StartInfo.FileName = _xrayCorePath
            _xrayProcess.StartInfo.Arguments = $"run -c ""{_configFilePath}"""
            _xrayProcess.StartInfo.UseShellExecute = False
            _xrayProcess.StartInfo.RedirectStandardOutput = True
            _xrayProcess.StartInfo.RedirectStandardError = True
            _xrayProcess.StartInfo.CreateNoWindow = True
            _xrayProcess.StartInfo.WorkingDirectory = Path.GetDirectoryName(_xrayCorePath)

            AddHandler _xrayProcess.OutputDataReceived, Sub(s, e)
                                                            If Not String.IsNullOrEmpty(e.Data) Then
                                                                ' In TUN modes, skip noisy connection logs to prevent UI freeze
                                                                If (_currentProxyMode = "TUN1" OrElse _currentProxyMode = "TUN2") AndAlso
                                                                   (e.Data.Contains("accepted") OrElse e.Data.Contains(">>")) Then
                                                                    Return
                                                                End If
                                                                Log($"Xray: {e.Data}")
                                                            End If
                                                        End Sub

            AddHandler _xrayProcess.ErrorDataReceived, Sub(s, e)
                                                           If Not String.IsNullOrEmpty(e.Data) Then
                                                               ' In TUN modes, only show actual errors
                                                               If (_currentProxyMode = "TUN1" OrElse _currentProxyMode = "TUN2") AndAlso
                                                                  Not e.Data.Contains("[Error]") AndAlso Not e.Data.Contains("[Warning]") Then
                                                                   Return
                                                               End If
                                                               Log($"Xray: {e.Data}", True)
                                                           End If
                                                       End Sub

            Log("Starting Xray-core process...")
            _xrayProcess.Start()

            If _gamingModeEnabled Then
                Dim prioritySet As Boolean = False
                Try
                    _xrayProcess.PriorityClass = ProcessPriorityClass.High
                    _xrayProcess.PriorityBoostEnabled = True
                    prioritySet = True
                    Log("✓ Xray-core priority set to HIGH for gaming")
                Catch ex As Exception
                    Log($"Could not boost Xray-core priority: {ex.Message}")
                End Try

                If Not prioritySet Then
                    Try
                        Dim procId = _xrayProcess.Id
                        Dim procHandle = OpenProcess(PROCESS_SET_INFORMATION, False, procId)
                        If procHandle <> IntPtr.Zero Then
                            Try
                                SetPriorityClass(procHandle, HIGH_PRIORITY_CLASS)
                                Log("✓ Xray-core priority set to HIGH (legacy method)")
                            Catch ex As Exception
                                Log($"Legacy priority boost failed: {ex.Message}")
                            Finally
                                CloseHandle(procHandle)
                            End Try
                        End If
                    Catch ex As Exception
                        Log($"Could not boost Xray-core priority (legacy): {ex.Message}")
                    End Try
                End If
            End If

            _xrayProcess.BeginOutputReadLine()
            _xrayProcess.BeginErrorReadLine()

            Log("Waiting for Xray-core to initialize...")
            Await Task.Delay(1500)

            If _xrayProcess.HasExited Then
                Log($"Xray-core exited with code: {_xrayProcess.ExitCode}", True)
                Return False
            End If

            Log("Xray-core process started successfully")
            Return True
        Catch ex As Exception
            Log($"Failed to start Xray-core: {ex.Message}", True)
            Throw
        End Try
    End Function

    Private Const INTERNET_OPTION_SETTINGS_CHANGED As Integer = 39
    Private Const INTERNET_OPTION_REFRESH As Integer = 37

    <Runtime.InteropServices.DllImport("wininet.dll", SetLastError:=True)>
    Private Shared Function InternetSetOption(hInternet As IntPtr, dwOption As Integer, lpBuffer As IntPtr, dwBufferLength As Integer) As Boolean
    End Function

    Public Function IsConnected() As Boolean
        If Not _isConnected Then
            Return False
        End If

        If _xrayProcess IsNot Nothing Then
            If _xrayProcess.HasExited Then
                _isConnected = False
            End If
        End If

        Return _isConnected
    End Function

    Public Sub Disconnect()
        Try
            Log("Disconnecting VPN...")

            ' Stop TUN mode if active
            If _tunManager IsNot Nothing AndAlso _tunManager.IsRunning Then
                _tunManager.StopTun()
            End If

            SetSystemProxy(False)

            Try
                If _sshDynamic IsNot Nothing AndAlso _sshDynamic.IsStarted Then
                    _sshDynamic.Stop()
                End If
            Catch
            End Try

            Try
                If _sshClient IsNot Nothing AndAlso _sshClient.IsConnected Then
                    _sshClient.Disconnect()
                End If
            Catch
            End Try

            _sshDynamic = Nothing
            If _sshClient IsNot Nothing Then
                _sshClient.Dispose()
                _sshClient = Nothing
            End If

            StopLocalTlsTunnel()

            If _sshProcess IsNot Nothing AndAlso Not _sshProcess.HasExited Then
                _sshProcess.Kill()
                _sshProcess.WaitForExit(2000)
                _sshProcess.Dispose()
                _sshProcess = Nothing
            End If

            If _xrayProcess IsNot Nothing AndAlso Not _xrayProcess.HasExited Then
                _xrayProcess.Kill()
                _xrayProcess.WaitForExit(2000)
                _xrayProcess.Dispose()
                _xrayProcess = Nothing
            End If

            Dim processes = Process.GetProcessesByName("xray")
            For Each proc In processes
                Try
                    proc.Kill()
                Catch
                End Try
            Next

            _isConnected = False
            _socksPort = 0
            _httpPort = 0
            _tlsLocalPort = 0
            _vpnServerHost = ""
            Log("Disconnection complete")
        Catch ex As Exception
            Log($"Disconnect error: {ex.Message}", True)
            Throw New Exception($"Disconnect failed: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Full cleanup including TUN adapter deletion (call on app exit only)
    ''' </summary>
    Public Sub CleanupAdapter()
        Try
            If _tunManager IsNot Nothing Then
                _tunManager.CleanupAdapter()
            End If
        Catch ex As Exception
            Log($"CleanupAdapter error: {ex.Message}", True)
        End Try
    End Sub

    Public Function GetConnectionStats() As Dictionary(Of String, String)
        Dim stats As New Dictionary(Of String, String)

        Try
            Dim interfaces = NetworkInterface.GetAllNetworkInterfaces()
            Dim totalBytesReceived As Long = 0
            Dim totalBytesSent As Long = 0

            For Each ni In interfaces
                If ni.OperationalStatus = OperationalStatus.Up Then
                    Dim stats2 = ni.GetIPv4Statistics()
                    totalBytesReceived += stats2.BytesReceived
                    totalBytesSent += stats2.BytesSent
                End If
            Next

            _bytesDownloaded = totalBytesReceived
            _bytesUploaded = totalBytesSent

            stats.Add("BytesDownloaded", FormatBytes(totalBytesReceived))
            stats.Add("BytesUploaded", FormatBytes(totalBytesSent))
            stats.Add("Status", If(IsConnected(), "Connected", "Disconnected"))
        Catch ex As Exception
            stats.Add("Error", ex.Message)
        End Try

        Return stats
    End Function

    Private Function FormatBytes(bytes As Long) As String
        Dim sizes As String() = {"B", "KB", "MB", "GB", "TB"}
        Dim len As Double = bytes
        Dim order As Integer = 0

        While len >= 1024 AndAlso order < sizes.Length - 1
            order += 1
            len = len / 1024
        End While

        Return $"{len:0.##} {sizes(order)}"
    End Function

    Public Async Function ConnectWithVLessAsync(config As VLessConfig, Optional proxyMode As String = "System") As Task(Of Boolean)
        Try
            Log($"VLESS Connection: {config.Host}:{config.Port}")
            Log($"UUID: {config.UUID}")
            Log($"Transport: {config.TransportType} | Security: {config.Security}")
            Log($"SNI/Host: {config.SNI}")
            Log($"Path: {config.Path}")

            If Not VLessParser.ValidateConfig(config) Then
                Log("Configuration validation failed", True)
                Throw New InvalidOperationException("Invalid VLESS configuration")
            End If

            If _isConnected Then
                Log("Disconnecting existing connection...")
                Disconnect()
            End If

            _currentConfig = config
            _vpnServerHost = config.Host

            Log("Allocating proxy ports...")
            AllocatePorts()

            Log("Generating Xray configuration (Gaming Mode)...")
            Dim xrayConfig = GenerateXrayConfig(config)

            File.WriteAllText(_configFilePath, xrayConfig)
            Log($"Configuration saved to: {_configFilePath}")

            Log("Generated Xray Config:")
            Log(xrayConfig)

            Log("Starting Xray-core with gaming optimizations...")
            Dim started = Await StartXrayCoreAsync()

            If started Then
                _isConnected = True
                SetSystemProxy(True, proxyMode)
                If proxyMode.StartsWith("TUN", StringComparison.OrdinalIgnoreCase) Then
                    Log($"TUN proxy mode: {proxyMode}")
                Else
                    Log($"System proxy enabled (mode: {proxyMode})")
                End If
                Log($"=== CONNECTION SUCCESSFUL ===")
                Log($"HTTP Proxy: 127.0.0.1:{_httpPort}")
                Log($"SOCKS5 Proxy: 127.0.0.1:{_socksPort}")
                Log("🎮 GAMING MODE: Ultra-low latency optimization active")
                Return True
            Else
                Log("Xray-core failed to start", True)
            End If

            Return False
        Catch ex As Exception
            Log($"VLESS connection error: {ex.Message}", True)
            _isConnected = False
            Throw
        End Try
    End Function

    Private Function GenerateXrayConfig(config As VLessConfig) As String
        Try
            Dim configObj As New Dictionary(Of String, Object) From {
                {"log", New Dictionary(Of String, Object) From {
                    {"loglevel", "error"}
                }},
                {"inbounds", New Object() {
                    New Dictionary(Of String, Object) From {
                        {"tag", "socks"},
                        {"port", _socksPort},
                        {"listen", "127.0.0.1"},
                        {"protocol", "socks"},
                        {"sniffing", New Dictionary(Of String, Object) From {
                            {"enabled", False},
                            {"destOverride", New String() {}},
                            {"routeOnly", False}
                        }},
                        {"settings", New Dictionary(Of String, Object) From {
                            {"auth", "noauth"},
                            {"udp", True},
                            {"ip", "127.0.0.1"},
                            {"userLevel", 0}
                        }},
                        {"streamSettings", New Dictionary(Of String, Object) From {
                            {"sockopt", New Dictionary(Of String, Object) From {
                                {"tcpFastOpen", True},
                                {"tcpNoDelay", True},
                                {"tcpKeepAliveInterval", 5},
                                {"tcpKeepAliveIdle", 10},
                                {"tcpCongestion", "bbr"},
                                {"tcpUserTimeout", 5000},
                                {"mark", 255}
                            }}
                        }}
                    },
                    New Dictionary(Of String, Object) From {
                        {"tag", "http"},
                        {"port", _httpPort},
                        {"listen", "127.0.0.1"},
                        {"protocol", "http"},
                        {"sniffing", New Dictionary(Of String, Object) From {
                            {"enabled", False},
                            {"destOverride", New String() {}},
                            {"routeOnly", False}
                        }},
                        {"streamSettings", New Dictionary(Of String, Object) From {
                            {"sockopt", New Dictionary(Of String, Object) From {
                                {"tcpFastOpen", True},
                                {"tcpNoDelay", True},
                                {"tcpKeepAliveInterval", 5},
                                {"tcpKeepAliveIdle", 10},
                                {"tcpCongestion", "bbr"},
                                {"tcpUserTimeout", 5000},
                                {"mark", 255}
                            }}
                        }}
                    }
                }},
                {"outbounds", New Object() {
                    GenerateOutbound(config),
                    New Dictionary(Of String, Object) From {
                        {"protocol", "freedom"},
                        {"tag", "direct"},
                        {"settings", New Dictionary(Of String, Object) From {
                            {"domainStrategy", "UseIPv4"},
                            {"userLevel", 0}
                        }},
                        {"streamSettings", New Dictionary(Of String, Object) From {
                            {"sockopt", New Dictionary(Of String, Object) From {
                                {"tcpFastOpen", True},
                                {"tcpNoDelay", True},
                                {"tcpKeepAliveIdle", 10},
                                {"tcpKeepAliveInterval", 5},
                                {"tcpCongestion", "bbr"},
                                {"tcpUserTimeout", 5000},
                                {"tcpMaxSeg", 1460},
                                {"mark", 255}
                            }}
                        }}
                    },
                    New Dictionary(Of String, Object) From {
                        {"protocol", "blackhole"},
                        {"tag", "block"}
                    }
                }},
                {"routing", New Dictionary(Of String, Object) From {
                    {"domainStrategy", "AsIs"},
                    {"rules", BuildRoutingRules()}
                }}
            }

            Dim options As New System.Text.Json.JsonSerializerOptions With {
                .WriteIndented = True,
                .Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }

            Return System.Text.Json.JsonSerializer.Serialize(configObj, options)
        Catch ex As Exception
            Throw New Exception($"Config generation failed: {ex.Message}")
        End Try
    End Function

    Private Function BuildRoutingRules() As Object()
        Try
            Dim rules As New List(Of Object)

            ' Always allow private IPs direct
            rules.Add(New Dictionary(Of String, Object) From {
                {"type", "field"},
                {"ip", New String() {"geoip:private"}},
                {"outboundTag", "direct"}
            })

            ' Determine the VPN outbound tag based on connection type
            Dim vpnOutboundTag As String = "proxy" ' Default for VLess
            If TypeOf _currentConfig Is SSHTLSConfig Then
                vpnOutboundTag = "ssh-tunnel"
            End If

            ' === SPLIT TUNNEL MODE: Only specified SNIs use VPN, rest goes DIRECT ===
            If _splitTunnelSettings IsNot Nothing AndAlso _splitTunnelSettings.CustomSNIEnabled AndAlso _splitTunnelSettings.CustomSNIs IsNot Nothing AndAlso _splitTunnelSettings.CustomSNIs.Count > 0 Then
                ' Route custom SNIs through VPN
                Dim sniDomains As New List(Of String)
                For Each sni In _splitTunnelSettings.CustomSNIs
                    Dim n = NormalizeDomain(sni)
                    If Not String.IsNullOrEmpty(n) Then sniDomains.Add("domain:" & n)
                Next
                
                If sniDomains.Count > 0 Then
                    rules.Add(New Dictionary(Of String, Object) From {
                        {"type", "field"},
                        {"domain", sniDomains.Distinct().ToArray()},
                        {"outboundTag", vpnOutboundTag}
                    })
                End If

                ' Route everything else DIRECT (bypass VPN) in split tunnel mode
                rules.Add(New Dictionary(Of String, Object) From {
                    {"type", "field"},
                    {"network", "tcp,udp"},
                    {"outboundTag", "direct"}
                })

                ' Return early - split tunnel mode overrides blocker
                Return rules.ToArray()
            End If

            ' === BLOCKER MODE: Block specified domains, rest goes through VPN ===
            Dim domains As New List(Of String)
            Dim whiteDomains As New List(Of String)

            If _blockerSettings IsNot Nothing Then
                If _blockerSettings.AdsEnabled Then domains.AddRange(GetAdsDomains())
                If _blockerSettings.AdultEnabled Then domains.AddRange(GetAdultDomains())
                If _blockerSettings.SocialEnabled Then domains.AddRange(GetSocialDomains())
                If _blockerSettings.CustomDomains IsNot Nothing Then
                    For Each d In _blockerSettings.CustomDomains
                        Dim n = NormalizeDomain(d)
                        If Not String.IsNullOrEmpty(n) Then domains.Add("domain:" & n)
                    Next
                End If
                If _blockerSettings.WhitelistDomains IsNot Nothing Then
                    For Each d In _blockerSettings.WhitelistDomains
                        Dim n = NormalizeDomain(d)
                        If Not String.IsNullOrEmpty(n) Then whiteDomains.Add("domain:" & n)
                    Next
                End If
            End If

            ' Whitelist: route DIRECT (bypass VPN entirely)
            ' User expectation: whitelist domains should skip VPN and go directly to internet
            ' This allows unblocking falsely blocked sites and bypassing VPN for specific domains
            If whiteDomains.Count > 0 Then
                rules.Add(New Dictionary(Of String, Object) From {
                    {"type", "field"},
                    {"domain", whiteDomains.Distinct().ToArray()},
                    {"outboundTag", "direct"}
                })
            End If

            ' Safety: Always allow core YouTube domains to route through VPN (do not block),
            ' because network-level domain blocking of YouTube endpoints breaks the app and website.
            ' This does NOT remove YouTube ads (uBO cosmetic/scriptlet is required for that),
            ' but prevents functionality breakage while other ad/track domains are blocked.
            Dim ytAllow = GetYouTubeCoreAllowDomains()
            If ytAllow.Length > 0 Then
                rules.Add(New Dictionary(Of String, Object) From {
                    {"type", "field"},
                    {"domain", ytAllow},
                    {"outboundTag", vpnOutboundTag}
                })
            End If

            If domains.Count > 0 Then
                rules.Add(New Dictionary(Of String, Object) From {
                    {"type", "field"},
                    {"domain", domains.Distinct().ToArray()},
                    {"outboundTag", "block"}
                })
            End If

            ' Append external block domains when ads blocking is enabled AND external lists are loaded
            If _blockerSettings IsNot Nothing AndAlso _blockerSettings.AdsEnabled AndAlso _externalBlockDomains IsNot Nothing AndAlso _externalBlockDomains.Count > 0 Then
                ' Filter out YouTube core and user whitelist from external domains to avoid breakage
                Dim ytCore = New HashSet(Of String)(GetYouTubeCoreAllowDomains().Select(Function(s) s.Replace("domain:", "")), StringComparer.OrdinalIgnoreCase)
                Dim userWhite = New HashSet(Of String)((If(_blockerSettings.WhitelistDomains, New List(Of String)())).Select(Function(s) NormalizeDomain(s)), StringComparer.OrdinalIgnoreCase)
                Dim filtered = _externalBlockDomains.Where(Function(d)
                                                               Dim dn = NormalizeDomain(d)
                                                               If String.IsNullOrEmpty(dn) Then Return False
                                                               If ytCore.Contains(dn) Then Return False
                                                               If userWhite.Contains(dn) Then Return False
                                                               Return True
                                                           End Function)
                rules.Add(New Dictionary(Of String, Object) From {
                    {"type", "field"},
                    {"domain", filtered.Select(Function(d) "domain:" & NormalizeDomain(d)).ToArray()},
                    {"outboundTag", "block"}
                })
            End If

            Return rules.ToArray()
        Catch
            ' Fallback minimal rule
            Return New Object() {
                New Dictionary(Of String, Object) From {
                    {"type", "field"},
                    {"ip", New String() {"geoip:private"}},
                    {"outboundTag", "direct"}
                }
            }
        End Try
    End Function

    Private Function NormalizeDomain(input As String) As String
        If String.IsNullOrWhiteSpace(input) Then Return String.Empty
        Dim s = input.Trim().ToLower()
        If s.StartsWith("http://") OrElse s.StartsWith("https://") Then
            Try
                Dim u = New Uri(s)
                s = u.Host
            Catch
            End Try
        End If
        If s.StartsWith("*") Then s = s.TrimStart("*"c, "."c)
        s = s.Trim("."c)
        Return s
    End Function

    Private Function GetAdsDomains() As IEnumerable(Of String)
        ' Best-effort core ad/tracker domains (avoid breaking YouTube core)
        Return New String() {
            "domain:doubleclick.net",
            "domain:g.doubleclick.net",
            "domain:partnerad.l.doubleclick.net",
            "domain:stats.g.doubleclick.net",
            "domain:static.doubleclick.net",
            "domain:googleadservices.com",
            "domain:google-analytics.com",
            "domain:ssl.google-analytics.com",
            "domain:googletagservices.com",
            "domain:googletagmanager.com",
            "domain:googlesyndication.com",
            "domain:tpc.googlesyndication.com",
            "domain:pagead2.googlesyndication.com",
            "domain:adservice.google.com",
            "domain:adservice.google.lk",
            "domain:adservice.google.co.in",
            "domain:adservice.google.co.uk",
            "domain:adroll.com",
            "domain:criteo.com",
            "domain:rubiconproject.com",
            "domain:pubmatic.com",
            "domain:openx.net",
            "domain:adsrvr.org",
            "domain:serving-sys.com",
            "domain:mathtag.com",
            "domain:rlcdn.com",
            "domain:rfihub.com",
            "domain:smartadserver.com",
            "domain:adnxs.com",
            "domain:spotxchange.com",
            "domain:yieldmo.com",
            "domain:taboola.com",
            "domain:outbrain.com",
            "domain:doubleverify.com",
            "domain:moatads.com",
            "domain:quantserve.com",
            "domain:scorecardresearch.com",
            "domain:trustx.org",
            "domain:crsspxl.com"
        }
    End Function

    Private Function GetAdultDomains() As IEnumerable(Of String)
        Return New String() {
            "domain:pornhub.com",
            "domain:xvideos.com",
            "domain:xhamster.com",
            "domain:redtube.com",
            "domain:xnxx.com",
            "domain:youporn.com",
            "domain:spankbang.com",
            "domain:youjizz.com",
            "domain:rule34.xxx",
            "domain:nhentai.net",
            "domain:trafficjunky.net",
            "domain:juicyads.com",
            "domain:exoclick.com",
            "domain:exosrv.com",
            "domain:exdynsrv.com",
            "domain:plugrush.com",
            "domain:adnium.com",
            "domain:trafficfactory.biz",
            "domain:popads.net",
            "domain:hilltopads.net",
            "domain:realsrv.com",
            "domain:adskeeper.com"
        }
    End Function

    Private Function GetSocialDomains() As IEnumerable(Of String)
        Return New String() {
            "domain:facebook.com",
            "domain:fb.com",
            "domain:instagram.com",
            "domain:whatsapp.com",
            "domain:tiktok.com",
            "domain:twitter.com",
            "domain:x.com",
            "domain:snapchat.com",
            "domain:youtube.com",
            "domain:ytimg.com",
            "domain:googlevideo.com"
        }
    End Function

    Private Function GetYouTubeCoreAllowDomains() As String()
        ' Domains required for YouTube website and mobile app functionality
        Return New String() {
            "domain:youtube.com",
            "domain:youtu.be",
            "domain:ytimg.com",
            "domain:i.ytimg.com",
            "domain:yt3.ggpht.com",
            "domain:googlevideo.com",
            "domain:youtubei.googleapis.com",
            "domain:s.youtube.com"
        }
    End Function

    Private Function GenerateOutbound(config As VLessConfig) As Dictionary(Of String, Object)
        Dim streamSettings As New Dictionary(Of String, Object)

        Dim wsHost As String = If(String.IsNullOrEmpty(config.SNI), config.Host, config.SNI)

        Select Case config.TransportType?.ToLower()
            Case "ws", "websocket"
                streamSettings.Add("network", "ws")
                streamSettings.Add("wsSettings", New Dictionary(Of String, Object) From {
                    {"path", If(String.IsNullOrEmpty(config.Path), "/", config.Path)},
                    {"headers", New Dictionary(Of String, Object) From {
                        {"Host", wsHost}
                    }},
                    {"maxEarlyData", 4096},
                    {"earlyDataHeaderName", "Sec-WebSocket-Protocol"}
                })
            Case "grpc"
                streamSettings.Add("network", "grpc")
                streamSettings.Add("grpcSettings", New Dictionary(Of String, Object) From {
                    {"serviceName", If(String.IsNullOrEmpty(config.Path), "", config.Path)},
                    {"multiMode", True},
                    {"idle_timeout", 300},
                    {"health_check_timeout", 20},
                    {"permit_without_stream", False},
                    {"initial_windows_size", 1048576}
                })
            Case "tcp"
                streamSettings.Add("network", "tcp")
                streamSettings.Add("tcpSettings", New Dictionary(Of String, Object) From {
                    {"header", New Dictionary(Of String, Object) From {
                        {"type", "none"}
                    }}
                })
            Case Else
                streamSettings.Add("network", "tcp")
        End Select

        Dim tlsServerName As String = If(String.IsNullOrEmpty(config.SNI), config.Host, config.SNI)

        Select Case config.Security?.ToLower()
            Case "tls"
                streamSettings.Add("security", "tls")
                streamSettings.Add("tlsSettings", New Dictionary(Of String, Object) From {
                    {"serverName", tlsServerName},
                    {"allowInsecure", True},
                    {"fingerprint", "chrome"},
                    {"alpn", New String() {"h2", "http/1.1"}},
                    {"disableSystemRoot", False},
                    {"enableSessionResumption", True}
                })
            Case "reality"
                streamSettings.Add("security", "reality")
                streamSettings.Add("realitySettings", New Dictionary(Of String, Object) From {
                    {"serverName", tlsServerName},
                    {"fingerprint", "chrome"},
                    {"show", False}
                })
            Case Else
                streamSettings.Add("security", "none")
        End Select

        streamSettings.Add("sockopt", New Dictionary(Of String, Object) From {
            {"mark", 255},
            {"tcpFastOpen", True},
            {"tcpNoDelay", True},
            {"tcpKeepAliveIdle", 10},
            {"tcpKeepAliveInterval", 5},
            {"tcpCongestion", "bbr"},
            {"tcpUserTimeout", 5000},
            {"tcpMaxSeg", 1460},
            {"tproxy", "off"}
        })

        Return New Dictionary(Of String, Object) From {
            {"protocol", "vless"},
            {"tag", "proxy"},
            {"settings", New Dictionary(Of String, Object) From {
                {"vnext", New Object() {
                    New Dictionary(Of String, Object) From {
                        {"address", config.Host},
                        {"port", config.Port},
                        {"users", New Object() {
                            New Dictionary(Of String, Object) From {
                                {"id", config.UUID},
                                {"encryption", "none"},
                                {"flow", ""},
                                {"level", 0}
                            }
                        }}
                    }
                }}
            }},
            {"streamSettings", streamSettings},
            {"mux", New Dictionary(Of String, Object) From {
                {"enabled", False},
                {"concurrency", -1}
            }}
        }
    End Function

    Private Function IsRawSSHServer(host As String, port As Integer) As Boolean
        Try
            Using client As New TcpClient()
                Dim connectTask = client.ConnectAsync(host, port)
                If Not connectTask.Wait(TimeSpan.FromSeconds(5)) Then
                    Log($"SSH probe timeout: {host}:{port}", True)
                    Return False
                End If
                If Not client.Connected Then
                    Return False
                End If
                client.ReceiveTimeout = 3000
                Dim stream = client.GetStream()
                Dim buffer = New Byte(256 - 1) {}
                Dim readTotal As Integer = 0
                Dim sw As New Stopwatch()
                sw.Start()

                While readTotal < buffer.Length AndAlso sw.ElapsedMilliseconds < 3000
                    If stream.DataAvailable Then
                        Dim read = stream.Read(buffer, readTotal, buffer.Length - readTotal)
                        If read <= 0 Then Exit While
                        readTotal += read
                        Dim txt = Encoding.ASCII.GetString(buffer, 0, readTotal)
                        Dim lineEnd = txt.IndexOfAny(New Char() {ControlChars.Cr, ControlChars.Lf})
                        If lineEnd >= 0 Then
                            txt = txt.Substring(0, lineEnd)
                            If txt.StartsWith("SSH-") Then
                                Log($"SSH probe: raw banner detected '{txt}'")
                                Return True
                            Else
                                Log($"SSH probe: non-SSH initial data: '{txt}'")
                                Return False
                            End If
                        End If
                    Else
                        Thread.Sleep(50)
                    End If
                End While

                If readTotal > 3 Then
                    Dim txt = Encoding.ASCII.GetString(buffer, 0, readTotal)
                    If txt.StartsWith("SSH-") Then
                        Log($"SSH probe: raw banner (partial) detected '{txt}'")
                        Return True
                    End If
                End If

                Log("SSH probe: no SSH banner received")
                Return False
            End Using
        Catch ex As Exception
            Log($"SSH probe error: {ex.Message}")
            Return False
        End Try
    End Function
End Class