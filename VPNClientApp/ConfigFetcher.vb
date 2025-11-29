Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.IO

Public Class ConfigFetcher
    Private Const FIREBASE_URL As String = "https://vpnisuru.web.app"
    Private _httpClient As HttpClient

    Public Sub New()
        _httpClient = New HttpClient()
        _httpClient.Timeout = New TimeSpan(0, 0, 30)
    End Sub

    ''' <summary>
    ''' Fetch configurations from online source - returns both VLESS and SSH configs
    ''' </summary>
    Public Async Function FetchConfigurationsAsync(Optional forceRefresh As Boolean = False) As Task(Of ConfigurationResult)
        Try
            AddLog("Fetching configurations from " & FIREBASE_URL)
            Dim response = Await _httpClient.GetAsync(FIREBASE_URL)

            If response.IsSuccessStatusCode Then
                Dim htmlContent = Await response.Content.ReadAsStringAsync()
                AddLog($"Successfully fetched HTML content ({htmlContent.Length} characters)")

                Dim vlessLinks = ExtractVLessLinks(htmlContent)
                Dim vmessLinks = ExtractVMESSLinks(htmlContent)
                Dim sshConfigs = ExtractSSHConfigs(htmlContent)

                ' Combine VLESS and VMESS links
                vlessLinks.AddRange(vmessLinks)

                AddLog($"Extracted {vlessLinks.Count} VLESS/VMESS and {sshConfigs.Count} SSH configuration(s)")

                If vlessLinks.Count > 0 OrElse sshConfigs.Count > 0 Then
                    ' Save to local cache for offline use
                    SaveConfigCache(htmlContent)
                    AddLog("[OK] Configurations cached locally")
                    Return New ConfigurationResult With {
                        .VLessLinks = vlessLinks,
                        .SSHConfigs = sshConfigs
                    }
                Else
                    AddLog("[WARNING] No configurations found in HTML content", True)

                    ' If forcing refresh and no links found online, don't fall back to cache
                    If forceRefresh Then
                        Throw New Exception("No configurations found on server")
                    End If

                    ' Otherwise try cache
                    Return LoadConfigCache()
                End If
            Else
                AddLog($"HTTP request failed with status: {response.StatusCode}", True)

                ' If forcing refresh, throw error instead of using cache
                If forceRefresh Then
                    Throw New Exception($"Failed to fetch configurations. Status: {response.StatusCode}")
                End If

                ' Try to load from local cache
                Dim cachedResult = LoadConfigCache()
                If cachedResult.VLessLinks.Count > 0 OrElse cachedResult.SSHConfigs.Count > 0 Then
                    AddLog($"Using {cachedResult.VLessLinks.Count} VLESS and {cachedResult.SSHConfigs.Count} SSH cached configuration(s)")
                    Return cachedResult
                End If

                Throw New Exception($"Failed to fetch configurations. Status: {response.StatusCode}")
            End If
        Catch ex As HttpRequestException
            AddLog($"Network error: {ex.Message}", True)

            ' If forcing refresh, don't use cache
            If forceRefresh Then
                Throw New Exception($"Network error: {ex.Message}")
            End If

            ' Load from cache on network error
            Dim cachedResult = LoadConfigCache()
            If cachedResult.VLessLinks.Count > 0 OrElse cachedResult.SSHConfigs.Count > 0 Then
                AddLog($"Network unavailable - Using cached configurations")
                Return cachedResult
            End If

            Throw New Exception($"Network error and no cached configurations available: {ex.Message}")
        Catch ex As Exception
            AddLog($"Config fetch error: {ex.Message}", True)

            ' If forcing refresh, don't use cache
            If forceRefresh Then
                Throw
            End If

            ' Load from cache on error
            Dim cachedResult = LoadConfigCache()
            If cachedResult.VLessLinks.Count > 0 OrElse cachedResult.SSHConfigs.Count > 0 Then
                AddLog($"Error occurred - Using cached configurations")
                Return cachedResult
            End If

            Throw New Exception($"Config fetch error: {ex.Message}")
        End Try
    End Function

    ''' <summary>
    ''' Extract VLESS links from HTML content
    ''' </summary>
    Private Function ExtractVLessLinks(htmlContent As String) As List(Of String)
        Dim vlessLinks As New List(Of String)

        ' Pattern to match VLESS links in HTML
        ' Matches: vless://UUID@host:port?params#tag
        Dim pattern As String = "vless://[a-fA-F0-9\-]+@[^""<>\s]+(?:\?[^""<>\s#]*)?(?:#[^""<>\s]*)?"

        Dim matches = Regex.Matches(htmlContent, pattern, RegexOptions.IgnoreCase)

        For Each match As Match In matches
            Dim link = match.Value.Trim()
            If Not String.IsNullOrWhiteSpace(link) AndAlso Not vlessLinks.Contains(link) Then
                vlessLinks.Add(link)
                AddLog($"Found VLESS: {If(link.Length > 80, link.Substring(0, 80) & "...", link)}")
            End If
        Next

        Return vlessLinks
    End Function

    ''' <summary>
    ''' Extract VMESS links from HTML content
    ''' </summary>
    Private Function ExtractVMESSLinks(htmlContent As String) As List(Of String)
        Dim vmessLinks As New List(Of String)

        ' Pattern to match VMESS links in HTML (base64 encoded)
        Dim pattern As String = "vmess://[A-Za-z0-9+/=]+"

        Dim matches = Regex.Matches(htmlContent, pattern, RegexOptions.IgnoreCase)

        For Each match As Match In matches
            Dim link = match.Value.Trim()
            If Not String.IsNullOrWhiteSpace(link) AndAlso Not vmessLinks.Contains(link) Then
                vmessLinks.Add(link)
                AddLog($"Found VMESS: {If(link.Length > 80, link.Substring(0, 80) & "...", link)}")
            End If
        Next

        Return vmessLinks
    End Function

    ''' <summary>
    ''' Extract SSH configurations from HTML content - Enhanced for 3x-ui panels
    ''' Supports multiple formats:
    ''' Format 1: host=xxx , port=xxx , username=xxx , password=xxx [, sni=xxx]
    ''' Format 2: ssh://username:password@host:port[?sni=xxx]
    ''' Format 3: JSON format from 3x-ui panels
    ''' Example: host=sggs7.hostip.co , port=443 , username=fastssh.com-isuru907 , password=isuru , sni=cloudflare.com
    ''' </summary>
    Private Function ExtractSSHConfigs(htmlContent As String) As List(Of SSHTLSConfig)
        Dim sshConfigs As New List(Of SSHTLSConfig)

        AddLog($"Searching for SSH configs in HTML content (length: {htmlContent.Length})...")

        ' Pattern 1: With optional SNI field
        ' Matches: host=xxx , port=xxx , username=xxx , password=xxx , sni=xxx
        Dim patternWithSNI As String = "host\s*=\s*([a-zA-Z0-9\.\-_]+)\s*,\s*port\s*=\s*(\d+)\s*,\s*username\s*=\s*([a-zA-Z0-9\.\-_@]+)\s*,\s*password\s*=\s*([a-zA-Z0-9\.\-_@!#$%^&*]+)\s*,\s*sni\s*=\s*([a-zA-Z0-9\.\-_]+)"
        
        ' Pattern 2: Without SNI field
        Dim patternNoSNI As String = "host\s*=\s*([a-zA-Z0-9\.\-_]+)\s*,\s*port\s*=\s*(\d+)\s*,\s*username\s*=\s*([a-zA-Z0-9\.\-_@]+)\s*,\s*password\s*=\s*([a-zA-Z0-9\.\-_@!#$%^&*]+)"

        ' Try pattern with SNI first
        Dim matches = Regex.Matches(htmlContent, patternWithSNI, RegexOptions.IgnoreCase)
        Dim hasSNI As Boolean = False
        
        If matches.Count > 0 Then
            AddLog($"Pattern with SNI matched {matches.Count} SSH config(s)")
            hasSNI = True
        Else
            ' Try pattern without SNI
            matches = Regex.Matches(htmlContent, patternNoSNI, RegexOptions.IgnoreCase)
            AddLog($"Pattern without SNI matched {matches.Count} SSH config(s)")
        End If

        ' If primary patterns fail, try fallback
        If matches.Count = 0 Then
            AddLog("Trying fallback pattern for SSH configs...")
            ' More permissive pattern - captures everything except commas and angle brackets
            Dim fallbackPattern = "host\s*=\s*([^,<>]+?)\s*,\s*port\s*=\s*(\d+)\s*,\s*username\s*=\s*([^,<>]+?)\s*,\s*password\s*=\s*([^,<>]+?)(?:\s*,\s*sni\s*=\s*([^,<>]+?))?(?:\s|,|<|$)"
            matches = Regex.Matches(htmlContent, fallbackPattern, RegexOptions.IgnoreCase)
            AddLog($"Fallback pattern matched {matches.Count} SSH config(s)")
            hasSNI = False ' Fallback pattern has optional SNI in group 5
        End If

        For Each match As Match In matches
            Try
                Dim host = match.Groups(1).Value.Trim()
                Dim portStr = match.Groups(2).Value.Trim()
                Dim username = match.Groups(3).Value.Trim()
                Dim password = match.Groups(4).Value.Trim()
                Dim sni As String = ""
                
                ' Extract SNI if present (group 5 for both patterns)
                If match.Groups.Count > 5 AndAlso Not String.IsNullOrWhiteSpace(match.Groups(5).Value) Then
                    sni = match.Groups(5).Value.Trim()
                    AddLog($"Found SSH match - Host: {host}, Port: {portStr}, Username: {username}, Password: ***, SNI: {sni}")
                Else
                    AddLog($"Found SSH match - Host: {host}, Port: {portStr}, Username: {username}, Password: ***, SNI: (none)")
                End If

                Dim port As Integer
                If Integer.TryParse(portStr, port) AndAlso port > 0 AndAlso port <= 65535 Then
                    Dim sshConfig As New SSHTLSConfig With {
                        .Host = host,
                        .Port = port,
                        .Username = username,
                        .Password = password,
                        .Tag = $"Online SSH - {host}:{port}",
                        .SNI = sni,
                        .LocalPort = 0,
                        .IsOnlineConfig = True,
                        .UseTLS = True
                    }

                    sshConfigs.Add(sshConfig)
                    AddLog($"[OK] Successfully parsed SSH: {host}:{port} - {username}" & If(String.IsNullOrWhiteSpace(sni), "", $" (SNI: {sni})"))
                Else
                    AddLog($"[ERROR] Invalid port number: {portStr}", True)
                End If
            Catch ex As Exception
                AddLog($"[ERROR] Failed to parse SSH config: {ex.Message}", True)
            End Try
        Next

        ' Try alternative SSH URL format: ssh://username:password@host:port?sni=xxx
        Dim sshUrlPattern As String = "ssh://([^:@]+):([^@]+)@([^:]+):(\d+)(?:\?sni=([^&\s<>]+))?"
        Dim sshUrlMatches = Regex.Matches(htmlContent, sshUrlPattern, RegexOptions.IgnoreCase)
        
        For Each match As Match In sshUrlMatches
            Try
                Dim username = match.Groups(1).Value.Trim()
                Dim password = match.Groups(2).Value.Trim()
                Dim host = match.Groups(3).Value.Trim()
                Dim portStr = match.Groups(4).Value.Trim()
                Dim sni = If(match.Groups.Count > 5, match.Groups(5).Value.Trim(), "")
                
                Dim port As Integer
                If Integer.TryParse(portStr, port) AndAlso port > 0 AndAlso port <= 65535 Then
                    Dim sshConfig As New SSHTLSConfig With {
                        .Host = host,
                        .Port = port,
                        .Username = username,
                        .Password = password,
                        .Tag = $"SSH URL - {host}:{port}",
                        .SNI = sni,
                        .LocalPort = 0,
                        .IsOnlineConfig = True,
                        .UseTLS = True
                    }
                    
                    ' Avoid duplicates
                    If Not sshConfigs.Any(Function(c) c.Host = host AndAlso c.Port = port AndAlso c.Username = username) Then
                        sshConfigs.Add(sshConfig)
                        AddLog($"[OK] Successfully parsed SSH URL: {host}:{port}")
                    End If
                End If
            Catch ex As Exception
                AddLog($"[ERROR] Failed to parse SSH URL: {ex.Message}", True)
            End Try
        Next

        ' Try JSON format from 3x-ui panels
        Try
            Dim jsonPattern = "\{[\s\S]*?""type""\s*:\s*""ssh[\s\S]*?\}"
            Dim jsonMatches = Regex.Matches(htmlContent, jsonPattern, RegexOptions.IgnoreCase)
            
            For Each match As Match In jsonMatches
                Try
                    Dim jsonText = match.Value
                    ' Simple JSON parsing for SSH configs
                    Dim hostMatch = Regex.Match(jsonText, """host""\s*:\s*""([^""]+)""")
                    Dim portMatch = Regex.Match(jsonText, """port""\s*:\s*(\d+)")
                    Dim userMatch = Regex.Match(jsonText, """username""\s*:\s*""([^""]+)""")
                    Dim passMatch = Regex.Match(jsonText, """password""\s*:\s*""([^""]+)""")
                    Dim sniMatch = Regex.Match(jsonText, """sni""\s*:\s*""([^""]+)""")
                    
                    If hostMatch.Success AndAlso portMatch.Success AndAlso userMatch.Success Then
                        Dim host = hostMatch.Groups(1).Value.Trim()
                        Dim port As Integer
                        Integer.TryParse(portMatch.Groups(1).Value, port)
                        Dim username = userMatch.Groups(1).Value.Trim()
                        Dim password = If(passMatch.Success, passMatch.Groups(1).Value.Trim(), "")
                        Dim sni = If(sniMatch.Success, sniMatch.Groups(1).Value.Trim(), "")
                        
                        If port > 0 AndAlso port <= 65535 Then
                            Dim sshConfig As New SSHTLSConfig With {
                                .Host = host,
                                .Port = port,
                                .Username = username,
                                .Password = password,
                                .Tag = $"3x-ui SSH - {host}:{port}",
                                .SNI = sni,
                                .LocalPort = 0,
                                .IsOnlineConfig = True,
                                .UseTLS = True
                            }
                            
                            ' Avoid duplicates
                            If Not sshConfigs.Any(Function(c) c.Host = host AndAlso c.Port = port AndAlso c.Username = username) Then
                                sshConfigs.Add(sshConfig)
                                AddLog($"[OK] Successfully parsed 3x-ui JSON SSH: {host}:{port}")
                            End If
                        End If
                    End If
                Catch
                    ' Skip malformed JSON
                End Try
            Next
        Catch
            ' JSON parsing failed, continue
        End Try

        If sshConfigs.Count = 0 Then
            AddLog("[WARNING] No SSH configurations found in HTML content", True)
            ' Log a sample of the HTML to help debug
            Dim sampleLength = Math.Min(500, htmlContent.Length)
            AddLog($"HTML Sample (first {sampleLength} chars): {htmlContent.Substring(0, sampleLength)}")
        End If

        Return sshConfigs
    End Function

    ''' <summary>
    ''' Save configuration to local cache
    ''' </summary>
    Private Sub SaveConfigCache(htmlContent As String)
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim vpnFolder = Path.Combine(appDataPath, "VPNClientApp")

            If Not Directory.Exists(vpnFolder) Then
                Directory.CreateDirectory(vpnFolder)
            End If

            Dim cacheFile = Path.Combine(vpnFolder, "config_cache.html")
            File.WriteAllText(cacheFile, htmlContent)

            ' Save timestamp
            Dim timestampFile = Path.Combine(vpnFolder, "cache_timestamp.txt")
            File.WriteAllText(timestampFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        Catch ex As Exception
            AddLog($"Failed to save cache: {ex.Message}", True)
        End Try
    End Sub

    ''' <summary>
    ''' Load configuration from local cache
    ''' </summary>
    Private Function LoadConfigCache() As ConfigurationResult
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim cacheFile = Path.Combine(appDataPath, "VPNClientApp", "config_cache.html")

            If File.Exists(cacheFile) Then
                Dim htmlContent = File.ReadAllText(cacheFile)
                Return New ConfigurationResult With {
                    .VLessLinks = ExtractVLessLinks(htmlContent),
                    .SSHConfigs = ExtractSSHConfigs(htmlContent)
                }
            End If
        Catch ex As Exception
            AddLog($"Failed to load cache: {ex.Message}", True)
        End Try

        Return New ConfigurationResult With {
            .VLessLinks = New List(Of String),
            .SSHConfigs = New List(Of SSHTLSConfig)
        }
    End Function

    ''' <summary>
    ''' Check if configuration has expired (24 hours)
    ''' </summary>
    Public Function IsConfigExpired() As Boolean
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim timestampFile = Path.Combine(appDataPath, "VPNClientApp", "cache_timestamp.txt")

            If File.Exists(timestampFile) Then
                Dim timestampText = File.ReadAllText(timestampFile).Trim()
                Dim lastUpdate As DateTime

                If DateTime.TryParse(timestampText, lastUpdate) Then
                    Dim timeSinceUpdate = DateTime.Now - lastUpdate
                    ' Configuration expires after 24 hours
                    Return timeSinceUpdate.TotalHours > 24
                End If
            End If
        Catch ex As Exception
            AddLog($"Error checking expiry: {ex.Message}", True)
        End Try

        ' Return true if we can't determine (force refresh)
        Return True
    End Function

    ''' <summary>
    ''' Clear cached configurations
    ''' </summary>
    Public Sub ClearCache()
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim vpnFolder = Path.Combine(appDataPath, "VPNClientApp")

            Dim cacheFile = Path.Combine(vpnFolder, "config_cache.html")
            Dim timestampFile = Path.Combine(vpnFolder, "cache_timestamp.txt")

            If File.Exists(cacheFile) Then File.Delete(cacheFile)
            If File.Exists(timestampFile) Then File.Delete(timestampFile)

            AddLog("[OK] Cache cleared")
        Catch ex As Exception
            AddLog($"Failed to clear cache: {ex.Message}", True)
        End Try
    End Sub

    ''' <summary>
    ''' Simple logging helper
    ''' </summary>
    Private Sub AddLog(message As String, Optional isError As Boolean = False)
        Try
            ' Write to debug output
            System.Diagnostics.Debug.WriteLine($"[ConfigFetcher] {message}")
        Catch
            ' Silent fail
        End Try
    End Sub
End Class

''' <summary>
''' Result class containing both VLESS and SSH configurations
''' </summary>
Public Class ConfigurationResult
    Public Property VLessLinks As List(Of String)
    Public Property SSHConfigs As List(Of SSHTLSConfig)
End Class
