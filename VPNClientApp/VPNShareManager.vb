Imports System.Net
Imports System.Net.Sockets
Imports System.Threading
Imports System.IO
Imports System.Diagnostics

Public Class VPNShareManager
    Private _httpListener As TcpListener
    Private _socksListener As TcpListener
    Private _isRunning As Boolean = False
    Private _cts As CancellationTokenSource
    Private _activeConnections As Integer = 0
    Private _totalConnections As Integer = 0
    Private _firewallRuleNames As New List(Of String)()
    Private _acceptTasks As New List(Of Task)() ' Track accept loops
    Private _connectionTasks As New List(Of Task)() ' Track active connection relay tasks
    
    ' URL Filtering
    Private _urlFilterEnabled As Boolean = False
    Private _allowedUrls As New List(Of String)()
    Private ReadOnly _filterLock As New Object()
    Private _localHttpPort As Integer = 0 ' Track the local HTTP port for filtering

    Public Event LogMessage(message As String, isError As Boolean)
    Public Event StatusChanged(isRunning As Boolean, activeConnections As Integer, totalConnections As Integer)

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _isRunning
        End Get
    End Property

    Public Async Function StartAsync(listenHost As String, publicHttpPort As Integer, publicSocksPort As Integer, shareHttp As Boolean, shareSocks As Boolean, localHttpPort As Integer, localSocksPort As Integer) As Task(Of Boolean)
        Try
            If _isRunning Then
                Log("Share VPN already running", True)
                Return False
            End If

            _cts = New CancellationTokenSource()
            _isRunning = True
            _activeConnections = 0
            _totalConnections = 0
            _acceptTasks.Clear()
            _connectionTasks.Clear()
            _localHttpPort = localHttpPort ' Store for filtering

            Dim listenAddr = IPAddress.Parse(listenHost)

            If shareHttp Then
                _httpListener = New TcpListener(listenAddr, publicHttpPort)
                _httpListener.Start()
                Log($"HTTP share listening on {listenHost}:{publicHttpPort} -> 127.0.0.1:{localHttpPort}")
                EnsureFirewallRule(publicHttpPort, "TCP", $"VPNClientApp_HTTPShare_{publicHttpPort}")
                _acceptTasks.Add(Task.Run(Function() AcceptLoopAsync(_httpListener, localHttpPort, False, _cts.Token)))
            End If

            If shareSocks Then
                _socksListener = New TcpListener(listenAddr, publicSocksPort)
                _socksListener.Start()
                Log($"SOCKS share listening on {listenHost}:{publicSocksPort} -> 127.0.0.1:{localSocksPort}")
                EnsureFirewallRule(publicSocksPort, "TCP", $"VPNClientApp_SOCKSShare_{publicSocksPort}")
                ' Also allow UDP for potential SOCKS UDP associate (some clients)
                EnsureFirewallRule(publicSocksPort, "UDP", $"VPNClientApp_SOCKSShare_UDP_{publicSocksPort}")
                _acceptTasks.Add(Task.Run(Function() AcceptLoopAsync(_socksListener, localSocksPort, True, _cts.Token)))
            End If

            RaiseEvent StatusChanged(_isRunning, _activeConnections, _totalConnections)

            ' Yield once so method is truly async (removes BC42356 warning) then return success
            Await Task.Yield()
            Return True
        Catch ex As Exception
            Log($"Failed to start sharing: {ex.Message}", True)
            StopShare()
            Return False
        End Try
    End Function

    Private Async Function AcceptLoopAsync(listener As TcpListener, forwardPort As Integer, isSocks As Boolean, token As CancellationToken) As Task
        While Not token.IsCancellationRequested AndAlso _isRunning
            Try
                Dim client = Await listener.AcceptTcpClientAsync()
                Threading.Interlocked.Increment(_totalConnections)
                Threading.Interlocked.Increment(_activeConnections)
                RaiseEvent StatusChanged(_isRunning, _activeConnections, _totalConnections)
                Try
                    Dim remote = CType(client.Client.RemoteEndPoint, IPEndPoint)
                    Log($"Incoming {(If(isSocks, "SOCKS", "HTTP"))} client {remote.Address}:{remote.Port}")
                Catch
                End Try

                ' Start connection handling and track the task
                Dim t = HandleConnectionAsync(client, forwardPort, token)
                SyncLock _connectionTasks
                    _connectionTasks.Add(t)
                End SyncLock
            Catch ex As ObjectDisposedException
                Exit While
            Catch ex As Exception
                If Not token.IsCancellationRequested Then
                    Log($"Accept error: {ex.Message}", True)
                End If
            End Try
        End While
    End Function

    Private Async Function HandleConnectionAsync(client As TcpClient, forwardPort As Integer, token As CancellationToken) As Task
        Try
            Using client
                client.NoDelay = True
                Try
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, True)
                Catch
                End Try
                client.ReceiveBufferSize = 65536
                client.SendBufferSize = 65536
                
                ' URL filtering for HTTP proxy connections only
                If _urlFilterEnabled AndAlso forwardPort = _localHttpPort Then
                    Dim filterResult = Await CheckAndFilterHttpConnectionAsync(client, token)
                    If Not filterResult.Allowed Then
                        ' Send HTTP 403 Forbidden response
                        Try
                            Using stream = client.GetStream()
                                Dim response = Text.Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden" & vbCrLf & 
                                                                            "Content-Type: text/plain" & vbCrLf & 
                                                                            "Connection: close" & vbCrLf & vbCrLf & 
                                                                            "Access to this URL is blocked by URL filter.")
                                Await stream.WriteAsync(response, 0, response.Length, token)
                            End Using
                        Catch
                        End Try
                        Log($"Blocked connection to: {filterResult.Host}")
                        Return
                    End If
                    
                    ' Connection allowed - forward the buffered request data
                    Using upstream As New TcpClient()
                        upstream.NoDelay = True
                        Try
                            upstream.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, True)
                        Catch
                        End Try
                        upstream.ReceiveBufferSize = 65536
                        upstream.SendBufferSize = 65536
                        Await upstream.ConnectAsync("127.0.0.1", forwardPort)

                        Using inStream = client.GetStream()
                            Using outStream = upstream.GetStream()
                                ' First write the buffered HTTP request data
                                If filterResult.BufferedData IsNot Nothing AndAlso filterResult.BufferedData.Length > 0 Then
                                    Await outStream.WriteAsync(filterResult.BufferedData, 0, filterResult.BufferedData.Length, token)
                                End If
                                
                                ' Then relay remaining data
                                Dim t1 = RelayAsync(inStream, outStream, token)
                                Dim t2 = RelayAsync(outStream, inStream, token)
                                Await Task.WhenAny(t1, t2)
                            End Using
                        End Using
                    End Using
                Else
                    ' No filtering - direct relay
                    Using upstream As New TcpClient()
                        upstream.NoDelay = True
                        Try
                            upstream.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, True)
                        Catch
                        End Try
                        upstream.ReceiveBufferSize = 65536
                        upstream.SendBufferSize = 65536
                        Await upstream.ConnectAsync("127.0.0.1", forwardPort)

                        Using inStream = client.GetStream()
                            Using outStream = upstream.GetStream()
                                Dim t1 = RelayAsync(inStream, outStream, token)
                                Dim t2 = RelayAsync(outStream, inStream, token)
                                Await Task.WhenAny(t1, t2)
                            End Using
                        End Using
                    End Using
                End If
            End Using
        Catch ex As Exception
            If Not token.IsCancellationRequested Then
                Log($"Forward error: {ex.Message}")
            End If
        Finally
            Threading.Interlocked.Decrement(_activeConnections)
            RaiseEvent StatusChanged(_isRunning, _activeConnections, _totalConnections)
        End Try
    End Function

    Private Class FilterResult
        Public Property Allowed As Boolean
        Public Property Host As String
        Public Property BufferedData As Byte()
    End Class

    ''' <summary>
    ''' Check if HTTP connection is allowed based on URL filter
    ''' </summary>
    Private Async Function CheckAndFilterHttpConnectionAsync(client As TcpClient, token As CancellationToken) As Task(Of FilterResult)
        Dim result As New FilterResult With {.Allowed = False, .Host = "unknown"}
        
        Try
            Dim stream = client.GetStream()
            ' Read the HTTP request
            Dim buffer(8191) As Byte
            stream.ReadTimeout = 5000
            
            Dim bytesRead = Await stream.ReadAsync(buffer, 0, buffer.Length, token)
            If bytesRead = 0 Then
                Log("Filter: No data received")
                Return result
            End If
            
            ' Store the buffered data to forward later if allowed
            ReDim result.BufferedData(bytesRead - 1)
            Array.Copy(buffer, result.BufferedData, bytesRead)
            
            Dim request = Text.Encoding.ASCII.GetString(buffer, 0, bytesRead)
            Dim lines = request.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
            
            If lines.Length > 0 Then
                Dim firstLine = lines(0)
                Dim host As String = Nothing
                
                ' Parse CONNECT method (for HTTPS)
                If firstLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase) Then
                    Dim parts = firstLine.Split(" "c)
                    If parts.Length >= 2 Then
                        host = parts(1).Split(":"c)(0) ' Remove port
                    End If
                ' Parse GET/POST method (for HTTP)
                ElseIf firstLine.Contains("HTTP/") Then
                    ' Look for Host header
                    For Each line In lines
                        If line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase) Then
                            host = line.Substring(5).Trim().Split(":"c)(0) ' Remove port
                            Exit For
                        End If
                    Next
                End If
                
                If Not String.IsNullOrEmpty(host) Then
                    result.Host = host
                    
                    SyncLock _filterLock
                        If _allowedUrls.Count = 0 Then
                            ' No URLs configured - block all
                            Log($"Filter: No allowed URLs configured, blocking {host}")
                            Return result
                        End If
                        
                        ' Check if host matches any allowed URL
                        For Each allowedUrl In _allowedUrls
                            If host.Equals(allowedUrl, StringComparison.OrdinalIgnoreCase) OrElse 
                               host.EndsWith("." & allowedUrl, StringComparison.OrdinalIgnoreCase) Then
                                result.Allowed = True
                                Log($"Allowed URL: {host} (matched: {allowedUrl})")
                                Return result
                            End If
                        Next
                    End SyncLock
                    
                    ' Not in allowed list
                    Log($"Blocked URL: {host} (not in allowed list)")
                    Return result
                End If
            End If
            
            ' If we can't determine host, block by default when filter is enabled
            Log("Filter: Could not determine host, blocking")
            Return result
        Catch ex As Exception
            Log($"URL filter check error: {ex.Message}", True)
            Return result
        End Try
    End Function

    ''' <summary>
    ''' Set URL filter for shared VPN connections
    ''' </summary>
    Public Sub SetUrlFilter(allowedUrls As List(Of String), enabled As Boolean)
        SyncLock _filterLock
            _urlFilterEnabled = enabled
            _allowedUrls.Clear()
            If enabled Then
                _allowedUrls.AddRange(allowedUrls)
                Log($"URL filter enabled with {allowedUrls.Count} allowed URL(s)")
            Else
                Log("URL filter disabled")
            End If
        End SyncLock
    End Sub

    Private Async Function RelayAsync(src As Stream, dst As Stream, token As CancellationToken) As Task
        Dim buffer(65535) As Byte
        Try
            While Not token.IsCancellationRequested
                Dim read = Await src.ReadAsync(buffer, 0, buffer.Length, token)
                If read = 0 Then Exit While
                Await dst.WriteAsync(buffer, 0, read, token)
                ' Flushing each write can hurt performance; let stream coalesce when possible
            End While
        Catch
        End Try
    End Function

    Public Sub StopShare()
        Try
            _isRunning = False
            If _cts IsNot Nothing Then _cts.Cancel()
            If _httpListener IsNot Nothing Then _httpListener.Stop()
            If _socksListener IsNot Nothing Then _socksListener.Stop()
            _httpListener = Nothing
            _socksListener = Nothing
            _cts = Nothing
            _activeConnections = 0
            ' Best-effort wait for accept loops to finish
            Try
                Task.WaitAll(_acceptTasks.ToArray(), 500)
            Catch
            End Try
            ' Connection tasks may still be relaying; give brief time then continue
            Try
                Task.WaitAll(_connectionTasks.ToArray(), 1000)
            Catch
            End Try
            RaiseEvent StatusChanged(_isRunning, _activeConnections, _totalConnections)
            Log("Share VPN stopped")

            ' Try remove firewall rules we created
            For Each rule In _firewallRuleNames.ToList()
                Try
                    RemoveFirewallRule(rule)
                Catch
                End Try
            Next
            _firewallRuleNames.Clear()
        Catch ex As Exception
            Log($"Stop share error: {ex.Message}", True)
        End Try
    End Sub

    Private Sub Log(message As String, Optional isError As Boolean = False)
        RaiseEvent LogMessage(message, isError)
    End Sub

    Private Sub EnsureFirewallRule(port As Integer, protocol As String, ruleName As String)
        Try
            Dim psi As New ProcessStartInfo()
            psi.FileName = "netsh"
            psi.Arguments = $"advfirewall firewall add rule name=""{ruleName}"" dir=in action=allow protocol={protocol} localport={port} profile=any"
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True

            Using p = Process.Start(psi)
                p.WaitForExit(3000)
                Dim exitCode = p.ExitCode
                If exitCode = 0 Then
                    _firewallRuleNames.Add(ruleName)
                    Log($"Firewall rule added for port {port}")
                Else
                    Log($"Firewall rule add may have failed (code {exitCode}). If clients can't connect, allow inbound on port {port} in Windows Firewall.", True)
                End If
            End Using
        Catch ex As Exception
            Log($"Firewall rule add failed: {ex.Message}. If clients can't connect, allow inbound on port {port} in Windows Firewall.", True)
        End Try
    End Sub

    Private Sub RemoveFirewallRule(ruleName As String)
        Try
            Dim psi As New ProcessStartInfo()
            psi.FileName = "netsh"
            psi.Arguments = $"advfirewall firewall delete rule name=""{ruleName}"""
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True

            Using p = Process.Start(psi)
                p.WaitForExit(3000)
            End Using
        Catch
        End Try
    End Sub
End Class
