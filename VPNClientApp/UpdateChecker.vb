Imports System.IO
Imports System.Net.Http
Imports System.Reflection
Imports System.Text.Json

Public Class UpdateChecker
    Private _configFetcher As ConfigFetcher
    Private _currentConfig As VLessConfig

    Public Sub New()
        _configFetcher = New ConfigFetcher()
    End Sub

    ''' <summary>
    ''' Check for configuration updates asynchronously
    ''' </summary>
    Public Async Function CheckForUpdatesAsync() As Task
        Try
            ' Check if configuration has expired
            If Not _configFetcher.IsConfigExpired() Then
                Exit Function
            End If

            ' Fetch latest configurations from Firebase
            Dim result = Await _configFetcher.FetchConfigurationsAsync()

            If result.VLessLinks.Count > 0 Then
                ' Parse first available link as primary config
                _currentConfig = VLessParser.ParseVLessLink(result.VLessLinks(0))

                ' Validate configuration
                If VLessParser.ValidateConfig(_currentConfig) Then
                    ' Save to local storage
                    SaveConfigsLocally(result.VLessLinks)

                    ' Notify subscribers (UI will handle update notification)
                    RaiseEvent ConfigurationUpdated(_currentConfig)
                End If
            End If
        Catch ex As Exception
            ' Log error but don't crash
            LogError($"Update check failed: {ex.Message}")
        End Try
    End Function

    ''' <summary>
    ''' Save configurations locally
    ''' </summary>
    Public Sub SaveConfigsLocally(vlessLinks As List(Of String))
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim configFile = Path.Combine(appDataPath, "VPNClientApp", "configs.txt")

            If Not Directory.Exists(Path.GetDirectoryName(configFile)) Then
                Directory.CreateDirectory(Path.GetDirectoryName(configFile))
            End If

            File.WriteAllLines(configFile, vlessLinks)
        Catch ex As Exception
            LogError($"Failed to save configs locally: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Load cached configurations
    ''' </summary>
    Public Function LoadCachedConfigs() As List(Of String)
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim configFile = Path.Combine(appDataPath, "VPNClientApp", "configs.txt")

            If File.Exists(configFile) Then
                Dim lines = File.ReadAllLines(configFile).ToList()
                Return lines.Where(Function(l) Not String.IsNullOrWhiteSpace(l)).ToList()
            End If
        Catch ex As Exception
            LogError($"Failed to load cached configs: {ex.Message}")
        End Try

        Return New List(Of String)
    End Function

    ''' <summary>
    ''' Load user's custom SNI
    ''' </summary>
    Public Function LoadUserSNI() As String
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim sniFile = Path.Combine(appDataPath, "VPNClientApp", "user_sni.txt")

            If File.Exists(sniFile) Then
                Return File.ReadAllText(sniFile).Trim()
            End If
        Catch ex As Exception
            LogError($"Failed to load SNI: {ex.Message}")
        End Try

        Return String.Empty
    End Function

    ''' <summary>
    ''' Save user's custom SNI
    ''' </summary>
    Public Sub SaveUserSNI(sni As String)
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim sniFile = Path.Combine(appDataPath, "VPNClientApp", "user_sni.txt")

            If Not Directory.Exists(Path.GetDirectoryName(sniFile)) Then
                Directory.CreateDirectory(Path.GetDirectoryName(sniFile))
            End If

            File.WriteAllText(sniFile, sni)
        Catch ex As Exception
            LogError($"Failed to save SNI: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Load selected configuration index
    ''' </summary>
    Public Function LoadSelectedConfigIndex() As Integer
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim indexFile = Path.Combine(appDataPath, "VPNClientApp", "selected_index.txt")

            If File.Exists(indexFile) Then
                Dim indexStr = File.ReadAllText(indexFile).Trim()
                Dim index As Integer
                If Integer.TryParse(indexStr, index) Then
                    Return index
                End If
            End If
        Catch ex As Exception
            LogError($"Failed to load selected index: {ex.Message}")
        End Try

        Return 0
    End Function

    ''' <summary>
    ''' Save selected configuration index
    ''' </summary>
    Public Sub SaveSelectedConfigIndex(index As Integer)
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim indexFile = Path.Combine(appDataPath, "VPNClientApp", "selected_index.txt")

            If Not Directory.Exists(Path.GetDirectoryName(indexFile)) Then
                Directory.CreateDirectory(Path.GetDirectoryName(indexFile))
            End If

            File.WriteAllText(indexFile, index.ToString())
        Catch ex As Exception
            LogError($"Failed to save selected index: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Get current application version
    ''' </summary>
    Public Shared Function GetAppVersion() As String
        Try
            Return Assembly.GetExecutingAssembly().GetName().Version.ToString()
        Catch
            Return "1.0.0.0"
        End Try
    End Function

    ' SSH config persistence - only for USER-CREATED configs, not online configs
    Public Function LoadSavedSSHConfigs() As List(Of SSHTLSConfig)
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim filePath = Path.Combine(appDataPath, "VPNClientApp", "ssh_configs.json")
            If File.Exists(filePath) Then
                Dim json = File.ReadAllText(filePath)
                Dim list = JsonSerializer.Deserialize(Of List(Of SSHTLSConfig))(json)
                If list IsNot Nothing Then
                    ' Only return user-created configs (not online configs)
                    Return list.Where(Function(c) Not c.IsOnlineConfig).ToList()
                End If
            End If
        Catch ex As Exception
            LogError($"Failed to load SSH configs: {ex.Message}")
        End Try
        Return New List(Of SSHTLSConfig)
    End Function

    Public Sub SaveSavedSSHConfigs(configs As List(Of SSHTLSConfig))
        Try
            ' Only save user-created configs (filter out online configs)
            Dim userConfigs = configs.Where(Function(c) Not c.IsOnlineConfig).ToList()

            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim dir = Path.Combine(appDataPath, "VPNClientApp")
            If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
            Dim filePath = Path.Combine(dir, "ssh_configs.json")
            Dim json = JsonSerializer.Serialize(userConfigs, New JsonSerializerOptions With {.WriteIndented = True})
            File.WriteAllText(filePath, json)
        Catch ex As Exception
            LogError($"Failed to save SSH configs: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Save online SSH configurations separately
    ''' </summary>
    Public Sub SaveOnlineSSHConfigs(configs As List(Of SSHTLSConfig))
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim dir = Path.Combine(appDataPath, "VPNClientApp")
            If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
            Dim filePath = Path.Combine(dir, "online_ssh_configs.json")
            Dim json = JsonSerializer.Serialize(configs, New JsonSerializerOptions With {.WriteIndented = True})
            File.WriteAllText(filePath, json)
            
            ' Log success
            System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Saved {configs.Count} online SSH config(s) to: {filePath}")
            For Each cfg In configs
                System.Diagnostics.Debug.WriteLine($"  - {cfg.Tag} | Host: {cfg.Host}:{cfg.Port} | SNI: {cfg.SNI}")
            Next
        Catch ex As Exception
            LogError($"Failed to save online SSH configs: {ex.Message}")
            System.Diagnostics.Debug.WriteLine($"[UpdateChecker] ERROR saving online SSH configs: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Load saved online SSH configurations
    ''' </summary>
    Public Function LoadOnlineSSHConfigs() As List(Of SSHTLSConfig)
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim filePath = Path.Combine(appDataPath, "VPNClientApp", "online_ssh_configs.json")
            System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Loading online SSH configs from: {filePath}")
            System.Diagnostics.Debug.WriteLine($"[UpdateChecker] File exists: {File.Exists(filePath)}")
            
            If File.Exists(filePath) Then
                Dim json = File.ReadAllText(filePath)
                System.Diagnostics.Debug.WriteLine($"[UpdateChecker] JSON length: {json.Length} chars")
                
                Dim list = JsonSerializer.Deserialize(Of List(Of SSHTLSConfig))(json)
                If list IsNot Nothing Then
                    System.Diagnostics.Debug.WriteLine($"[UpdateChecker] Loaded {list.Count} online SSH config(s)")
                    For Each cfg In list
                        System.Diagnostics.Debug.WriteLine($"  - {cfg.Tag} | Host: {cfg.Host}:{cfg.Port} | SNI: {cfg.SNI}")
                    Next
                    Return list
                End If
            Else
                System.Diagnostics.Debug.WriteLine($"[UpdateChecker] No saved online SSH configs file found")
            End If
        Catch ex As Exception
            LogError($"Failed to load online SSH configs: {ex.Message}")
            System.Diagnostics.Debug.WriteLine($"[UpdateChecker] ERROR loading online SSH configs: {ex.Message}")
        End Try
        Return New List(Of SSHTLSConfig)
    End Function

    ''' <summary>
    ''' Log errors to file
    ''' </summary>
    Private Sub LogError(message As String)
        Try
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim logFile = Path.Combine(appDataPath, "VPNClientApp", "error.log")

            If Not Directory.Exists(Path.GetDirectoryName(logFile)) Then
                Directory.CreateDirectory(Path.GetDirectoryName(logFile))
            End If

            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}")
        Catch
            ' Silent fail on logging error
        End Try
    End Sub

    Public Event ConfigurationUpdated(config As VLessConfig)
End Class
