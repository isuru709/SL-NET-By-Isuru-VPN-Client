Imports System.ComponentModel
Imports System.Windows.Threading
Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Net.Security
Imports System.Net.Sockets
Imports System.Security.Authentication
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Diagnostics
Imports System.Net.Http
Imports System.Security.Cryptography.X509Certificates
Imports System.Net.NetworkInformation

Namespace VPNClientApp
    Partial Public Class MainWindow
        Inherits Window
        Implements INotifyPropertyChanged

        ' P/Invoke for notifying Windows of proxy changes
        <Runtime.InteropServices.DllImport("wininet.dll", SetLastError:=True)>
        Private Shared Function InternetSetOption(hInternet As IntPtr, dwOption As Integer, lpBuffer As IntPtr, dwBufferLength As Integer) As Boolean
        End Function

        Private _updateChecker As UpdateChecker
        Private _connectionManager As VPNConnectionManager
        Private _vpnShareManager As VPNShareManager
        Private _hotspotManager As HotspotManager
        Private _currentConfig As Object
        Private _isConnected As Boolean
        Private _sniCts As CancellationTokenSource
        Private _speedCts As CancellationTokenSource
        Private _availableConfigs As New List(Of ServerConfigItem)
        Private _savedSSHConfigs As New List(Of SSHTLSConfig)
        Private _savedCustomConfigs As New List(Of VLessConfig)
        Private _proxyMode As String = "System" ' Default proxy mode
        Private _previousProxySettings As Dictionary(Of String, Object) = Nothing
        Private _blocker As New BlockerSettings()
        Private _adBlockMgr As New AdBlockListManager()
        Private _splitTunnel As New SplitTunnelSettings()
        Private _selectedAppPath As String = Nothing

        Public Sub New()
            InitializeComponent()

            _updateChecker = New UpdateChecker()
            _connectionManager = New VPNConnectionManager()
            _vpnShareManager = New VPNShareManager()
            _hotspotManager = New HotspotManager()

            AddHandler _updateChecker.ConfigurationUpdated, AddressOf OnConfigurationUpdated
            AddHandler _connectionManager.LogMessage, AddressOf OnConnectionLog
            AddHandler _vpnShareManager.LogMessage, AddressOf OnConnectionLog
            AddHandler _vpnShareManager.StatusChanged, AddressOf OnShareStatusChanged
            AddHandler _hotspotManager.LogMessage, AddressOf OnConnectionLog

            ' Add handler for SSH key authentication checkbox
            AddHandler UseSSHKeyCheck.Checked, Sub(s, e) SSHKeyPanel.Visibility = Visibility.Visible
            AddHandler UseSSHKeyCheck.Unchecked, Sub(s, e) SSHKeyPanel.Visibility = Visibility.Collapsed
            AddHandler BrowseSSHKeyButton.Click, AddressOf BrowseSSHKeyButton_Click

            AddHandler Me.Loaded, Sub(s, e)
                                      ' Initialize UI on startup

                                      Dim loadTask = Task.Run(Async Function()
                                                                  LoadSavedSSHConfigsUI()
                                                                  LoadSavedCustomConfigsUI()
                                                                  LoadBlockerSettingsUI()
                                                                  LoadSplitTunnelSettingsUI()
                                                                  ' Ensure external domains are loaded at startup if Ads is on
                                                                  Await AutoRefreshAdlistsIfNeededAsync()
                                                                  Await LoadConfigurationAsync(useCache:=True)
                                                                  Await CheckForUpdatesAsync()
                                                                  ' Detect and set current proxy mode
                                                                  DetectAndSetCurrentProxyMode()
                                                              End Function)
                                  End Sub
        End Sub

        ''' <summary>
        ''' Handle window closing event to disable manual proxy settings
        ''' </summary>
        Private Sub Window_Closing(sender As Object, e As ComponentModel.CancelEventArgs)
            Try
                AddLog("Application closing - restoring proxy settings...")
                
                ' Disconnect VPN if connected
                If _isConnected Then
                    _connectionManager?.Disconnect()
                End If

                ' Disable system proxy to restore original settings
                Dim registryKey = "Software\Microsoft\Windows\CurrentVersion\Internet Settings"
                Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey, True)
                    If key IsNot Nothing Then
                        ' Restore previous proxy settings if they were backed up
                        If _previousProxySettings IsNot Nothing Then
                            ' Restore ProxyEnable
                            If _previousProxySettings.ContainsKey("ProxyEnable") Then
                                Dim proxyEnable = CInt(_previousProxySettings("ProxyEnable"))
                                key.SetValue("ProxyEnable", proxyEnable, Microsoft.Win32.RegistryValueKind.DWord)
                            Else
                                key.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord)
                            End If

                            ' Restore ProxyServer
                            If _previousProxySettings.ContainsKey("ProxyServer") Then
                                Dim proxyServer = _previousProxySettings("ProxyServer").ToString()
                                If Not String.IsNullOrEmpty(proxyServer) Then
                                    key.SetValue("ProxyServer", proxyServer, Microsoft.Win32.RegistryValueKind.String)
                                Else
                                    key.DeleteValue("ProxyServer", False)
                                End If
                            Else
                                key.DeleteValue("ProxyServer", False)
                            End If

                            ' Restore ProxyOverride
                            If _previousProxySettings.ContainsKey("ProxyOverride") Then
                                Dim proxyOverride = _previousProxySettings("ProxyOverride").ToString()
                                If Not String.IsNullOrEmpty(proxyOverride) Then
                                    key.SetValue("ProxyOverride", proxyOverride, Microsoft.Win32.RegistryValueKind.String)
                                Else
                                    key.DeleteValue("ProxyOverride", False)
                                End If
                            Else
                                key.DeleteValue("ProxyOverride", False)
                            End If

                            ' Restore AutoConfigURL
                            If _previousProxySettings.ContainsKey("AutoConfigURL") Then
                                Dim autoConfigURL = _previousProxySettings("AutoConfigURL").ToString()
                                If Not String.IsNullOrEmpty(autoConfigURL) Then
                                    key.SetValue("AutoConfigURL", autoConfigURL, Microsoft.Win32.RegistryValueKind.String)
                                Else
                                    key.DeleteValue("AutoConfigURL", False)
                                End If
                            Else
                                key.DeleteValue("AutoConfigURL", False)
                            End If

                            AddLog("Previous proxy settings restored")
                        Else
                            ' No previous settings, just disable proxy
                            key.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord)
                            key.DeleteValue("ProxyServer", False)
                            key.DeleteValue("ProxyOverride", False)
                            key.DeleteValue("AutoConfigURL", False)
                            AddLog("Proxy disabled")
                        End If
                    End If
                End Using

                ' Notify Windows of the proxy change
                Const INTERNET_OPTION_SETTINGS_CHANGED As Integer = 39
                Const INTERNET_OPTION_REFRESH As Integer = 37
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0)
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0)

            Catch ex As Exception
                AddLog($"Error during application close: {ex.Message}", True)
            End Try
        End Sub
        Private Async Sub UpdateXrayButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If UpdateXrayButton IsNot Nothing Then
                    UpdateXrayButton.IsEnabled = False
                    UpdateXrayButton.Content = "Updating…"
                End If
                AddLog("Starting Xray update (x64)…")
                Dim updater As New XrayUpdater()
                Dim result = Await updater.UpdateXrayAsync(AddressOf AddLog,
                    Sub()
                        AddLog("Preparing to install update: disconnecting VPN…")
                        Try
                            _connectionManager?.Disconnect()
                        Catch
                        End Try
                    End Sub,
                    Function()
                        Dim dlg As New Microsoft.Win32.OpenFileDialog()
                        dlg.Title = "Select Xray-windows-64.zip"
                        dlg.Filter = "Zip Files (*.zip)|*.zip|All Files (*.*)|*.*"
                        dlg.FileName = "Xray-windows-64.zip"
                        Dim ok = dlg.ShowDialog()
                        If ok.HasValue AndAlso ok.Value Then
                            Return dlg.FileName
                        End If
                        Return Nothing
                    End Function)
                If result.Success Then
                    AddLog($"✓ Xray update completed: v{result.Version}")
                    MessageBox.Show($"Xray-core has been updated to version {result.Version}.", "Update Complete", MessageBoxButton.OK, MessageBoxImage.Information)
                Else
                    AddLog($"✗ Xray update failed: {result.ErrorMessage}", True)
                    MessageBox.Show($"Failed to update Xray-core: {result.ErrorMessage}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error)
                End If
            Catch ex As Exception
                AddLog($"✗ Update error: {ex.Message}", True)
                MessageBox.Show($"Unexpected error: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error)
            Finally
                If UpdateXrayButton IsNot Nothing Then
                    UpdateXrayButton.IsEnabled = True
                    UpdateXrayButton.Content = "⬇ Update Xray"
                End If
            End Try
        End Sub

        Private Sub OnConnectionLog(message As String, isError As Boolean)
            AddLog(message, isError)
        End Sub

        ' ============================
        ' Blocker: settings helpers
        ' ============================
        Private Function GetBlockerSettingsPath() As String
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim dir = Path.Combine(appDataPath, "VPNClientApp")
            If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
            Return Path.Combine(dir, "blocker_settings.json")
        End Function

        Private Sub LoadBlockerSettingsUI()
            Try
                Dim path = GetBlockerSettingsPath()
                If File.Exists(path) Then
                    Dim json = File.ReadAllText(path)
                    Dim s = System.Text.Json.JsonSerializer.Deserialize(Of BlockerSettings)(json)
                    If s IsNot Nothing Then _blocker = s
                End If
            Catch
            End Try

            Dispatcher.Invoke(Sub()
                                  If AdsBlockCheck IsNot Nothing Then AdsBlockCheck.IsChecked = _blocker.AdsEnabled
                                  If AdultBlockCheck IsNot Nothing Then AdultBlockCheck.IsChecked = _blocker.AdultEnabled
                                  If SocialBlockCheck IsNot Nothing Then SocialBlockCheck.IsChecked = _blocker.SocialEnabled
                                  If UseOisdSmallCheck IsNot Nothing Then UseOisdSmallCheck.IsChecked = _blocker.UseOisdSmall
                                  If UseOisdMediumCheck IsNot Nothing Then UseOisdMediumCheck.IsChecked = _blocker.UseOisdMedium
                                  If UseOisdFullCheck IsNot Nothing Then UseOisdFullCheck.IsChecked = _blocker.UseOisdFull
                                  RefreshBlockedListUI()
                                  RefreshWhitelistListUI()
                                  UpdateCategoriesSummary()
                                  UpdateListsSummaryUI()
                              End Sub)

            ' Push to connection manager
            _connectionManager.UpdateBlockerSettings(_blocker)
        End Sub

        Private Sub SaveBlockerSettings()
            Try
                Dim path = GetBlockerSettingsPath()
                Dim json = System.Text.Json.JsonSerializer.Serialize(_blocker, New System.Text.Json.JsonSerializerOptions With {.WriteIndented = True})
                File.WriteAllText(path, json)
            Catch ex As Exception
                AddLog($"Failed to save blocker settings: {ex.Message}", True)
            End Try
        End Sub

        ' Periodic (24h) refresh for ad lists + initial load of merged domains
        Private Async Function AutoRefreshAdlistsIfNeededAsync() As Task
            Try
                ' Always try to load existing merged list into routing when Ads is enabled
                If _blocker IsNot Nothing AndAlso _blocker.AdsEnabled Then
                    Dim existing = _adBlockMgr.LoadMergedDomains()
                    If existing IsNot Nothing AndAlso existing.Count > 0 Then
                        _connectionManager.UpdateExternalBlockDomains(existing)
                        AddLog($"Ad lists: loaded {existing.Count} merged domains at startup")
                    End If
                End If

                ' Refresh only if Ads is enabled and last update is older than 24h (or missing)
                If _blocker Is Nothing OrElse Not _blocker.AdsEnabled Then Return
                Dim last As DateTime = DateTime.MinValue
                If Not String.IsNullOrWhiteSpace(_blocker.BlocklistLastUpdated) Then
                    DateTime.TryParse(_blocker.BlocklistLastUpdated, last)
                End If
                Dim due As Boolean = (last = DateTime.MinValue) OrElse (DateTime.Now - last) > TimeSpan.FromHours(24)
                If Not due Then Return

                AddLog("Ad lists: refreshing (older than 24h)…")
                Dim total = Await _adBlockMgr.UpdateListsAsync(_blocker, AddressOf AddLog)
                If total > 0 Then
                    _blocker.BlocklistLastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                    SaveBlockerSettings()
                    Dispatcher.Invoke(Sub() UpdateListsSummaryUI())

                    ' Reload merged domains into routing
                    Dim external = _adBlockMgr.LoadMergedDomains()
                    _connectionManager.UpdateExternalBlockDomains(external)
                    AddLog($"Ad lists: auto-refreshed and loaded {external.Count} domains")

                    ' If connected, re-apply routing so new blocks take effect
                    Try
                        Await _connectionManager.ApplyBlockerRulesAsync()
                    Catch
                    End Try
                Else
                    AddLog("Ad lists: no updates during auto-refresh; using existing merged list")
                End If
            Catch ex As Exception
                AddLog($"Ad lists auto-refresh error: {ex.Message}", True)
            End Try
        End Function

        Private Sub RefreshBlockedListUI()
            Try
                If BlockedUrlsListBox Is Nothing Then Return
                BlockedUrlsListBox.ItemsSource = Nothing
                BlockedUrlsListBox.ItemsSource = If(_blocker.CustomDomains, New List(Of String))
            Catch
            End Try
        End Sub

        Private Sub UpdateListsSummaryUI()
            Try
                If ListsSummaryText Is Nothing Then Return
                Dim mergedPath = _adBlockMgr.GetMergedPath()
                Dim count As Integer = 0
                If File.Exists(mergedPath) Then
                    Using sr As New StreamReader(mergedPath)
                        While sr.ReadLine() IsNot Nothing
                            count += 1
                        End While
                    End Using
                End If
                ListsSummaryText.Text = $"Lists: {count} domains • Last: {_blocker.BlocklistLastUpdated}"
            Catch
            End Try
        End Sub

        Private Sub RefreshWhitelistListUI()
            Try
                If WhitelistUrlsListBox Is Nothing Then Return
                WhitelistUrlsListBox.ItemsSource = Nothing
                WhitelistUrlsListBox.ItemsSource = If(_blocker.WhitelistDomains, New List(Of String))
            Catch
            End Try
        End Sub

        Private Sub UpdateCategoriesSummary()
            Try
                If CategoriesSummaryText Is Nothing Then Return
                Dim items As New List(Of String)
                items.Add($"Ads: {(If(_blocker.AdsEnabled, "ON", "OFF"))}")
                items.Add($"18+: {(If(_blocker.AdultEnabled, "ON", "OFF"))}")
                items.Add($"Social: {(If(_blocker.SocialEnabled, "ON", "OFF"))}")
                CategoriesSummaryText.Text = "Categories applied: " & String.Join(" | ", items)
            Catch
            End Try
        End Sub

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

        Private Async Function ApplyBlockerRulesAsync() As Task
            Try
                _connectionManager.UpdateBlockerSettings(_blocker)
                SaveBlockerSettings()
                If BlockerStatusText IsNot Nothing Then
                    BlockerStatusText.Text = "Applying..."
                    BlockerStatusText.Foreground = New Media.SolidColorBrush(Media.Colors.DarkOrange)
                End If
                Await _connectionManager.ApplyBlockerRulesAsync()
                If BlockerStatusText IsNot Nothing Then
                    BlockerStatusText.Text = "Applied"
                    BlockerStatusText.Foreground = New Media.SolidColorBrush(Media.Colors.Green)
                End If
            Catch ex As Exception
                If BlockerStatusText IsNot Nothing Then
                    BlockerStatusText.Text = "Apply failed"
                    BlockerStatusText.Foreground = New Media.SolidColorBrush(Media.Colors.Red)
                End If
                AddLog($"Blocker apply error: {ex.Message}", True)
            End Try
        End Function

        ' ============================
        ' Blocker: UI handlers
        ' ============================
        Private Sub AddBlockButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim raw = CustomBlockTextBox?.Text
                Dim d = NormalizeDomain(raw)
                If String.IsNullOrEmpty(d) Then
                    MessageBox.Show("Enter a valid URL or domain (e.g., example.com)", "Validation", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If
                If _blocker.CustomDomains Is Nothing Then _blocker.CustomDomains = New List(Of String)()
                If Not _blocker.CustomDomains.Any(Function(x) x.Equals(d, StringComparison.OrdinalIgnoreCase)) Then
                    _blocker.CustomDomains.Add(d)
                    SaveBlockerSettings()
                    RefreshBlockedListUI()
                    CustomBlockTextBox.Clear()
                    UpdateCategoriesSummary()
                End If
            Catch ex As Exception
                AddLog($"Add block error: {ex.Message}", True)
            End Try
        End Sub

        Private Async Sub ApplyBlockButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                ' Check mutual exclusivity
                If IsSplitTunnelActive() Then
                    MessageBox.Show("Split Tunnel is active. Disable split tunnel first to use Blocker.", "Conflict", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                _blocker.AdsEnabled = AdsBlockCheck.IsChecked.GetValueOrDefault(False)
                _blocker.AdultEnabled = AdultBlockCheck.IsChecked.GetValueOrDefault(False)
                _blocker.SocialEnabled = SocialBlockCheck.IsChecked.GetValueOrDefault(False)
                _blocker.UseOisdSmall = UseOisdSmallCheck.IsChecked.GetValueOrDefault(True)
                _blocker.UseOisdMedium = If(UseOisdMediumCheck IsNot Nothing, UseOisdMediumCheck.IsChecked.GetValueOrDefault(False), False)
                _blocker.UseOisdFull = If(UseOisdFullCheck IsNot Nothing, UseOisdFullCheck.IsChecked.GetValueOrDefault(False), False)
                UpdateCategoriesSummary()

                ' If Ads blocking is enabled, load any existing merged domains into routing
                If _blocker.AdsEnabled Then
                    Dim external = _adBlockMgr.LoadMergedDomains()
                    If external IsNot Nothing AndAlso external.Count > 0 Then
                        _connectionManager.UpdateExternalBlockDomains(external)
                        AddLog($"Loaded {external.Count} domains from merged list for Ads blocking")
                    Else
                        AddLog("No merged adblock list found. Click 'Update Lists' to download EasyList, EasyPrivacy, Peter Lowe, and uBlock filters.")
                    End If
                Else
                    ' If not using Ads list, clear external domains to avoid stale blocks
                    _connectionManager.UpdateExternalBlockDomains(New List(Of String))
                End If
                Await ApplyBlockerRulesAsync()
            Catch ex As Exception
                AddLog($"Apply button error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub DeleteSelectedBlockButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If BlockedUrlsListBox Is Nothing OrElse BlockedUrlsListBox.SelectedItem Is Nothing Then
                    MessageBox.Show("Select a domain to delete.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If
                Dim sel = BlockedUrlsListBox.SelectedItem.ToString()
                _blocker.CustomDomains.RemoveAll(Function(x) x.Equals(sel, StringComparison.OrdinalIgnoreCase))
                SaveBlockerSettings()
                RefreshBlockedListUI()
            Catch ex As Exception
                AddLog($"Delete block error: {ex.Message}", True)
            End Try
        End Sub

        Private Async Sub UpdateListsButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If UseOisdSmallCheck IsNot Nothing Then _blocker.UseOisdSmall = UseOisdSmallCheck.IsChecked.GetValueOrDefault(True)
                If UseOisdMediumCheck IsNot Nothing Then _blocker.UseOisdMedium = UseOisdMediumCheck.IsChecked.GetValueOrDefault(False)
                If UseOisdFullCheck IsNot Nothing Then _blocker.UseOisdFull = UseOisdFullCheck.IsChecked.GetValueOrDefault(False)
                ' Tie Ads checkbox state into list update behavior
                If AdsBlockCheck IsNot Nothing Then _blocker.AdsEnabled = AdsBlockCheck.IsChecked.GetValueOrDefault(False)
                SaveBlockerSettings()
                UpdateListsButton.IsEnabled = False
                UpdateListsButton.Content = "Updating…"
                AddLog("Updating adblock lists…")
                Dim total = Await _adBlockMgr.UpdateListsAsync(_blocker, AddressOf AddLog)
                If total > 0 Then
                    _blocker.BlocklistLastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                    SaveBlockerSettings()
                Else
                    AddLog("No new lists applied; using previous merged list if available")
                End If
                UpdateListsSummaryUI()

                ' Load merged and push to connection manager
                Dim external = _adBlockMgr.LoadMergedDomains()
                _connectionManager.UpdateExternalBlockDomains(external)
                AddLog($"Loaded {external.Count} domains into external block routing")

                ' If connected, re-apply routing
                Await ApplyBlockerRulesAsync()
            Catch ex As Exception
                AddLog($"Update lists error: {ex.Message}", True)
            Finally
                UpdateListsButton.IsEnabled = True
                UpdateListsButton.Content = "⬇ Update Lists"
            End Try
        End Sub

        Private Sub ClearAllBlocksButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If _blocker.CustomDomains Is Nothing OrElse _blocker.CustomDomains.Count = 0 Then Return
                If MessageBox.Show("Clear all custom blocked domains?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) = MessageBoxResult.Yes Then
                    _blocker.CustomDomains.Clear()
                    SaveBlockerSettings()
                    RefreshBlockedListUI()
                End If
            Catch ex As Exception
                AddLog($"Clear blocks error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub AddWhitelistButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim raw = WhitelistTextBox?.Text
                Dim d = NormalizeDomain(raw)
                If String.IsNullOrEmpty(d) Then
                    MessageBox.Show("Enter a valid URL or domain (e.g., example.com)", "Validation", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If
                If _blocker.WhitelistDomains Is Nothing Then _blocker.WhitelistDomains = New List(Of String)()
                If Not _blocker.WhitelistDomains.Any(Function(x) x.Equals(d, StringComparison.OrdinalIgnoreCase)) Then
                    _blocker.WhitelistDomains.Add(d)
                    SaveBlockerSettings()
                    RefreshWhitelistListUI()
                    WhitelistTextBox.Clear()
                End If
            Catch ex As Exception
                AddLog($"Add whitelist error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub DeleteSelectedWhitelistButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If WhitelistUrlsListBox Is Nothing OrElse WhitelistUrlsListBox.SelectedItem Is Nothing Then
                    MessageBox.Show("Select a domain to delete.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If
                Dim sel = WhitelistUrlsListBox.SelectedItem.ToString()
                _blocker.WhitelistDomains.RemoveAll(Function(x) x.Equals(sel, StringComparison.OrdinalIgnoreCase))
                SaveBlockerSettings()
                RefreshWhitelistListUI()
            Catch ex As Exception
                AddLog($"Delete whitelist error: {ex.Message}", True)
            End Try
        End Sub

        ' ============================
        ' Split Tunnel: settings helpers
        ' ============================
        Private Function GetSplitTunnelSettingsPath() As String
            Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim dir = Path.Combine(appDataPath, "VPNClientApp")
            If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
            Return Path.Combine(dir, "splittunnel_settings.json")
        End Function

        Private Sub LoadSplitTunnelSettingsUI()
            Try
                Dim path = GetSplitTunnelSettingsPath()
                If File.Exists(path) Then
                    Dim json = File.ReadAllText(path)
                    Dim s = System.Text.Json.JsonSerializer.Deserialize(Of SplitTunnelSettings)(json)
                    If s IsNot Nothing Then _splitTunnel = s
                End If
            Catch
            End Try

            Dispatcher.Invoke(Sub()
                                  ' App-based routing feature is currently under development and disabled in the UI
                                  If AppBasedRoutingCheck IsNot Nothing Then
                                      AppBasedRoutingCheck.IsChecked = False
                                      AppBasedRoutingCheck.IsEnabled = False
                                      Try
                                          AppBasedRoutingCheck.Content = "App-based routing (Developing)"
                                      Catch
                                      End Try
                                  End If
                                  If CustomSNIRoutingCheck IsNot Nothing Then CustomSNIRoutingCheck.IsChecked = _splitTunnel.CustomSNIEnabled
                                  ' Disable app list interactions while feature is developing
                                  If BrowseAppButton IsNot Nothing Then BrowseAppButton.IsEnabled = False
                                  If RoutedAppsListBox IsNot Nothing Then RoutedAppsListBox.IsEnabled = False
                                  If DeleteSelectedAppButton IsNot Nothing Then DeleteSelectedAppButton.IsEnabled = False
                                  If SelectedAppText IsNot Nothing Then SelectedAppText.Text = "App routing disabled — Developing"
                                  RefreshRoutedAppsListUI()
                                  RefreshCustomSNIsListUI()
                                  UpdateSplitTunnelSummary()
                              End Sub)

            ' Push to connection manager
            _connectionManager.UpdateSplitTunnelSettings(_splitTunnel)
        End Sub

        Private Sub SaveSplitTunnelSettings()
            Try
                Dim path = GetSplitTunnelSettingsPath()
                Dim json = System.Text.Json.JsonSerializer.Serialize(_splitTunnel, New System.Text.Json.JsonSerializerOptions With {.WriteIndented = True})
                File.WriteAllText(path, json)
            Catch ex As Exception
                AddLog($"Failed to save split tunnel settings: {ex.Message}", True)
            End Try
        End Sub

        Private Sub RefreshRoutedAppsListUI()
            Try
                If RoutedAppsListBox Is Nothing Then Return
                RoutedAppsListBox.ItemsSource = Nothing
                RoutedAppsListBox.ItemsSource = If(_splitTunnel.AppPaths, New List(Of String))
            Catch
            End Try
        End Sub

        Private Sub RefreshCustomSNIsListUI()
            Try
                If RoutedSNIsListBox Is Nothing Then Return
                RoutedSNIsListBox.ItemsSource = Nothing
                RoutedSNIsListBox.ItemsSource = If(_splitTunnel.CustomSNIs, New List(Of String))
            Catch
            End Try
        End Sub

        Private Sub UpdateSplitTunnelSummary()
            Try
                If SplitTunnelSummaryText Is Nothing Then Return
                Dim items As New List(Of String)
                If _splitTunnel.AppBasedEnabled Then
                    Dim appCount = If(_splitTunnel.AppPaths IsNot Nothing, _splitTunnel.AppPaths.Count, 0)
                    items.Add($"Apps: {appCount}")
                End If
                If _splitTunnel.CustomSNIEnabled Then
                    Dim sniCount = If(_splitTunnel.CustomSNIs IsNot Nothing, _splitTunnel.CustomSNIs.Count, 0)
                    items.Add($"SNIs: {sniCount}")
                End If
                If items.Count > 0 Then
                    SplitTunnelSummaryText.Text = "Split Tunnel: " & String.Join(" | ", items)
                Else
                    SplitTunnelSummaryText.Text = "Split Tunnel: Inactive"
                End If
            Catch
            End Try
        End Sub

        Private Function IsSplitTunnelActive() As Boolean
            Return (_splitTunnel.AppBasedEnabled AndAlso _splitTunnel.AppPaths?.Count > 0) OrElse (_splitTunnel.CustomSNIEnabled AndAlso _splitTunnel.CustomSNIs?.Count > 0)
        End Function

        Private Function IsBlockerActive() As Boolean
            Return _blocker.AdsEnabled OrElse _blocker.AdultEnabled OrElse _blocker.SocialEnabled OrElse (_blocker.CustomDomains?.Count > 0) OrElse _blocker.UseOisdSmall OrElse _blocker.UseOisdMedium OrElse _blocker.UseOisdFull
        End Function

        ' ============================
        ' Split Tunnel: UI handlers
        ' ============================
        Private Sub BrowseAppButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                MessageBox.Show("App-based routing is under development and temporarily disabled.", "Feature Developing", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            Catch ex As Exception
                AddLog($"Browse app (disabled) error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub AddSNIButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If SplitTunnelSNITextBox Is Nothing OrElse String.IsNullOrWhiteSpace(SplitTunnelSNITextBox.Text) Then
                    MessageBox.Show("Enter SNI to add.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If

                Dim sni = SplitTunnelSNITextBox.Text.Trim().ToLower()
                If _splitTunnel.CustomSNIs Is Nothing Then _splitTunnel.CustomSNIs = New List(Of String)()

                If Not _splitTunnel.CustomSNIs.Any(Function(x) x.Equals(sni, StringComparison.OrdinalIgnoreCase)) Then
                    _splitTunnel.CustomSNIs.Add(sni)
                    SaveSplitTunnelSettings()
                    RefreshCustomSNIsListUI()
                    UpdateSplitTunnelSummary()
                    SplitTunnelSNITextBox.Clear()
                Else
                    MessageBox.Show("SNI already in list.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                End If
            Catch ex As Exception
                AddLog($"Add SNI error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub DeleteSelectedSNIButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If RoutedSNIsListBox Is Nothing OrElse RoutedSNIsListBox.SelectedItem Is Nothing Then
                    MessageBox.Show("Select an SNI to delete.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If
                Dim sel = RoutedSNIsListBox.SelectedItem.ToString()
                _splitTunnel.CustomSNIs.RemoveAll(Function(x) x.Equals(sel, StringComparison.OrdinalIgnoreCase))
                SaveSplitTunnelSettings()
                RefreshCustomSNIsListUI()
                UpdateSplitTunnelSummary()
            Catch ex As Exception
                AddLog($"Delete SNI error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub DeleteSelectedAppButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                MessageBox.Show("App-based routing is under development and temporarily disabled.", "Feature Developing", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            Catch ex As Exception
                AddLog($"Delete app (disabled) error: {ex.Message}", True)
            End Try
        End Sub

        Private Async Sub ApplySplitTunnelButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                ' Check mutual exclusivity
                If IsBlockerActive() Then
                    MessageBox.Show("Blocker tab is active. Disable blocker first to use Split Tunnel.", "Conflict", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                ' App-based routing is temporarily disabled (under development).
                If AppBasedRoutingCheck IsNot Nothing AndAlso AppBasedRoutingCheck.IsChecked.GetValueOrDefault(False) Then
                    MessageBox.Show("App-based routing is under development and temporarily disabled.", "Feature Developing", MessageBoxButton.OK, MessageBoxImage.Information)
                End If
                _splitTunnel.AppBasedEnabled = False
                _splitTunnel.CustomSNIEnabled = CustomSNIRoutingCheck.IsChecked.GetValueOrDefault(False)
                UpdateSplitTunnelSummary()

                If SplitTunnelStatusText IsNot Nothing Then
                    SplitTunnelStatusText.Text = "Applying..."
                    SplitTunnelStatusText.Foreground = New Media.SolidColorBrush(Media.Colors.DarkOrange)
                End If

                _connectionManager.UpdateSplitTunnelSettings(_splitTunnel)
                SaveSplitTunnelSettings()
                Await _connectionManager.ApplySplitTunnelRulesAsync()

                If SplitTunnelStatusText IsNot Nothing Then
                    SplitTunnelStatusText.Text = "Applied"
                    SplitTunnelStatusText.Foreground = New Media.SolidColorBrush(Media.Colors.Green)
                End If
            Catch ex As Exception
                If SplitTunnelStatusText IsNot Nothing Then
                    SplitTunnelStatusText.Text = "Apply failed"
                    SplitTunnelStatusText.Foreground = New Media.SolidColorBrush(Media.Colors.Red)
                End If
                AddLog($"Split tunnel apply error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub ClearAllSplitTunnelButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If MessageBox.Show("Clear all split tunnel settings?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) = MessageBoxResult.Yes Then
                    _splitTunnel.AppPaths.Clear()
                    _splitTunnel.CustomSNIs.Clear()
                    _splitTunnel.AppBasedEnabled = False
                    _splitTunnel.CustomSNIEnabled = False
                    SaveSplitTunnelSettings()
                    RefreshRoutedAppsListUI()
                    RefreshCustomSNIsListUI()
                    UpdateSplitTunnelSummary()
                    If AppBasedRoutingCheck IsNot Nothing Then AppBasedRoutingCheck.IsChecked = False
                    If CustomSNIRoutingCheck IsNot Nothing Then CustomSNIRoutingCheck.IsChecked = False
                    If SplitTunnelSNITextBox IsNot Nothing Then SplitTunnelSNITextBox.Clear()
                    If SelectedAppText IsNot Nothing Then SelectedAppText.Text = "No app selected"
                End If
            Catch ex As Exception
                AddLog($"Clear split tunnel error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub AddLog(message As String, Optional isError As Boolean = False)
            Try
                Dispatcher.Invoke(Sub()
                                      If LogTextBox Is Nothing Then Return
                                      Dim timestamp = DateTime.Now.ToString("HH:mm:ss")
                                      Dim logEntry = $"[{timestamp}] {message}{Environment.NewLine}"
                                      LogTextBox.AppendText(logEntry)
                                      LogTextBox.ScrollToEnd()
                                  End Sub)
            Catch
            End Try
        End Sub

        Private Sub UpdateStatusUI(isConnected As Boolean, protocol As String, Optional configInfo As String = "")
            Dim activeTab = MainTabControl.SelectedIndex

            If activeTab = 0 Then ' Online VPN
                If isConnected Then
                    OnlineStatusBadge.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    OnlineStatusText.Text = "🟢 Connected"
                    OnlineProtocolText.Text = protocol
                    OnlineProtocolText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    OnlineConfigStatus.Text = If(String.IsNullOrEmpty(configInfo), "Active connection", configInfo)
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                Else
                    OnlineStatusBadge.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7))
                    OnlineStatusText.Text = "⚫ Disconnected"
                    OnlineProtocolText.Text = "N/A"
                    OnlineProtocolText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                    OnlineConfigStatus.Text = If(String.IsNullOrEmpty(configInfo), "No active connection", configInfo)
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                End If
            ElseIf activeTab = 1 Then ' Custom SSH
                If isConnected Then
                    SSHStatusBadge.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    SSHStatusText.Text = "🟢 Connected"
                    SSHProtocolText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    SSHConfigStatusText.Text = "Active SSH tunnel"
                    SSHConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    SSHConnectionInfo.Visibility = Visibility.Visible

                    If TypeOf _currentConfig Is SSHTLSConfig Then
                        Dim sshCfg = DirectCast(_currentConfig, SSHTLSConfig)
                        SSHServerInfo.Text = $"Server: {sshCfg.Host}:{sshCfg.Port}"
                        SSHProxyInfo.Text = $"Local SOCKS: 127.0.0.1:{sshCfg.LocalPort}"
                    End If
                Else
                    SSHStatusBadge.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7))
                    SSHStatusText.Text = "⚫ Disconnected"
                    SSHProtocolText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                    SSHConfigStatusText.Text = "Not connected"
                    SSHConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                    SSHConnectionInfo.Visibility = Visibility.Collapsed
                End If
            ElseIf activeTab = 2 Then ' Custom VLESS/VMESS
                If isConnected Then
                    CustomStatusBadge.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    CustomStatusText.Text = "🟢 Connected"
                    CustomProtocolText.Text = protocol
                    CustomProtocolText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    CustomConfigStatusText.Text = "Active connection"
                    CustomConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    CustomConnectionInfo.Visibility = Visibility.Visible

                    If TypeOf _currentConfig Is VLessConfig Then
                        Dim cfg = DirectCast(_currentConfig, VLessConfig)
                        CustomServerInfo.Text = $"Server: {cfg.Host}:{cfg.Port}"
                        CustomProxyInfo.Text = $"Transport: {cfg.TransportType} | Security: {cfg.Security}"
                    End If
                Else
                    CustomStatusBadge.Background = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7))
                    CustomStatusText.Text = "⚫ Disconnected"
                    CustomProtocolText.Text = "N/A"
                    CustomProtocolText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                    CustomConfigStatusText.Text = "Not connected"
                    CustomConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                    CustomConnectionInfo.Visibility = Visibility.Collapsed
                End If
            End If
        End Sub

        Private Sub UpdateTrafficStats(downloaded As String, uploaded As String)
            ' Update all tabs
            OnlineDownloadedText.Text = downloaded
            OnlineUploadedText.Text = uploaded
            SSHDownloadedText.Text = downloaded
            SSHUploadedText.Text = uploaded
            CustomDownloadedText.Text = downloaded
            CustomUploadedText.Text = uploaded
        End Sub

        Private Sub ClearLogsButton_Click(sender As Object, e As RoutedEventArgs)
            LogTextBox.Clear()
            AddLog("Logs cleared")
        End Sub

        ' ============================
        ' SNI Check: UI handlers
        ' ============================
        Private Sub AddSniLog(message As String)
            ' Write SNI logs into the main log area to avoid duplicating UI
            AddLog($"[SNI] {message}")
        End Sub

        Private Async Sub SniCheckButton_Click(sender As Object, e As RoutedEventArgs)
            Dim host As String = If(SniInputTextBox?.Text, String.Empty).Trim()
            Dim portVal As Integer = 443
            Integer.TryParse(If(SniPortTextBox?.Text, "443"), portVal)
            If portVal <= 0 Then portVal = 443

            If String.IsNullOrWhiteSpace(host) Then
                MessageBox.Show("Please enter an SNI (hostname).", "SNI Check", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If

            SniCheckButton.IsEnabled = False
            SniStopButton.IsEnabled = True
            ' No separate SNI text log; using main logs

            _sniCts?.Dispose()
            _sniCts = New CancellationTokenSource()
            Dim token = _sniCts.Token

            AddSniLog($"Starting SNI check for {host}:{portVal} …")

            ' Reset summary panel
            Dispatcher.Invoke(Sub()
                                  SniPingText.Text = "-"
                                  SniTlsText.Text = "-"
                                  SniIdleText.Text = "-"
                                  SniSpeedText.Text = "-"
                                  SniClassText.Text = "-"
                                  SniInlineStatusText.Text = ""
                                  SniInlineStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                              End Sub)

            Try
                ' 1) DNS resolve
                Dim sw = Stopwatch.StartNew()
                Dim addrs = Await Dns.GetHostAddressesAsync(host)
                sw.Stop()
                Dim ips = String.Join(", ", addrs.Take(3).Select(Function(a) a.ToString()))
                AddSniLog($"DNS: {addrs.Length} record(s) in {sw.ElapsedMilliseconds} ms → {ips}")
                If token.IsCancellationRequested Then Throw New OperationCanceledException()

                ' 2) Ping (prefer IPv4; graceful fallback to TCP RTT if ICMP blocked)
                Dim pingMs As Long = -1
                Dim testViaVpn As Boolean = _isConnected AndAlso _connectionManager IsNot Nothing AndAlso _connectionManager.LocalHttpPort > 0

                If testViaVpn Then
                    ' Measure latency via VPN proxy
                    AddSniLog("Ping: measuring latency via VPN proxy...")
                    Try
                        Dim handler As New HttpClientHandler()
                        handler.ServerCertificateCustomValidationCallback = Function(req As HttpRequestMessage, cert As X509Certificate2, ch As X509Chain, errs As SslPolicyErrors) True
                        handler.Proxy = New WebProxy(New Uri($"http://127.0.0.1:{_connectionManager.LocalHttpPort}"))
                        handler.UseProxy = True

                        Using hc As New HttpClient(handler)
                            hc.Timeout = TimeSpan.FromSeconds(5)
                            Dim url = $"https://{host}/"
                            sw.Restart()
                            Using req As New HttpRequestMessage(HttpMethod.Get, url)
                                req.Headers.Add("Range", "bytes=0-0")
                                Try
                                    Using resp = Await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                                        sw.Stop()
                                        pingMs = sw.ElapsedMilliseconds
                                        AddSniLog($"Ping: {pingMs} ms via VPN")
                                    End Using
                                Catch
                                    sw.Stop()
                                    If sw.ElapsedMilliseconds > 0 AndAlso sw.ElapsedMilliseconds < 5000 Then
                                        pingMs = sw.ElapsedMilliseconds
                                        AddSniLog($"Ping: {pingMs} ms via VPN (partial)")
                                    End If
                                End Try
                            End Using
                        End Using
                    Catch ex As Exception
                        AddSniLog($"Ping: VPN test error - {ex.Message}")
                    End Try

                    If pingMs >= 0 Then
                        Dispatcher.Invoke(Sub() SniPingText.Text = $"{pingMs} ms (via VPN)")
                    Else
                        Dispatcher.Invoke(Sub() SniPingText.Text = "Unreachable via VPN")
                    End If
                Else
                    ' Direct connection - try ICMP first, then TCP RTT
                    Try
                        Dim ip4 = addrs.FirstOrDefault(Function(a) a.AddressFamily = Sockets.AddressFamily.InterNetwork)
                        Dim ipToPing = If(ip4 IsNot Nothing, ip4, addrs.FirstOrDefault())
                        If ipToPing IsNot Nothing Then
                            Dim p As New Ping()
                            Dim smallBuf(7) As Byte ' keep payload small to avoid buffer issues
                            sw.Restart()
                            Dim pr = Await p.SendPingAsync(ipToPing, 2500, smallBuf, New PingOptions(64, True))
                            sw.Stop()
                            If pr.Status = IPStatus.Success Then
                                AddSniLog($"Ping: {pr.RoundtripTime} ms to {pr.Address}")
                                pingMs = pr.RoundtripTime
                            Else
                                AddSniLog($"Ping: {pr.Status}")
                            End If
                        Else
                            AddSniLog("Ping: no resolvable address")
                        End If
                    Catch ex As Exception
                        ' Suppress noisy ping exceptions; we'll try TCP RTT below
                        AddSniLog("Ping: ICMP unavailable; trying TCP RTT")
                    End Try

                    If pingMs < 0 Then
                        ' Fallback: measure TCP connect RTT (direct)
                        Try
                            Using tcpProbe As New TcpClient()
                                sw.Restart()
                                Dim connectTask = tcpProbe.ConnectAsync(host, portVal)
                                Dim done = Await Task.WhenAny(connectTask, Task.Delay(3000, token))
                                If done IsNot connectTask Then Throw New TimeoutException("TCP RTT timeout")
                                sw.Stop()
                                pingMs = sw.ElapsedMilliseconds
                                AddSniLog($"TCP RTT: {pingMs} ms (direct)")
                            End Using
                        Catch ex As Exception
                            AddSniLog($"TCP RTT: error - {ex.Message}")
                        End Try
                    End If

                    If token.IsCancellationRequested Then Throw New OperationCanceledException()
                    If pingMs >= 0 Then
                        Dispatcher.Invoke(Sub() SniPingText.Text = $"{pingMs} ms (ICMP/TCP)")
                    Else
                        Dispatcher.Invoke(Sub() SniPingText.Text = "Unreachable/blocked")
                    End If
                End If

                ' 3) TLS handshake with SNI
                AddSniLog("TLS: starting handshake …")
                Dim tlsOk As Boolean = False
                Dim idleOk As Boolean = False
                Dim tlsMs As Long = 0
                Try
                    Dim useVpn = _isConnected AndAlso _connectionManager IsNot Nothing AndAlso _connectionManager.LocalHttpPort > 0
                    Dim proxyPort = If(useVpn, _connectionManager.LocalHttpPort, 0)

                    ' For VPN connections, use HttpClient instead of raw TCP for better compatibility
                    If useVpn Then
                        AddSniLog($"TLS: using VPN proxy at 127.0.0.1:{proxyPort}")
                        Dim handler As New HttpClientHandler()
                        handler.ServerCertificateCustomValidationCallback = Function(req As HttpRequestMessage, cert As X509Certificate2, ch As X509Chain, errs As SslPolicyErrors) True
                        handler.Proxy = New WebProxy(New Uri($"http://127.0.0.1:{proxyPort}"))
                        handler.UseProxy = True
                        handler.SslProtocols = SslProtocols.Tls12 Or SslProtocols.Tls13

                        Try
                            Using hc As New HttpClient(handler)
                                hc.Timeout = TimeSpan.FromSeconds(10)
                                Dim url = $"https://{host}:{portVal}/"
                                sw.Restart()
                                Using req As New HttpRequestMessage(HttpMethod.Head, url)
                                    Dim resp = Await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                                    sw.Stop()
                                    tlsMs = sw.ElapsedMilliseconds
                                    tlsOk = True
                                    AddSniLog($"TLS: handshake OK in {tlsMs} ms via VPN proxy; status={CInt(resp.StatusCode)}")
                                    Dispatcher.Invoke(Sub() SniTlsText.Text = $"OK ({tlsMs} ms)")
                                End Using
                            End Using

                            ' Test idle tolerance
                            Try
                                Await Task.Delay(1500, token)
                                idleOk = True
                                AddSniLog("TLS: idle window tolerated")
                                Dispatcher.Invoke(Sub() SniIdleText.Text = "Tolerated")
                            Catch oce As TaskCanceledException
                                Throw
                            Catch
                                idleOk = False
                                Dispatcher.Invoke(Sub() SniIdleText.Text = "N/A")
                            End Try
                        Catch exVpn As Exception
                            AddSniLog($"TLS: VPN proxy error - {exVpn.Message}")
                            Dispatcher.Invoke(Sub() SniTlsText.Text = "Error")
                        End Try
                    Else
                        ' Direct connection (no VPN)
                        Using tcp As New TcpClient()
                            tcp.ReceiveTimeout = 10000
                            tcp.SendTimeout = 10000

                            Dim connectCts = CancellationTokenSource.CreateLinkedTokenSource(token)
                            Dim connectTask = tcp.ConnectAsync(host, portVal)
                            Dim timeoutTask = Task.Delay(5000, connectCts.Token)
                            Dim done = Await Task.WhenAny(connectTask, timeoutTask)
                            If done Is timeoutTask Then Throw New TimeoutException("TCP connect timeout")
                            connectCts.Cancel()

                            Using ns = tcp.GetStream()
                                ns.ReadTimeout = 10000
                                ns.WriteTimeout = 10000

                                Using ssl As New SslStream(ns, leaveInnerStreamOpen:=False, userCertificateValidationCallback:=Function(sender2, cert, chain, errs) True)
                                    Dim opt As New SslClientAuthenticationOptions With {
                                        .TargetHost = host,
                                        .EnabledSslProtocols = SslProtocols.Tls12 Or SslProtocols.Tls13,
                                        .CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                                    }
                                    sw.Restart()
                                    Await ssl.AuthenticateAsClientAsync(opt, token)
                                    sw.Stop()
                                    tlsMs = sw.ElapsedMilliseconds
                                    tlsOk = ssl.IsAuthenticated AndAlso ssl.IsEncrypted
                                    AddSniLog($"TLS: handshake {(If(tlsOk, "OK", "FAILED"))} in {tlsMs} ms; protocol={ssl.SslProtocol} (direct)")
                                    Dispatcher.Invoke(Sub() SniTlsText.Text = If(tlsOk, $"OK ({tlsMs} ms)", "FAILED"))

                                    ' Leave idle briefly to see if server allows a short tunnel
                                    Try
                                        Await Task.Delay(1500, token)
                                        idleOk = True
                                        AddSniLog("TLS: idle window tolerated (likely tunnel-friendly)")
                                        Dispatcher.Invoke(Sub() SniIdleText.Text = "Tolerated")
                                    Catch oce As TaskCanceledException
                                        Throw
                                    Catch
                                        idleOk = False
                                        Dispatcher.Invoke(Sub() SniIdleText.Text = "Closed quickly")
                                    End Try
                                End Using
                            End Using
                        End Using
                    End If
                Catch ex As Exception
                    AddSniLog($"TLS: error - {ex.Message}")
                    Dispatcher.Invoke(Sub() SniTlsText.Text = "Error")
                End Try
                If token.IsCancellationRequested Then Throw New OperationCanceledException()

                ' 4) Lightweight HTTP probe for speed (optional) + redirect detection
                If tlsOk Then
                    Try
                        Dim url = $"https://{host}:{portVal}/"
                        ' Use direct connection, or VPN proxy if connected
                        Dim handler As New HttpClientHandler()
                        handler.ServerCertificateCustomValidationCallback = Function(req As HttpRequestMessage, cert As X509Certificate2, ch As X509Chain, errs As SslPolicyErrors) True

                        Dim useProxy = _isConnected AndAlso _connectionManager IsNot Nothing AndAlso _connectionManager.LocalHttpPort > 0
                        If useProxy Then
                            handler.Proxy = New WebProxy(New Uri($"http://127.0.0.1:{_connectionManager.LocalHttpPort}"))
                            handler.UseProxy = True
                        End If

                        Try
                            Using hc As New HttpClient(handler)
                                hc.Timeout = TimeSpan.FromSeconds(7)
                                Dim req = New HttpRequestMessage(HttpMethod.Head, url)
                                Dim respHead = Await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                                Dim isRedirect = CInt(respHead.StatusCode) >= 300 AndAlso CInt(respHead.StatusCode) < 400
                                If isRedirect Then
                                    Dim loc = If(respHead.Headers.Location, Nothing)
                                    AddSniLog($"HTTP({If(useProxy, "proxy", "direct")}): redirect {CInt(respHead.StatusCode)} → {If(loc IsNot Nothing, loc.ToString(), "(no location)")}")
                                Else
                                    AddSniLog($"HTTP({If(useProxy, "proxy", "direct")}): status {CInt(respHead.StatusCode)}")
                                End If
                                Dim bufSize = 64 * 1024
                                Dim buffer(bufSize - 1) As Byte
                                sw.Restart()
                                Using resp = Await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                                    Dim stream = Await resp.Content.ReadAsStreamAsync(token)
                                    Dim totalRead As Long = 0
                                    Dim read As Integer
                                    Do
                                        read = Await stream.ReadAsync(buffer, 0, buffer.Length, token)
                                        totalRead += read
                                        If totalRead >= 128 * 1024 OrElse read = 0 Then Exit Do
                                    Loop
                                End Using
                                sw.Stop()
                                Dim secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001)
                                Dim kbps = CInt((128.0 / secs) * 8)
                                AddSniLog($"HTTP({If(useProxy, "proxy", "direct")}): first-bytes {CInt(secs * 1000)} ms, ~{kbps} kbps")
                                Dispatcher.Invoke(Sub() SniSpeedText.Text = $"~{kbps} kbps (rough)")
                            End Using
                        Catch exProbe As Exception
                            AddSniLog($"HTTP({If(useProxy, "proxy", "direct")}): probe error - {exProbe.Message}")
                            Dispatcher.Invoke(Sub() SniSpeedText.Text = "Error")
                        End Try
                    Catch ex As Exception
                        AddSniLog($"HTTP: unexpected probe error - {ex.Message}")
                        Dispatcher.Invoke(Sub() SniSpeedText.Text = "Error")
                    End Try
                End If

                ' 5) Classification and marks
                Dim marks As New List(Of String)
                If host.EndsWith("aka.ms", StringComparison.OrdinalIgnoreCase) OrElse host.Equals("aka.ms", StringComparison.OrdinalIgnoreCase) Then
                    marks.Add("✅ aka.ms-class (Azure Front Door redirector)")
                End If
                Dim socialList = New String() {"facebook.com", "fb.com", "instagram.com", "whatsapp.com", "tiktok.com", "x.com", "twitter.com", "youtube.com", "google.com"}
                If socialList.Any(Function(s) host.EndsWith(s, StringComparison.OrdinalIgnoreCase)) Then
                    marks.Add("ℹ Social-media SNI")
                End If
                If tlsOk Then marks.Add("TLS OK") Else marks.Add("TLS FAILED")
                If idleOk Then marks.Add("Idle tolerated")
                AddSniLog("Mark: " & String.Join(" | ", marks))
                Dispatcher.Invoke(Sub() SniClassText.Text = String.Join(" | ", marks))
                ' Compute overall verdict & badge colors
                Dispatcher.Invoke(Sub()
                                      Dim verdict As String
                                      Dim okTls = marks.Any(Function(m) m.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                                      Dim okPing = Not (SniPingText.Text.Contains("Unreachable") OrElse SniPingText.Text.Contains("blocked"))
                                      Dim okSpeed = marks.Any(Function(m) m.Contains("Speed", StringComparison.OrdinalIgnoreCase) OrElse m.Contains("Throughput", StringComparison.OrdinalIgnoreCase)) OrElse SniSpeedText.Text.Contains("kbps") OrElse SniSpeedText.Text.Contains("Mbps")
                                      verdict = If(okTls AndAlso okPing, If(okSpeed, "Suitable for tunneling and moderate throughput.", "Suitable for tunneling; limited speed visibility."), "Not reliable for tunneling.")
                                      SniClassText.Text = SniClassText.Text & Environment.NewLine & verdict
                                      ' Helper local function for color
                                      Dim green = New Media.SolidColorBrush(Media.Color.FromRgb(40, 167, 69))
                                      Dim red = New Media.SolidColorBrush(Media.Color.FromRgb(220, 53, 69))
                                      Dim amber = New Media.SolidColorBrush(Media.Color.FromRgb(255, 193, 7))
                                      SniBadgeConnectivity.Background = If(okPing, green, red)
                                      SniBadgeTLS.Background = If(okTls, green, red)
                                      ' Idle classification (renamed to avoid shadowing outer idleOk variable)
                                      Dim idleBadgeOk = SniIdleText.Text.Contains("OK") OrElse SniIdleText.Text.Contains("Accept", StringComparison.OrdinalIgnoreCase)
                                      SniBadgeIdle.Background = If(idleBadgeOk, green, amber)
                                      ' Redirect
                                      Dim redirectSupported = SniSpeedText.Text.Contains("redirect", StringComparison.OrdinalIgnoreCase)
                                      SniBadgeRedirect.Background = If(redirectSupported, green, amber)
                                      SniBadgeSpeed.Background = If(okSpeed, green, amber)
                                      SniOverallBorder.Background = If(okTls AndAlso okPing, New Media.SolidColorBrush(Media.Color.FromRgb(224, 247, 250)), New Media.SolidColorBrush(Media.Color.FromRgb(252, 235, 234)))
                                  End Sub)

                ' Inline SNI status
                Dispatcher.Invoke(Sub()
                                      If tlsOk Then
                                          SniInlineStatusText.Text = $"SNI OK • TLS {tlsMs} ms • Idle {(If(idleOk, "OK", "No"))}"
                                          SniInlineStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                                      Else
                                          SniInlineStatusText.Text = "SNI FAILED"
                                          SniInlineStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                                      End If
                                  End Sub)

                AddSniLog("Done.")
            Catch oce As OperationCanceledException
                AddSniLog("Stopped by user.")
            Catch ex As Exception
                AddSniLog($"Error: {ex.Message}")
            Finally
                SniCheckButton.IsEnabled = True
                SniStopButton.IsEnabled = False
            End Try
        End Sub
        ' ============================
        ' Speed Test: UI handlers
        ' ============================
        Private Async Sub SpeedStartButton_Click(sender As Object, e As RoutedEventArgs)
            SpeedStartButton.IsEnabled = False
            SpeedStopButton.IsEnabled = True

            ' Reset summary
            Dispatcher.Invoke(Sub()
                                  SpeedPingText.Text = "-"
                                  SpeedDownloadText.Text = "-"
                                  SpeedUploadText.Text = "-"
                                  SpeedGamingText.Text = "-"
                                  SpeedStreamingText.Text = "-"
                                  SpeedBrowsingText.Text = "-"
                              End Sub)

            _speedCts?.Dispose()
            _speedCts = New CancellationTokenSource()
            Dim token = _speedCts.Token

            Dim pingHost = If(SpeedPingHostTextBox?.Text, "1.1.1.1").Trim()
            Dim durationSec As Integer = 8
            Integer.TryParse(If(SpeedDurationTextBox?.Text, "8"), durationSec)
            If durationSec <= 0 Then durationSec = 8

            Dim useVpn As Boolean = _isConnected AndAlso _connectionManager IsNot Nothing AndAlso _connectionManager.LocalHttpPort > 0
            Dim proxyPort As Integer = If(useVpn, _connectionManager.LocalHttpPort, 0)

            Try
                ' 1) Latency: TCP RTT (more reliable than ICMP)
                Dim pingMs As Long = -1
                AddLog($"[Speed] Measuring TCP latency to {pingHost}:443...")
                Try
                    If useVpn Then
                        ' Use HttpClient for VPN connections - measure connection establishment only
                        Dim handler As New HttpClientHandler()
                        handler.ServerCertificateCustomValidationCallback = Function(req As HttpRequestMessage, cert As System.Security.Cryptography.X509Certificates.X509Certificate2, ch As System.Security.Cryptography.X509Certificates.X509Chain, errs As System.Net.Security.SslPolicyErrors) True
                        handler.Proxy = New WebProxy(New Uri($"http://127.0.0.1:{proxyPort}"))
                        handler.UseProxy = True
                        handler.MaxConnectionsPerServer = 1

                        Using hc As New HttpClient(handler)
                            hc.Timeout = TimeSpan.FromSeconds(5)
                            Dim url = $"https://{pingHost}/"
                            Dim sw = Stopwatch.StartNew()

                            ' Use a very small request to minimize data transfer time
                            Using req As New HttpRequestMessage(HttpMethod.Get, url)
                                req.Headers.Add("Range", "bytes=0-0") ' Request only 1 byte
                                Try
                                    Using resp = Await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                                        ' Just getting headers is enough for latency
                                        sw.Stop()
                                        pingMs = sw.ElapsedMilliseconds
                                        AddLog($"[Speed] TCP latency via VPN: {pingMs} ms")
                                    End Using
                                Catch
                                    sw.Stop()
                                    ' Even if request fails, we got latency measurement
                                    If sw.ElapsedMilliseconds > 0 AndAlso sw.ElapsedMilliseconds < 5000 Then
                                        pingMs = sw.ElapsedMilliseconds
                                        AddLog($"[Speed] TCP latency via VPN: {pingMs} ms (partial)")
                                    End If
                                End Try
                            End Using
                        End Using
                    Else
                        ' Direct TCP connection
                        Using tcp As New TcpClient()
                            tcp.ReceiveTimeout = 3000
                            tcp.SendTimeout = 3000
                            Dim sw = Stopwatch.StartNew()
                            Dim connectTask = tcp.ConnectAsync(pingHost, 443)
                            Dim timeoutTask = Task.Delay(3000, token)
                            Dim completed = Await Task.WhenAny(connectTask, timeoutTask)
                            sw.Stop()
                            If completed Is connectTask AndAlso Not connectTask.IsFaulted Then
                                pingMs = sw.ElapsedMilliseconds
                                AddLog($"[Speed] TCP latency (direct): {pingMs} ms")
                            Else
                                AddLog($"[Speed] TCP connection timeout or failed")
                            End If
                        End Using
                    End If
                Catch ex As Exception
                    AddLog($"[Speed] TCP latency test failed: {ex.Message}")
                End Try

                If pingMs >= 0 Then
                    Dispatcher.Invoke(Sub() SpeedPingText.Text = $"{pingMs} ms (TCP)")
                Else
                    Dispatcher.Invoke(Sub() SpeedPingText.Text = "Unreachable")
                End If

                ' 2) Download throughput
                Dim dlMbps As Double = Await RunDownloadTestAsync(durationSec, useVpn, proxyPort, token)
                If dlMbps >= 0 Then
                    Dispatcher.Invoke(Sub() SpeedDownloadText.Text = $"{dlMbps:F1} Mbps")
                Else
                    Dispatcher.Invoke(Sub() SpeedDownloadText.Text = "Error")
                End If

                ' 3) Upload throughput
                Dim ulMbps As Double = Await RunUploadTestAsync(durationSec, useVpn, proxyPort, token)
                If ulMbps >= 0 Then
                    Dispatcher.Invoke(Sub() SpeedUploadText.Text = $"{ulMbps:F1} Mbps")
                Else
                    Dispatcher.Invoke(Sub() SpeedUploadText.Text = "Error")
                End If

                ' 4) Qualitative statuses
                Dim gaming = If(pingMs < 0, "Poor", If(pingMs <= 60, "Good", If(pingMs <= 120, "OK", "Poor")))
                Dim streaming = If(dlMbps >= 25, "4K Good", If(dlMbps >= 5, "HD OK", "Poor"))
                Dim browsing = If((pingMs <= 100 OrElse pingMs < 0 = False) AndAlso dlMbps >= 10, "Fast", If(dlMbps >= 3, "OK", "Slow"))
                Dispatcher.Invoke(Sub()
                                      SpeedGamingText.Text = gaming
                                      SpeedStreamingText.Text = streaming
                                      SpeedBrowsingText.Text = browsing
                                      ' Overall quality and badge colors
                                      Dim overall As String
                                      Dim green = New Media.SolidColorBrush(Media.Color.FromRgb(40, 167, 69))
                                      Dim red = New Media.SolidColorBrush(Media.Color.FromRgb(220, 53, 69))
                                      Dim amber = New Media.SolidColorBrush(Media.Color.FromRgb(255, 193, 7))
                                      ' Badge helpers
                                      Dim SetBadge = Sub(b As Controls.Border, status As String)
                                                         If status.Contains("Good") OrElse status.Contains("Fast") OrElse status.Contains("4K") Then
                                                             b.Background = green
                                                         ElseIf status.Contains("OK") OrElse status.Contains("HD") Then
                                                             b.Background = amber
                                                         Else
                                                             b.Background = red
                                                         End If
                                                     End Sub
                                      SetBadge(SpeedBadgePing, SpeedPingText.Text)
                                      SetBadge(SpeedBadgeDownload, SpeedDownloadText.Text)
                                      SetBadge(SpeedBadgeUpload, SpeedUploadText.Text)
                                      SetBadge(SpeedBadgeGaming, gaming)
                                      SetBadge(SpeedBadgeStreaming, streaming)
                                      SetBadge(SpeedBadgeBrowsing, browsing)
                                      overall = If(gaming = "Good" AndAlso streaming.Contains("4K") AndAlso browsing = "Fast", "Excellent for all activities.", If(streaming.Contains("HD") AndAlso browsing <> "Slow", "Good general performance; HD streaming OK.", "Basic connectivity; limited for heavy use."))
                                      SpeedOverallText.Text = overall
                                      SpeedOverallBorder.Background = If(overall.StartsWith("Excellent"), New Media.SolidColorBrush(Media.Color.FromRgb(224, 247, 250)), If(overall.StartsWith("Good"), New Media.SolidColorBrush(Media.Color.FromRgb(255, 249, 196)), New Media.SolidColorBrush(Media.Color.FromRgb(252, 235, 234))))
                                  End Sub)

            Catch ex As Exception
                AddLog($"[Speed] Error: {ex.Message}", True)
            Finally
                SpeedStartButton.IsEnabled = True
                SpeedStopButton.IsEnabled = False
            End Try
        End Sub

        Private Sub SpeedStopButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                _speedCts?.Cancel()
            Catch
            End Try
        End Sub

        Private Async Function RunDownloadTestAsync(durationSec As Integer, useVpn As Boolean, proxyPort As Integer, token As Threading.CancellationToken) As Task(Of Double)
            Dim urls = New List(Of String) From {
                "https://speed.cloudflare.com/__down?bytes=50000000",
                "https://speed.hetzner.de/100MB.bin",
                "https://speed.hetzner.de/10MB.bin"
            }
            For Each url In urls
                Try
                    ' Use direct connection only when VPN is not connected
                    Dim handler As New HttpClientHandler()
                    handler.ServerCertificateCustomValidationCallback = Function(req As HttpRequestMessage, cert As System.Security.Cryptography.X509Certificates.X509Certificate2, ch As System.Security.Cryptography.X509Certificates.X509Chain, errs As System.Net.Security.SslPolicyErrors) True

                    If useVpn AndAlso proxyPort > 0 Then
                        ' Use VPN proxy
                        handler.Proxy = New WebProxy(New Uri($"http://127.0.0.1:{proxyPort}"))
                        handler.UseProxy = True
                    End If

                    Try
                        Using hc As New HttpClient(handler)
                            hc.Timeout = TimeSpan.FromSeconds(Math.Max(durationSec + 3, 10))
                            Using resp = Await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                                resp.EnsureSuccessStatusCode()
                                Dim stream = Await resp.Content.ReadAsStreamAsync(token)
                                Dim buf(64 * 1024 - 1) As Byte
                                Dim sw = Stopwatch.StartNew()
                                Dim total As Long = 0
                                While sw.Elapsed.TotalSeconds < durationSec AndAlso Not token.IsCancellationRequested
                                    Dim n = Await stream.ReadAsync(buf, 0, buf.Length, token)
                                    If n <= 0 Then Exit While
                                    total += n
                                End While
                                Dim secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001)
                                Dim mbps = (total * 8.0 / 1_000_000.0) / secs
                                AddLog($"[Speed][DL] Download test completed: {mbps:F1} Mbps via {If(useVpn, "VPN", "direct")}")
                                Return mbps
                            End Using
                        End Using
                    Catch exInner As Exception
                        AddLog($"[Speed][DL] {If(useVpn, "VPN", "direct")} attempt failed: {exInner.Message}")
                    End Try
                Catch
                    Continue For
                End Try
            Next
            Return -1
        End Function

        Private Async Function RunUploadTestAsync(durationSec As Integer, useVpn As Boolean, proxyPort As Integer, token As Threading.CancellationToken) As Task(Of Double)
            Dim urls = New List(Of String) From {
                "https://speed.cloudflare.com/__up",
                "https://httpbin.org/post"
            }
            Dim payload(128 * 1024 - 1) As Byte ' 128KB chunk (reduced for better performance via VPN)
            Dim rnd As New Random()
            rnd.NextBytes(payload)
            For Each url In urls
                Try
                    Dim handler As New HttpClientHandler()
                    handler.ServerCertificateCustomValidationCallback = Function(req As HttpRequestMessage, cert As System.Security.Cryptography.X509Certificates.X509Certificate2, ch As System.Security.Cryptography.X509Certificates.X509Chain, errs As System.Net.Security.SslPolicyErrors) True

                    If useVpn AndAlso proxyPort > 0 Then
                        ' Use VPN proxy
                        handler.Proxy = New WebProxy(New Uri($"http://127.0.0.1:{proxyPort}"))
                        handler.UseProxy = True
                    End If

                    Try
                        Using hc As New HttpClient(handler)
                            ' Increase timeout for VPN connections (uploads can be slower)
                            Dim timeoutSec = If(useVpn, Math.Max(durationSec * 2 + 10, 30), Math.Max(durationSec + 3, 10))
                            hc.Timeout = TimeSpan.FromSeconds(timeoutSec)

                            Dim sw = Stopwatch.StartNew()
                            Dim total As Long = 0
                            Dim uploadCount As Integer = 0

                            While sw.Elapsed.TotalSeconds < durationSec AndAlso Not token.IsCancellationRequested
                                Try
                                    Using content As New ByteArrayContent(payload)
                                        content.Headers.ContentType = New Headers.MediaTypeHeaderValue("application/octet-stream")
                                        Dim resp = Await hc.PostAsync(url, content, token)
                                        resp.EnsureSuccessStatusCode()
                                        total += payload.Length
                                        uploadCount += 1
                                    End Using
                                Catch uploadEx As Exception
                                    AddLog($"[Speed][UL] Upload chunk error: {uploadEx.Message}")
                                    If uploadCount = 0 Then Throw ' Re-throw if first upload fails
                                    Exit While ' Exit loop if we had at least one successful upload
                                End Try
                            End While

                            If total > 0 Then
                                Dim secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001)
                                Dim mbps = (total * 8.0 / 1_000_000.0) / secs
                                AddLog($"[Speed][UL] Upload test completed: {mbps:F1} Mbps ({uploadCount} chunks, {total / 1024 / 1024:F1} MB) via {If(useVpn, "VPN", "direct")}")
                                Return mbps
                            Else
                                Throw New Exception("No data uploaded")
                            End If
                        End Using
                    Catch exInner As Exception
                        AddLog($"[Speed][UL] {If(useVpn, "VPN", "direct")} attempt failed: {exInner.Message}")
                    End Try
                Catch
                    Continue For
                End Try
            Next
            Return -1
        End Function

        Private Sub SniStopButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                _sniCts?.Cancel()
            Catch
            End Try
        End Sub

        Private Sub OnWindowClosing(sender As Object, e As ComponentModel.CancelEventArgs)
            Try
                ' Ensure all resources are released and system settings restored
                AddLog("Cleaning up before exit...")
                _connectionManager.Disconnect()
                If _vpnShareManager IsNot Nothing AndAlso _vpnShareManager.IsRunning Then
                    _vpnShareManager.StopShare()
                End If
                If _hotspotManager IsNot Nothing AndAlso _hotspotManager.IsRunning Then
                    _hotspotManager.StopHotspot()
                End If
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Load configurations - either from cache or fresh from web
        ''' </summary>
        Private Async Function LoadConfigurationAsync(Optional useCache As Boolean = True, Optional freshResult As ConfigurationResult = Nothing) As Task
            Try
                ' Initialize as empty lists instead of Nothing to avoid null reference issues
                Dim vlessLinks As List(Of String) = New List(Of String)
                Dim sshConfigs As List(Of SSHTLSConfig) = New List(Of SSHTLSConfig)

                If freshResult IsNot Nothing Then
                    ' Use provided fresh result (from LOAD button)
                    vlessLinks = freshResult.VLessLinks
                    sshConfigs = freshResult.SSHConfigs
                    AddLog($"✓ Using freshly fetched {vlessLinks.Count} VLESS and {sshConfigs.Count} SSH configuration(s)")

                    ' Save online SSH configs to persistent storage
                    If sshConfigs IsNot Nothing AndAlso sshConfigs.Count > 0 Then
                        AddLog($"Saving {sshConfigs.Count} online SSH config(s) to local storage...")
                        _updateChecker.SaveOnlineSSHConfigs(sshConfigs)
                        AddLog("✓ Online SSH configs saved for offline use")

                        ' Verify they were saved
                        Dim verifyLoad = _updateChecker.LoadOnlineSSHConfigs()
                        AddLog($"✓ Verified: {verifyLoad.Count} online SSH config(s) in storage")
                    Else
                        ' If no SSH configs from server, clear saved online configs
                        AddLog("No SSH configs from server - clearing saved online configs...")
                        _updateChecker.SaveOnlineSSHConfigs(New List(Of SSHTLSConfig))
                        AddLog("✓ Cleared saved online configs")
                    End If

                    OnlineConfigStatus.Text = "Using fresh configurations from server"
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                ElseIf useCache Then
                    ' Load from cache first (startup/offline mode)
                    AddLog("Loading configurations from offline cache...")
                    OnlineConfigStatus.Text = "Loading offline configurations..."
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7))

                    ' Load VLESS configs from cache
                    Dim cachedVless = _updateChecker.LoadCachedConfigs()
                    If cachedVless IsNot Nothing Then
                        vlessLinks = cachedVless
                    End If
                    AddLog($"Loaded {vlessLinks.Count} VLESS config(s) from cache")

                    ' Load online SSH configs from cache
                    Dim cachedSSH = _updateChecker.LoadOnlineSSHConfigs()
                    If cachedSSH IsNot Nothing Then
                        sshConfigs = cachedSSH
                    End If
                    AddLog($"Loaded {sshConfigs.Count} online SSH config(s) from cache")

                    ' Log details of loaded SSH configs
                    If sshConfigs.Count > 0 Then
                        For Each cfg In sshConfigs
                            AddLog($"  Cached SSH: {cfg.Tag} (Host: {cfg.Host}, Port: {cfg.Port}, IsOnline: {cfg.IsOnlineConfig})")
                        Next
                    End If

                    If vlessLinks.Count > 0 OrElse sshConfigs.Count > 0 Then
                        OnlineConfigStatus.Text = "Loaded from offline cache (Click LOAD to update)"
                        OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Blue)
                        AddLog($"✓ Total from cache: {vlessLinks.Count} VLESS and {sshConfigs.Count} SSH configuration(s)")
                    Else
                        ' No cached configs, try to fetch from web
                        AddLog("No offline cache found. Attempting to fetch from web...")
                        OnlineConfigStatus.Text = "Fetching from web..."
                        OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7))

                        Try
                            Dim fetcher As New ConfigFetcher()
                            Dim result = Await fetcher.FetchConfigurationsAsync(forceRefresh:=False)
                            vlessLinks = result.VLessLinks
                            sshConfigs = result.SSHConfigs
                            If vlessLinks.Count > 0 OrElse sshConfigs.Count > 0 Then
                                AddLog($"✓ Fetched {vlessLinks.Count} VLESS and {sshConfigs.Count} SSH configuration(s) from web")
                                _updateChecker.SaveConfigsLocally(vlessLinks)
                                AddLog($"Saving {sshConfigs.Count} SSH config(s) during startup fallback...")
                                _updateChecker.SaveOnlineSSHConfigs(sshConfigs)
                                AddLog("✓ SSH configs saved during startup fallback")
                            End If
                        Catch ex As Exception
                            AddLog($"⚠ Failed to fetch from web: {ex.Message}", True)
                            OnlineConfigStatus.Text = "Offline - No cached configurations available"
                            OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                            vlessLinks = New List(Of String)()
                            sshConfigs = New List(Of SSHTLSConfig)()
                        End Try
                    End If
                Else
                    ' Force fetch fresh from web
                    AddLog("Fetching fresh configurations from web...")
                    OnlineConfigStatus.Text = "Fetching latest from server..."
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7))

                    Dim fetcher As New ConfigFetcher()
                    Dim result = Await fetcher.FetchConfigurationsAsync(forceRefresh:=True)
                    vlessLinks = result.VLessLinks
                    sshConfigs = result.SSHConfigs
                    AddLog($"✓ Fetched {vlessLinks.Count} VLESS and {sshConfigs.Count} SSH fresh configuration(s) from server")
                    _updateChecker.SaveConfigsLocally(vlessLinks)
                    AddLog($"Saving {sshConfigs.Count} SSH config(s) from force refresh...")
                    _updateChecker.SaveOnlineSSHConfigs(sshConfigs)
                    AddLog("✓ SSH configs saved from force refresh")
                End If

                ' Clear and rebuild configuration list
                _availableConfigs.Clear()

                ' Add VLESS configs
                If vlessLinks IsNot Nothing AndAlso vlessLinks.Count > 0 Then
                    For i As Integer = 0 To vlessLinks.Count - 1
                        Try
                            Dim config = VLessParser.ParseVLessLink(vlessLinks(i))
                            Dim displayName As String = If(Not String.IsNullOrEmpty(config.Tag), config.Tag, $"VLESS {i + 1} - {config.Host}:{config.Port}")
                            _availableConfigs.Add(New ServerConfigItem With {.DisplayName = displayName, .Config = config})
                            AddLog($"  → {displayName}")
                        Catch ex As Exception
                            AddLog($"⚠ Failed to parse VLESS config {i + 1}: {ex.Message}", True)
                            Continue For
                        End Try
                    Next
                End If

                ' Add SSH configs (online configs)
                If sshConfigs IsNot Nothing AndAlso sshConfigs.Count > 0 Then
                    AddLog($"Adding {sshConfigs.Count} SSH config(s) to available configs list...")
                    For Each sshConfig In sshConfigs
                        Try
                            ' Ensure it's marked as online config
                            If Not sshConfig.IsOnlineConfig Then
                                sshConfig.IsOnlineConfig = True
                                AddLog($"  Marked {sshConfig.Tag} as online config")
                            End If

                            _availableConfigs.Add(New ServerConfigItem With {
                        .DisplayName = sshConfig.Tag,
                        .Config = sshConfig
                    })
                            AddLog($"  → {sshConfig.Tag}")
                        Catch ex As Exception
                            AddLog($"⚠ Failed to add SSH config: {ex.Message}", True)
                        End Try
                    Next
                    AddLog($"✓ Added {sshConfigs.Count} SSH config(s) to UI")
                Else
                    AddLog("No SSH configs to add to UI (list is empty or null)")
                End If

                ' Update UI
                AddLog($"Updating ServerConfigComboBox with {_availableConfigs.Count} config(s)...")
                ServerConfigComboBox.ItemsSource = Nothing
                ServerConfigComboBox.ItemsSource = _availableConfigs
                AddLog($"ServerConfigComboBox ItemsSource set")

                If _availableConfigs.Count > 0 Then
                    Dim savedIndex = _updateChecker.LoadSelectedConfigIndex()
                    AddLog($"Saved index from previous session: {savedIndex}")

                    If savedIndex >= 0 AndAlso savedIndex < _availableConfigs.Count Then
                        ServerConfigComboBox.SelectedIndex = savedIndex
                        AddLog($"Selected index {savedIndex}: {_availableConfigs(savedIndex).DisplayName}")
                    Else
                        ServerConfigComboBox.SelectedIndex = 0
                        AddLog($"Selected default index 0: {_availableConfigs(0).DisplayName}")
                    End If

                    If freshResult IsNot Nothing Then
                        OnlineConfigStatus.Text = $"✓ {_availableConfigs.Count} fresh configuration(s) loaded from server"
                        OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    ElseIf useCache Then
                        OnlineConfigStatus.Text = $"{_availableConfigs.Count} configuration(s) from cache (Click LOAD to update)"
                        OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Blue)
                    End If

                    AddLog($"✓ Total configurations available: {_availableConfigs.Count}")
                Else
                    OnlineConfigStatus.Text = "No configurations available - Click LOAD to fetch from server"
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange)
                    AddLog("⚠ No configurations available - Click LOAD button or use Custom tabs", True)
                End If
            Catch ex As Exception
                OnlineConfigStatus.Text = $"Error: {ex.Message}"
                OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                AddLog($"✗ Configuration load error: {ex.Message}", True)
            End Try
        End Function

        Private Sub ServerConfigComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Try
                If ServerConfigComboBox.SelectedItem IsNot Nothing Then
                    Dim selectedItem = DirectCast(ServerConfigComboBox.SelectedItem, ServerConfigItem)
                    _currentConfig = selectedItem.Config
                    AddLog($"Selected: {selectedItem.DisplayName}")

                    ' Preserve SNI from fetched config; only apply stored user override if config lacks SNI
                    If TypeOf _currentConfig Is VLessConfig Then
                        Dim vlessConfig = DirectCast(_currentConfig, VLessConfig)
                        Dim userSNI = _updateChecker.LoadUserSNI()
                        If String.IsNullOrEmpty(vlessConfig.SNI) AndAlso Not String.IsNullOrEmpty(userSNI) Then
                            vlessConfig.SNI = userSNI ' fallback only
                            AddLog("Using saved user SNI override (config had none).")
                        End If
                    End If

                    DisplayConfiguration(_currentConfig)
                    _updateChecker.SaveSelectedConfigIndex(ServerConfigComboBox.SelectedIndex)
                End If
            Catch ex As Exception
                AddLog($"✗ Selection error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub DisplayConfiguration(config As Object)
            Try
                If TypeOf config Is VLessConfig Then
                    Dim vlessConfig = DirectCast(config, VLessConfig)
                    HostTextBox.Text = If(vlessConfig.Host, "")
                    PortTextBox.Text = vlessConfig.Port.ToString()
                    SecurityTextBox.Text = If(vlessConfig.Security, "")
                    SNITextBox.Text = If(String.IsNullOrEmpty(vlessConfig.SNI), "", vlessConfig.SNI)
                    SNITextBox.IsReadOnly = False
                    TransportTextBox.Text = If(vlessConfig.TransportType, "")
                    OnlineProtocolText.Text = "VLESS"
                    OnlineConfigStatus.Text = "Configuration loaded"
                ElseIf TypeOf config Is SSHTLSConfig Then
                    Dim sshConfig = DirectCast(config, SSHTLSConfig)
                    ' For online SSH configs - HIDE credentials but allow SNI editing
                    If sshConfig.IsOnlineConfig Then
                        HostTextBox.Text = "●●●●●●●●"
                        PortTextBox.Text = "●●●"
                        SecurityTextBox.Text = "SSH+TLS (Online)"
                        SNITextBox.Text = If(String.IsNullOrEmpty(sshConfig.SNI), "", sshConfig.SNI)
                        SNITextBox.IsReadOnly = False ' Allow editing SNI for online configs
                        TransportTextBox.Text = "SSH Tunnel"
                        OnlineConfigStatus.Text = $"SSH configuration loaded - SNI: {If(String.IsNullOrEmpty(sshConfig.SNI), "(not set - will use host)", sshConfig.SNI)}"
                    Else
                        ' User-saved configs can show details
                        HostTextBox.Text = sshConfig.Host
                        PortTextBox.Text = sshConfig.Port.ToString()
                        SecurityTextBox.Text = "SSH+TLS (Custom)"
                        SNITextBox.Text = If(String.IsNullOrEmpty(sshConfig.SNI), "", sshConfig.SNI)
                        SNITextBox.IsReadOnly = False
                        TransportTextBox.Text = "SSH Tunnel"
                        OnlineConfigStatus.Text = "Custom SSH configuration loaded"
                    End If
                    OnlineProtocolText.Text = "SSH+TLS"
                End If

                _currentConfig = config
            Catch ex As Exception
                AddLog($"✗ Display error: {ex.Message}", True)
            End Try
        End Sub

        Private Async Sub OnConfigurationUpdated(config As VLessConfig)
            Try
                AddLog("✓ Configuration update event received")
                Await Dispatcher.InvokeAsync(Async Function() As Task
                                                 Await LoadConfigurationAsync(useCache:=False)
                                                 OnlineConfigStatus.Text = "Configurations updated from server!"
                                                 OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                                             End Function)
            Catch ex As Exception
                AddLog($"✗ Configuration update error: {ex.Message}", True)
            End Try
        End Sub

        Private Async Sub ConnectButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                ' Reset UI session logs and traffic counters for a fresh connection
                LogTextBox.Clear()
                OnlineDownloadedText.Text = "0 B"
                OnlineUploadedText.Text = "0 B"
                SSHDownloadedText.Text = "0 B"
                SSHUploadedText.Text = "0 B"
                CustomDownloadedText.Text = "0 B"
                CustomUploadedText.Text = "0 B"
                AddLog("=== 🚀 Connection Attempt Started ===")

                Dim activeTab = MainTabControl.SelectedIndex
                Dim isSSHCustom = (activeTab = 1)
                Dim isCustomVless = (activeTab = 2)

                If isCustomVless Then
                    ' Custom VLESS/VMESS tab
                    Dim config = GetCustomConfigFromUI()
                    If config Is Nothing Then
                        MessageBox.Show("Please import or configure a VLESS/VMESS configuration first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                        Exit Sub
                    End If

                    If Not VLessParser.ValidateConfig(config) Then
                        MessageBox.Show("Invalid configuration. Please check all required fields.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                        Exit Sub
                    End If

                    _currentConfig = config
                    CustomConfigStatusText.Text = $"Connecting to {config.Host}..."
                ElseIf isSSHCustom Then
                    Dim host = SSHHostTextBox.Text?.Trim()
                    Dim portVal As Integer
                    Integer.TryParse(SSHPortTextBox.Text?.Trim(), portVal)
                    Dim user = SSHUsernameTextBox.Text?.Trim()
                    Dim sni = SSHSNITextBox.Text?.Trim()

                    If String.IsNullOrWhiteSpace(host) OrElse portVal <= 0 OrElse String.IsNullOrWhiteSpace(user) Then
                        MessageBox.Show("Please provide Host, Port, and Username for SSH connection.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                        Exit Sub
                    End If

                    ' Check authentication method
                    Dim useKeyAuth = UseSSHKeyCheck.IsChecked.GetValueOrDefault(False)
                    Dim pass = SSHPasswordBox.Password
                    Dim keyPath = SSHKeyPathTextBox.Text?.Trim()
                    Dim passphrase = SSHKeyPassphraseBox.Password

                    If useKeyAuth Then
                        If String.IsNullOrWhiteSpace(keyPath) OrElse Not IO.File.Exists(keyPath) Then
                            MessageBox.Show("Please select a valid private key file.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                            Exit Sub
                        End If
                    Else
                        If String.IsNullOrWhiteSpace(pass) Then
                            MessageBox.Show("Please provide a password.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                            Exit Sub
                        End If
                    End If

                    Dim label As String = $"{host}:{portVal} - {user}"
                    Dim sshConfig As New SSHTLSConfig With {
                        .Host = host,
                        .Port = portVal,
                        .Username = user,
                        .Password = If(useKeyAuth, "", pass),
                        .UseKeyAuth = useKeyAuth,
                        .PrivateKeyPath = If(useKeyAuth, keyPath, ""),
                        .Passphrase = If(useKeyAuth, passphrase, ""),
                        .SNI = sni,
                        .Tag = label,
                        .LocalPort = 0,
                        .UseTLS = False,
                        .IsOnlineConfig = False
                    }

                    ' Get transport type
                    Dim transportItem = TryCast(SSHTransportComboBox.SelectedItem, ComboBoxItem)
                    If transportItem IsNot Nothing AndAlso transportItem.Tag IsNot Nothing Then
                        sshConfig.TransportType = transportItem.Tag.ToString()
                    End If

                    _currentConfig = sshConfig
                    SSHConfigStatusText.Text = $"Connecting to {host}..."
                Else
                    If ServerConfigComboBox.SelectedItem Is Nothing Then
                        AddLog("✗ ERROR: No configuration selected", True)
                        MessageBox.Show("No configuration available. Please click LOAD button to fetch configurations from server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
                        Exit Sub
                    End If
                    _currentConfig = DirectCast(ServerConfigComboBox.SelectedItem, ServerConfigItem).Config

                    If TypeOf _currentConfig Is VLessConfig Then
                        Dim vlessConfig = DirectCast(_currentConfig, VLessConfig)
                        ' Apply SNI from textbox before connecting
                        If SNITextBox.Text <> vlessConfig.SNI Then
                            vlessConfig.SNI = SNITextBox.Text
                            _updateChecker.SaveUserSNI(vlessConfig.SNI)
                        End If
                        OnlineConfigStatus.Text = $"Connecting to {vlessConfig.Host}..."
                    ElseIf TypeOf _currentConfig Is SSHTLSConfig Then
                        Dim sshConfig = DirectCast(_currentConfig, SSHTLSConfig)
                        ' Apply SNI from textbox before connecting
                        Dim currentSNI = SNITextBox.Text?.Trim()
                        If currentSNI <> sshConfig.SNI Then
                            sshConfig.SNI = currentSNI
                            ' Save updated SNI for online configs
                            If sshConfig.IsOnlineConfig Then
                                Dim onlineConfigs = _updateChecker.LoadOnlineSSHConfigs()
                                Dim existingConfig = onlineConfigs.FirstOrDefault(Function(c) c.Host = sshConfig.Host AndAlso c.Port = sshConfig.Port AndAlso c.Username = sshConfig.Username)
                                If existingConfig IsNot Nothing Then
                                    existingConfig.SNI = currentSNI
                                    _updateChecker.SaveOnlineSSHConfigs(onlineConfigs)
                                    AddLog($"✓ Updated SNI for connection: {currentSNI}")
                                End If
                            End If
                        End If
                        AddLog($"Connecting with SNI: {If(String.IsNullOrWhiteSpace(sshConfig.SNI), "(using host)", sshConfig.SNI)}")
                        OnlineConfigStatus.Text = $"Connecting to {sshConfig.Host}..."
                    End If
                End If

                ConnectButton.IsEnabled = False
                DisconnectButton.IsEnabled = False
                ServerConfigComboBox.IsEnabled = False
                UpdateConfigButton.IsEnabled = False
                ConnectButton.Content = "⏳ Connecting..."

                UpdateStatusUI(False, "Connecting...", "Establishing connection...")

                Dim result As Boolean = False

                If TypeOf _currentConfig Is VLessConfig Then
                    Dim vlessConfig = DirectCast(_currentConfig, VLessConfig)
                    AddLog($"Protocol: VLESS | Transport: {vlessConfig.TransportType} | Security: {vlessConfig.Security}")
                    result = Await _connectionManager.ConnectWithVLessAsync(vlessConfig, _proxyMode)
                ElseIf TypeOf _currentConfig Is SSHTLSConfig Then
                    Dim sshTlsConfig = DirectCast(_currentConfig, SSHTLSConfig)
                    AddLog($"Protocol: SSH+TLS | Username: {sshTlsConfig.Username}")
                    result = Await _connectionManager.ConnectWithSSHTLSAsync(sshTlsConfig, _proxyMode)
                End If

                If result Then
                    _isConnected = True
                    Dim protocol = If(TypeOf _currentConfig Is VLessConfig, "VLESS", "SSH+TLS")
                    UpdateStatusUI(True, protocol, "Traffic routing through proxy")
                    ConnectButton.Content = "✓ Connected"
                    DisconnectButton.IsEnabled = True
                    UpdateConfigButton.IsEnabled = True
                    ' Allow starting Share VPN only when base VPN is connected
                    If StartShareButton IsNot Nothing Then StartShareButton.IsEnabled = True
                    If StopShareButton IsNot Nothing Then StopShareButton.IsEnabled = False
                    AddLog("=== ✓ CONNECTION SUCCESSFUL ===")

                    ' Auto update adblock lists (24h policy) on connect when Ads is enabled
                    Try
                        Await AutoRefreshAdlistsIfNeededAsync()
                    Catch
                    End Try
                Else
                    UpdateStatusUI(False, "N/A", "Connection failed - Check configuration")
                    ConnectButton.Content = "▶ Connect"
                    ConnectButton.IsEnabled = True
                    ServerConfigComboBox.IsEnabled = True
                    UpdateConfigButton.IsEnabled = True
                    If StartShareButton IsNot Nothing Then StartShareButton.IsEnabled = False
                    If StopShareButton IsNot Nothing Then StopShareButton.IsEnabled = False
                    AddLog("=== ✗ CONNECTION FAILED ===", True)
                End If
            Catch ex As Exception
                AddLog($"✗ EXCEPTION: {ex.Message}", True)
                UpdateStatusUI(False, "N/A", "Connection error")
                ConnectButton.Content = "▶ Connect"
                ConnectButton.IsEnabled = True
                ServerConfigComboBox.IsEnabled = True
                UpdateConfigButton.IsEnabled = True
                If StartShareButton IsNot Nothing Then StartShareButton.IsEnabled = False
                If StopShareButton IsNot Nothing Then StopShareButton.IsEnabled = False
            End Try
        End Sub

        Private Sub DisconnectButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                AddLog("Disconnecting...")
                _connectionManager.Disconnect()
                If _vpnShareManager IsNot Nothing AndAlso _vpnShareManager.IsRunning Then
                    _vpnShareManager.StopShare()
                End If
                _isConnected = False
                UpdateStatusUI(False, "N/A", "Disconnected")
                ConnectButton.IsEnabled = True
                ConnectButton.Content = "▶ Connect"
                DisconnectButton.IsEnabled = False
                ServerConfigComboBox.IsEnabled = True
                UpdateConfigButton.IsEnabled = True
                If StartShareButton IsNot Nothing Then StartShareButton.IsEnabled = False
                If StopShareButton IsNot Nothing Then StopShareButton.IsEnabled = False
                AddLog("✓ Disconnected successfully")
            Catch ex As Exception
                AddLog($"✗ Disconnect error: {ex.Message}", True)
            End Try
        End Sub

        ''' <summary>
        ''' LOAD button - Force fetch fresh configurations from vpnisuru.web.app
        ''' </summary>
        Private Async Sub UpdateConfigButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                UpdateConfigButton.IsEnabled = False
                UpdateConfigButton.Content = "⏳ Loading..."
                AddLog("=================================================")
                AddLog("🔄 LOAD BUTTON CLICKED - Fetching from isuruhub.site")
                AddLog("=================================================")

                OnlineConfigStatus.Text = "Fetching latest from isuruhub.site..."
                OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7))

                Dim fetcher As New ConfigFetcher()

                ' FORCE REFRESH - Always fetch fresh from online, don't use cache
                Dim result = Await fetcher.FetchConfigurationsAsync(forceRefresh:=True)

                If result.VLessLinks.Count > 0 OrElse result.SSHConfigs.Count > 0 Then
                    AddLog($"✓ Successfully fetched {result.VLessLinks.Count} VLESS and {result.SSHConfigs.Count} SSH configuration(s) from server")

                    ' *** REMOVED CLIPBOARD FUNCTIONALITY ***

                    ' Disconnect if connected
                    Dim wasConnected As Boolean = _isConnected

                    ' Save fresh configs to cache
                    _updateChecker.SaveConfigsLocally(result.VLessLinks)
                    _updateChecker.SaveOnlineSSHConfigs(result.SSHConfigs)
                    AddLog("✓ Fresh configurations saved to local cache")

                    ' Load the fresh configurations into UI (pass the fresh result directly)
                    Await LoadConfigurationAsync(useCache:=False, freshResult:=result)

                    ' Show success message
                    MessageBox.Show($"✓ Successfully loaded {result.VLessLinks.Count} VLESS and {result.SSHConfigs.Count} SSH configuration(s) from isuruhub.site!", "Success", MessageBoxButton.OK, MessageBoxImage.Information)

                    ' Optionally reconnect if was connected
                    If wasConnected AndAlso _currentConfig IsNot Nothing AndAlso _availableConfigs.Count > 0 Then
                        Dim reconnect = MessageBox.Show("Do you want to reconnect with the updated configuration?", "Reconnect", MessageBoxButton.YesNo, MessageBoxImage.Question)

                        If reconnect = MessageBoxResult.Yes Then
                            AddLog("User chose to reconnect with updated config...")
                            Await Task.Delay(500)

                            ' If Share VPN is running, remember its settings and stop it before reconnect
                            Dim shareWasRunning As Boolean = (_vpnShareManager IsNot Nothing AndAlso _vpnShareManager.IsRunning)
                            Dim rememberedListenHost As String = "0.0.0.0"
                            Dim rememberedPublicHttpPort As Integer = 0
                            Dim rememberedPublicSocksPort As Integer = 0
                            Dim rememberedShareHttp As Boolean = False
                            Dim rememberedShareSocks As Boolean = False
                            If shareWasRunning Then
                                Try
                                    Dim listenItem = TryCast(ShareListenHostComboBox.SelectedItem, ComboBoxItem)
                                    If listenItem IsNot Nothing AndAlso listenItem.Tag IsNot Nothing Then
                                        rememberedListenHost = listenItem.Tag.ToString()
                                    End If
                                    Integer.TryParse(ShareHttpPortTextBox.Text.Trim(), rememberedPublicHttpPort)
                                    Integer.TryParse(ShareSocksPortTextBox.Text.Trim(), rememberedPublicSocksPort)
                                    rememberedShareHttp = ShareHttpCheck.IsChecked.GetValueOrDefault(False)
                                    rememberedShareSocks = ShareSocksCheck.IsChecked.GetValueOrDefault(False)
                                Catch
                                End Try
                                Try
                                    _vpnShareManager.StopShare()
                                    AddLog("Temporarily stopped Share VPN for reconnection (will restore after reconnect)")
                                Catch
                                End Try
                            End If

                            ' Now disconnect the active base connection
                            AddLog("Disconnecting current connection for reconnection...")
                            _connectionManager.Disconnect()
                            _isConnected = False
                            UpdateStatusUI(False, "N/A", "Disconnected for update")
                            Await Task.Delay(800)

                            Dim result2 As Boolean = False
                            If TypeOf _currentConfig Is VLessConfig Then
                                result2 = Await _connectionManager.ConnectWithVLessAsync(DirectCast(_currentConfig, VLessConfig))
                            ElseIf TypeOf _currentConfig Is SSHTLSConfig Then
                                result2 = Await _connectionManager.ConnectWithSSHTLSAsync(DirectCast(_currentConfig, SSHTLSConfig))
                            End If

                            If result2 Then
                                _isConnected = True
                                Dim protocol = If(TypeOf _currentConfig Is VLessConfig, "VLESS", "SSH+TLS")
                                UpdateStatusUI(True, protocol, "Reconnected with updated config")
                                ConnectButton.Content = "✓ Connected"
                                DisconnectButton.IsEnabled = True
                                AddLog("✓ Reconnected successfully with updated configuration")

                                ' Restore Share VPN if it was running before update, using new local ports
                                If shareWasRunning Then
                                    Try
                                        Dim localHttp = _connectionManager.LocalHttpPort
                                        Dim localSocks = _connectionManager.LocalSocksPort

                                        Dim startedShare = Await _vpnShareManager.StartAsync(rememberedListenHost,
                                                                                              rememberedPublicHttpPort,
                                                                                              rememberedPublicSocksPort,
                                                                                              rememberedShareHttp,
                                                                                              rememberedShareSocks,
                                                                                              localHttp,
                                                                                              localSocks)
                                        If startedShare Then
                                            Dim displayHost = rememberedListenHost
                                            If rememberedListenHost = "0.0.0.0" Then
                                                displayHost = GetHotspotIPv4()
                                                If String.IsNullOrEmpty(displayHost) Then displayHost = GetPrimaryLanIPv4()
                                                If String.IsNullOrEmpty(displayHost) Then displayHost = "(LAN IP?)"
                                                AddLog($"Sharing bound to 0.0.0.0. Use device IP {displayHost} for clients.")
                                            End If
                                            ShareHttpInfo.Text = If(rememberedShareHttp AndAlso localHttp > 0, $"HTTP: {displayHost}:{rememberedPublicHttpPort}", "HTTP: (not shared)")
                                            ShareSocksInfo.Text = If(rememberedShareSocks AndAlso localSocks > 0, $"SOCKS5: {displayHost}:{rememberedPublicSocksPort}", "SOCKS5: (not shared)")
                                            StartShareButton.Content = "✓ Sharing"
                                            StopShareButton.IsEnabled = True
                                            AddLog("✓ Share VPN restored after reconnect")
                                        Else
                                            AddLog("✗ Failed to restore Share VPN after reconnect", True)
                                        End If
                                    Catch exRestore As Exception
                                        AddLog($"✗ Share restore error: {exRestore.Message}", True)
                                    End Try
                                End If
                            Else
                                AddLog("✗ Reconnection failed", True)
                            End If
                        End If
                    End If
                Else
                    AddLog("⚠ No configurations found on server", True)
                    OnlineConfigStatus.Text = "No configurations found on server"
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange)
                    MessageBox.Show("No configurations found on isuruhub.site. Please check your website.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning)
                End If

                UpdateConfigButton.Content = "🔄 LOAD"
                UpdateConfigButton.IsEnabled = True
                AddLog("=================================================")
            Catch ex As Exception
                OnlineConfigStatus.Text = $"Error loading from server: {ex.Message}"
                OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                UpdateConfigButton.Content = "🔄 LOAD"
                UpdateConfigButton.IsEnabled = True
                AddLog($"✗ LOAD ERROR: {ex.Message}", True)
                AddLog("=================================================")
                MessageBox.Show($"Failed to fetch configurations from isuruhub.site:{vbCrLf}{vbCrLf}{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

        ''' <summary>
        ''' Load saved online configurations from local storage (no network)
        ''' </summary>
        Private Async Sub LoadSavedOnlineButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                AddLog("🔎 Loading locally saved online configurations...")
                LoadSavedOnlineButton.IsEnabled = False
                UpdateConfigButton.IsEnabled = False
                OnlineConfigStatus.Text = "Loading saved configurations..."
                OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Blue)

                ' Check if saved configs exist before loading
                Dim vlessCount = _updateChecker.LoadCachedConfigs().Count
                Dim sshCount = _updateChecker.LoadOnlineSSHConfigs().Count

                If vlessCount = 0 AndAlso sshCount = 0 Then
                    AddLog("⚠ No saved online configurations found", True)
                    OnlineConfigStatus.Text = "No saved configurations found"
                    OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange)
                    MessageBox.Show("No saved online configurations found!" & vbCrLf & vbCrLf &
                                  "Please click the '🔄 LOAD' button to fetch configurations from the server first." & vbCrLf & vbCrLf &
                                  "Configurations will be saved locally for offline use.",
                                  "No Saved Configs", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If

                AddLog($"Found {vlessCount} VLESS and {sshCount} SSH saved config(s)")

                ' Use the existing loader in cache mode
                Await LoadConfigurationAsync(useCache:=True)

                AddLog("✓ Local saved online configurations loaded")
                MessageBox.Show($"Successfully loaded {_availableConfigs.Count} configuration(s) from local storage!" & vbCrLf & vbCrLf &
                              $"VLESS: {vlessCount}" & vbCrLf &
                              $"SSH: {sshCount}",
                              "Configs Loaded", MessageBoxButton.OK, MessageBoxImage.Information)
            Catch ex As Exception
                AddLog($"✗ Failed to load saved online configurations: {ex.Message}", True)
                OnlineConfigStatus.Text = $"Error loading saved configs: {ex.Message}"
                OnlineConfigStatus.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                MessageBox.Show($"Failed to load saved configurations:{vbCrLf}{vbCrLf}{ex.Message}",
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            Finally
                LoadSavedOnlineButton.IsEnabled = True
                UpdateConfigButton.IsEnabled = True
            End Try
        End Sub

        Private Sub SaveButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim activeTab = MainTabControl.SelectedIndex

                If activeTab = 1 Then
                    ' Custom SSH tab
                    Dim newSNI = SSHSNITextBox.Text?.Trim()
                    If TypeOf _currentConfig Is SSHTLSConfig Then
                        DirectCast(_currentConfig, SSHTLSConfig).SNI = newSNI
                    End If
                    AddLog($"✓ SNI saved (SSH): {newSNI}")
                Else
                    ' Online VPN tab
                    Dim newSNI = SNITextBox.Text?.Trim()

                    If TypeOf _currentConfig Is VLessConfig Then
                        Dim vlessConfig = DirectCast(_currentConfig, VLessConfig)
                        vlessConfig.SNI = newSNI
                        _updateChecker.SaveUserSNI(newSNI)
                        AddLog($"✓ SNI saved for VLESS: {newSNI}")
                    ElseIf TypeOf _currentConfig Is SSHTLSConfig Then
                        Dim sshConfig = DirectCast(_currentConfig, SSHTLSConfig)
                        sshConfig.SNI = newSNI

                        ' If this is an online SSH config, save it to the online configs file
                        If sshConfig.IsOnlineConfig Then
                            Dim onlineConfigs = _updateChecker.LoadOnlineSSHConfigs()
                            Dim existingConfig = onlineConfigs.FirstOrDefault(Function(c) c.Host = sshConfig.Host AndAlso c.Port = sshConfig.Port AndAlso c.Username = sshConfig.Username)
                            If existingConfig IsNot Nothing Then
                                existingConfig.SNI = newSNI
                                _updateChecker.SaveOnlineSSHConfigs(onlineConfigs)
                                AddLog($"✓ SNI saved for online SSH config: {newSNI}")
                            End If
                        End If
                    End If
                End If

                MessageBox.Show("SNI saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information)
            Catch ex As Exception
                AddLog($"✗ Failed to save SNI: {ex.Message}", True)
            End Try
        End Sub

        ' ============== SSH CONFIG HANDLERS ==============
        Private Sub BrowseSSHKeyButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim dlg As New Microsoft.Win32.OpenFileDialog()
                dlg.Title = "Select SSH Private Key File"
                dlg.Filter = "Key Files (*.pem;*.key;*.ppk)|*.pem;*.key;*.ppk|All Files (*.*)|*.*"
                Dim result = dlg.ShowDialog()
                If result.HasValue AndAlso result.Value Then
                    SSHKeyPathTextBox.Text = dlg.FileName
                    AddLog($"Selected SSH key: {IO.Path.GetFileName(dlg.FileName)}")
                End If
            Catch ex As Exception
                AddLog($"Browse SSH key error: {ex.Message}", True)
            End Try
        End Sub

        Private Sub LoadSavedSSHConfigsUI()
            Try
                _savedSSHConfigs = _updateChecker.LoadSavedSSHConfigs()

                Dispatcher.Invoke(Sub()
                                      SSHConfigComboBox.ItemsSource = _savedSSHConfigs
                                      If _savedSSHConfigs.Count > 0 Then
                                          SSHConfigComboBox.SelectedIndex = 0
                                          SSHConfigStatusText.Text = $"{_savedSSHConfigs.Count} saved configuration(s)"
                                          SSHConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                                          AddLog($"✓ Loaded {_savedSSHConfigs.Count} saved SSH configuration(s)")
                                      Else
                                          SSHConfigStatusText.Text = "No saved SSH configurations"
                                          SSHConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                                      End If
                                  End Sub)
            Catch ex As Exception
                AddLog($"✗ Failed to load saved SSH configs: {ex.Message}", True)
            End Try
        End Sub

        Private Sub SSHConfigComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Try
                If SSHConfigComboBox.SelectedItem Is Nothing Then Return
                Dim cfg = DirectCast(SSHConfigComboBox.SelectedItem, SSHTLSConfig)
                SSHHostTextBox.Text = cfg.Host
                SSHPortTextBox.Text = cfg.Port.ToString()
                SSHUsernameTextBox.Text = cfg.Username

                ' Load authentication method
                If cfg.UseKeyAuth AndAlso Not String.IsNullOrEmpty(cfg.PrivateKeyPath) Then
                    UseSSHKeyCheck.IsChecked = True
                    SSHKeyPathTextBox.Text = cfg.PrivateKeyPath
                    SSHKeyPassphraseBox.Password = If(cfg.Passphrase, "")
                    SSHPasswordBox.Password = ""
                Else
                    UseSSHKeyCheck.IsChecked = False
                    SSHPasswordBox.Password = cfg.Password
                    SSHKeyPathTextBox.Text = ""
                    SSHKeyPassphraseBox.Password = ""
                End If

                SSHSNITextBox.Text = If(cfg.SNI, "")

                ' Load transport type
                If SSHTransportComboBox IsNot Nothing AndAlso Not String.IsNullOrEmpty(cfg.TransportType) Then
                    For Each item As ComboBoxItem In SSHTransportComboBox.Items
                        If item.Tag?.ToString().Equals(cfg.TransportType, StringComparison.OrdinalIgnoreCase) Then
                            SSHTransportComboBox.SelectedItem = item
                            Exit For
                        End If
                    Next
                End If

                SSHConfigStatusText.Text = $"Loaded: {cfg.Tag}"
                AddLog($"✓ Loaded saved SSH config: {cfg.Tag}")
            Catch ex As Exception
                AddLog($"✗ Failed to apply saved SSH config: {ex.Message}", True)
            End Try
        End Sub

        Private Sub SaveSSHConfigButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim host = SSHHostTextBox.Text?.Trim()
                Dim portVal As Integer
                Integer.TryParse(SSHPortTextBox.Text?.Trim(), portVal)
                Dim user = SSHUsernameTextBox.Text?.Trim()
                Dim sni = SSHSNITextBox.Text?.Trim()

                If String.IsNullOrWhiteSpace(host) OrElse portVal <= 0 OrElse String.IsNullOrWhiteSpace(user) Then
                    MessageBox.Show("Please provide Host, Port, and Username to save.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                ' Check authentication method
                Dim useKeyAuth = UseSSHKeyCheck.IsChecked.GetValueOrDefault(False)
                Dim pass = SSHPasswordBox.Password
                Dim keyPath = SSHKeyPathTextBox.Text?.Trim()
                Dim passphrase = SSHKeyPassphraseBox.Password

                If useKeyAuth Then
                    If String.IsNullOrWhiteSpace(keyPath) Then
                        MessageBox.Show("Please select a private key file for key authentication.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                        Return
                    End If
                    If Not IO.File.Exists(keyPath) Then
                        MessageBox.Show("Private key file not found. Please check the path.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                        Return
                    End If
                Else
                    If String.IsNullOrWhiteSpace(pass) Then
                        MessageBox.Show("Please provide a password for password authentication.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                        Return
                    End If
                End If

                Dim label As String = $"{host}:{portVal} - {user}"
                Dim existing = _savedSSHConfigs.FirstOrDefault(Function(c) c.Tag = label)
                If existing Is Nothing Then
                    existing = New SSHTLSConfig()
                    _savedSSHConfigs.Add(existing)
                End If

                existing.Host = host
                existing.Port = portVal
                existing.Username = user
                existing.Password = If(useKeyAuth, "", pass)
                existing.UseKeyAuth = useKeyAuth
                existing.PrivateKeyPath = If(useKeyAuth, keyPath, "")
                existing.Passphrase = If(useKeyAuth, passphrase, "")
                existing.SNI = sni
                existing.Tag = label
                existing.LocalPort = 0

                ' Get transport type
                Dim transportItem = TryCast(SSHTransportComboBox.SelectedItem, ComboBoxItem)
                If transportItem IsNot Nothing AndAlso transportItem.Tag IsNot Nothing Then
                    existing.TransportType = transportItem.Tag.ToString()
                End If

                _updateChecker.SaveSavedSSHConfigs(_savedSSHConfigs)

                SSHConfigComboBox.ItemsSource = Nothing
                SSHConfigComboBox.ItemsSource = _savedSSHConfigs
                SSHConfigComboBox.SelectedItem = existing
                SSHConfigStatusText.Text = "Configuration saved successfully"
                SSHConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                AddLog($"✓ SSH config saved: {label} ({If(useKeyAuth, "Key Auth", "Password Auth")})")
                MessageBox.Show("SSH configuration saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information)
            Catch ex As Exception
                AddLog($"✗ Failed to save SSH config: {ex.Message}", True)
            End Try
        End Sub

        Private Sub DeleteSSHConfigButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If SSHConfigComboBox.SelectedItem Is Nothing Then
                    MessageBox.Show("Select a saved SSH configuration to delete.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If

                Dim cfg = DirectCast(SSHConfigComboBox.SelectedItem, SSHTLSConfig)
                _savedSSHConfigs.Remove(cfg)
                _updateChecker.SaveSavedSSHConfigs(_savedSSHConfigs)

                SSHConfigComboBox.ItemsSource = Nothing
                SSHConfigComboBox.ItemsSource = _savedSSHConfigs
                If _savedSSHConfigs.Count > 0 Then
                    SSHConfigComboBox.SelectedIndex = 0
                Else
                    SSHHostTextBox.Clear()
                    SSHPortTextBox.Text = "22"
                    SSHUsernameTextBox.Clear()
                    SSHPasswordBox.Password = ""
                    SSHSNITextBox.Clear()
                    SSHConfigStatusText.Text = "No saved configurations"
                End If

                AddLog($"✓ SSH config deleted: {cfg.Tag}")
            Catch ex As Exception
                AddLog($"✗ Failed to delete SSH config: {ex.Message}", True)
            End Try
        End Sub

        ' ============== CUSTOM VLESS/VMESS CONFIG HANDLERS ==============
        Private Sub ImportLinkButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim link = CustomLinkTextBox.Text?.Trim()
                If String.IsNullOrWhiteSpace(link) Then
                    MessageBox.Show("Please paste a VLESS or VMESS configuration link.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                If link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) Then
                    Dim config = VLessParser.ParseVLessLink(link)
                    LoadCustomConfigToUI(config)
                    CustomConfigStatusText.Text = "VLESS configuration imported successfully"
                    CustomConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    AddLog($"✓ Imported VLESS config: {config.Tag}")
                    MessageBox.Show("VLESS configuration imported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information)
                ElseIf link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) Then
                    Try
                        Dim vmessConfig = VMESSParser.ParseVMESSLink(link)
                        ' Convert VMESS to VLess for UI display
                        Dim vlessConfig As New VLessConfig With {
                            .UUID = vmessConfig.UUID,
                            .Host = vmessConfig.Host,
                            .Port = vmessConfig.Port,
                            .Tag = vmessConfig.Tag,
                            .Security = If(vmessConfig.TLS = "tls", "tls", "none"),
                            .TransportType = vmessConfig.Network,
                            .Path = vmessConfig.Path,
                            .SNI = ""
                        }
                        LoadCustomConfigToUI(vlessConfig)
                        CustomConfigStatusText.Text = "VMESS configuration imported successfully (converted to VLESS format)"
                        CustomConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                        AddLog($"✓ Imported VMESS config: {vmessConfig.Tag}")
                        MessageBox.Show("VMESS configuration imported and converted to VLESS format!\n\nNote: VMESS is legacy protocol. VLESS is recommended.", "Success", MessageBoxButton.OK, MessageBoxImage.Information)
                    Catch vmessEx As Exception
                        AddLog($"✗ VMESS import error: {vmessEx.Message}", True)
                        MessageBox.Show($"Failed to parse VMESS configuration:\n\n{vmessEx.Message}\n\nPlease check the link format.", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
                    End Try
                ElseIf link.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) Then
                    MessageBox.Show("SSH link detected! Please paste it in the 'Custom SSH' tab for SSH configurations.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                Else
                    MessageBox.Show("Invalid link format. Please use:\n• vless:// for VLESS\n• vmess:// for VMESS\n• ssh:// for SSH (use Custom SSH tab)", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
                End If
            Catch ex As Exception
                AddLog($"✗ Import error: {ex.Message}", True)
                MessageBox.Show($"Failed to import configuration:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

        Private Sub LoadCustomConfigToUI(config As VLessConfig)
            CustomTagTextBox.Text = If(config.Tag, "")
            CustomHostTextBox.Text = If(config.Host, "")
            CustomPortTextBox.Text = config.Port.ToString()
            CustomUUIDTextBox.Text = If(config.UUID, "")
            CustomSNITextBox.Text = If(config.SNI, "")
            CustomPathTextBox.Text = If(config.Path, "")

            ' Set security
            Dim securityIndex = 0
            If Not String.IsNullOrEmpty(config.Security) Then
                Select Case config.Security.ToLower()
                    Case "none" : securityIndex = 0
                    Case "tls" : securityIndex = 1
                    Case "reality" : securityIndex = 2
                End Select
            End If
            CustomSecurityComboBox.SelectedIndex = securityIndex

            ' Set transport
            Dim transportIndex = 0
            If Not String.IsNullOrEmpty(config.TransportType) Then
                Select Case config.TransportType.ToLower()
                    Case "tcp" : transportIndex = 0
                    Case "ws" : transportIndex = 1
                    Case "grpc" : transportIndex = 2
                    Case "h2" : transportIndex = 3
                    Case "quic" : transportIndex = 4
                End Select
            End If
            CustomTransportComboBox.SelectedIndex = transportIndex

            CustomProtocolText.Text = "VLESS"
        End Sub

        Private Function GetCustomConfigFromUI() As VLessConfig
            Try
                Dim tag = CustomTagTextBox.Text?.Trim()
                Dim host = CustomHostTextBox.Text?.Trim()
                Dim portStr = CustomPortTextBox.Text?.Trim()
                Dim uuid = CustomUUIDTextBox.Text?.Trim()
                Dim sni = CustomSNITextBox.Text?.Trim()
                Dim path = CustomPathTextBox.Text?.Trim()

                If String.IsNullOrWhiteSpace(host) OrElse String.IsNullOrWhiteSpace(uuid) Then
                    Return Nothing
                End If

                Dim port As Integer
                If Not Integer.TryParse(portStr, port) OrElse port <= 0 OrElse port > 65535 Then
                    Return Nothing
                End If

                Dim security = DirectCast(CustomSecurityComboBox.SelectedItem, ComboBoxItem)?.Content?.ToString()?.ToLower()
                Dim transport = DirectCast(CustomTransportComboBox.SelectedItem, ComboBoxItem)?.Content?.ToString()?.ToLower()

                Dim config As New VLessConfig With {
                    .Tag = tag,
                    .Host = host,
                    .Port = port,
                    .UUID = uuid,
                    .Security = security,
                    .TransportType = transport,
                    .SNI = sni,
                    .Path = path
                }

                Return config
            Catch ex As Exception
                AddLog($"✗ Failed to read custom config from UI: {ex.Message}", True)
                Return Nothing
            End Try
        End Function

        Private Sub LoadSavedCustomConfigsUI()
            Try
                _savedCustomConfigs = LoadSavedCustomConfigs()

                Dispatcher.Invoke(Sub()
                                      CustomConfigComboBox.ItemsSource = _savedCustomConfigs
                                      If _savedCustomConfigs.Count > 0 Then
                                          CustomConfigComboBox.SelectedIndex = 0
                                          CustomConfigStatusText.Text = $"{_savedCustomConfigs.Count} saved configuration(s)"
                                          CustomConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                                          AddLog($"✓ Loaded {_savedCustomConfigs.Count} saved custom VLESS/VMESS configuration(s)")
                                      Else
                                          CustomConfigStatusText.Text = "No saved custom configurations"
                                          CustomConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray)
                                      End If
                                  End Sub)
            Catch ex As Exception
                AddLog($"✗ Failed to load saved custom configs: {ex.Message}", True)
            End Try
        End Sub

        Private Sub CustomConfigComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Try
                If CustomConfigComboBox.SelectedItem Is Nothing Then Return
                Dim cfg = DirectCast(CustomConfigComboBox.SelectedItem, VLessConfig)
                LoadCustomConfigToUI(cfg)
                CustomConfigStatusText.Text = $"Loaded: {cfg.Tag}"
                AddLog($"✓ Loaded saved custom config: {cfg.Tag}")
            Catch ex As Exception
                AddLog($"✗ Failed to apply saved custom config: {ex.Message}", True)
            End Try
        End Sub

        Private Sub SaveCustomConfigButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim config = GetCustomConfigFromUI()
                If config Is Nothing Then
                    MessageBox.Show("Please configure all required fields (Host, Port, UUID) before saving.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                If Not VLessParser.ValidateConfig(config) Then
                    MessageBox.Show("Invalid configuration. Please check all fields.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                Dim label = If(String.IsNullOrWhiteSpace(config.Tag), $"{config.Host}:{config.Port}", config.Tag)
                config.Tag = label

                Dim existing = _savedCustomConfigs.FirstOrDefault(Function(c) c.Tag = label)
                If existing IsNot Nothing Then
                    _savedCustomConfigs.Remove(existing)
                End If

                _savedCustomConfigs.Add(config)
                SaveSavedCustomConfigs(_savedCustomConfigs)

                CustomConfigComboBox.ItemsSource = Nothing
                CustomConfigComboBox.ItemsSource = _savedCustomConfigs
                CustomConfigComboBox.SelectedItem = config
                CustomConfigStatusText.Text = "Configuration saved successfully"
                CustomConfigStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                AddLog($"✓ Custom config saved: {label}")
                MessageBox.Show("Configuration saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information)
            Catch ex As Exception
                AddLog($"✗ Failed to save custom config: {ex.Message}", True)
            End Try
        End Sub

        Private Sub DeleteCustomConfigButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If CustomConfigComboBox.SelectedItem Is Nothing Then
                    MessageBox.Show("Select a saved configuration to delete.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
                    Return
                End If

                Dim cfg = DirectCast(CustomConfigComboBox.SelectedItem, VLessConfig)
                _savedCustomConfigs.Remove(cfg)
                SaveSavedCustomConfigs(_savedCustomConfigs)

                CustomConfigComboBox.ItemsSource = Nothing
                CustomConfigComboBox.ItemsSource = _savedCustomConfigs
                If _savedCustomConfigs.Count > 0 Then
                    CustomConfigComboBox.SelectedIndex = 0
                Else
                    CustomTagTextBox.Clear()
                    CustomHostTextBox.Clear()
                    CustomPortTextBox.Clear()
                    CustomUUIDTextBox.Clear()
                    CustomSNITextBox.Clear()
                    CustomPathTextBox.Clear()
                    CustomConfigStatusText.Text = "No saved configurations"
                End If

                AddLog($"✓ Custom config deleted: {cfg.Tag}")
            Catch ex As Exception
                AddLog($"✗ Failed to delete custom config: {ex.Message}", True)
            End Try
        End Sub

        ' Persistence for custom configs
        Private Function LoadSavedCustomConfigs() As List(Of VLessConfig)
            Try
                Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                Dim filePath = Path.Combine(appDataPath, "VPNClientApp", "custom_vless_configs.json")
                If File.Exists(filePath) Then
                    Dim json = File.ReadAllText(filePath)
                    Dim list = System.Text.Json.JsonSerializer.Deserialize(Of List(Of VLessConfig))(json)
                    If list IsNot Nothing Then Return list
                End If
            Catch ex As Exception
                AddLog($"Failed to load custom configs: {ex.Message}", True)
            End Try
            Return New List(Of VLessConfig)
        End Function

        Private Sub SaveSavedCustomConfigs(configs As List(Of VLessConfig))
            Try
                Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                Dim dir = Path.Combine(appDataPath, "VPNClientApp")
                If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
                Dim filePath = Path.Combine(dir, "custom_vless_configs.json")
                Dim json = System.Text.Json.JsonSerializer.Serialize(configs, New System.Text.Json.JsonSerializerOptions With {.WriteIndented = True})
                File.WriteAllText(filePath, json)
            Catch ex As Exception
                AddLog($"Failed to save custom configs: {ex.Message}", True)
            End Try
        End Sub

        Private Async Function CheckForUpdatesAsync() As Task
            Try
                AddLog("Checking for configuration updates in background...")

                Await _updateChecker.CheckForUpdatesAsync()

                ' If update check succeeds, reload configurations
                Dispatcher.Invoke(Sub()
                                      AddLog("✓ Background update check completed")
                                  End Sub)
            Catch ex As Exception
                AddLog($"⚠ Background update check failed: {ex.Message}", True)
            End Try
        End Function

        Private Sub RefreshUI(sender As Object, e As EventArgs)
            Try
                If _connectionManager IsNot Nothing Then
                    Dim isConnected = _connectionManager.IsConnected()
                    If isConnected <> _isConnected Then
                        _isConnected = isConnected
                        Dim protocol = If(TypeOf _currentConfig Is VLessConfig, "VLESS", "SSH+TLS")
                        UpdateStatusUI(isConnected, protocol)

                        If isConnected Then
                            ConnectButton.Content = "✓ Connected"
                            DisconnectButton.IsEnabled = True
                            ConnectButton.IsEnabled = False
                            ServerConfigComboBox.IsEnabled = False
                            ' Enable starting share only when VPN is connected
                            If StartShareButton IsNot Nothing Then StartShareButton.IsEnabled = True
                        Else
                            ConnectButton.Content = "▶ Connect"
                            ConnectButton.IsEnabled = True
                            DisconnectButton.IsEnabled = False
                            ServerConfigComboBox.IsEnabled = True
                            ' Stop share automatically if VPN went down and disable start
                            Try
                                If _vpnShareManager IsNot Nothing AndAlso _vpnShareManager.IsRunning Then
                                    _vpnShareManager.StopShare()
                                    ShareHttpInfo.Text = "HTTP: -"
                                    ShareSocksInfo.Text = "SOCKS5: -"
                                End If
                                If _hotspotManager IsNot Nothing AndAlso _hotspotManager.IsRunning Then
                                    _hotspotManager.StopHotspot()
                                End If
                            Catch
                            End Try
                            If StartShareButton IsNot Nothing Then
                                StartShareButton.IsEnabled = False
                                StartShareButton.Content = "▶ Start Share"
                            End If
                            If StopShareButton IsNot Nothing Then StopShareButton.IsEnabled = False
                        End If
                    End If

                    Dim stats = _connectionManager.GetConnectionStats()
                    If stats.ContainsKey("BytesDownloaded") AndAlso stats.ContainsKey("BytesUploaded") Then
                        UpdateTrafficStats(stats("BytesDownloaded"), stats("BytesUploaded"))
                    End If
                End If
            Catch
            End Try
        End Sub

        ' ============== SHARE VPN FEATURE ==============
        Private Sub OnShareStatusChanged(running As Boolean, active As Integer, total As Integer)
            Try
                Dispatcher.Invoke(Sub()
                                      ShareStatusBadge.Background = If(running, New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green), New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&HFF, &HC1, &H7)))
                                      ShareStatusText.Text = If(running, "🟢 Sharing", "⚫ Stopped")
                                      ShareActiveText.Text = $"Active: {active}"
                                      ShareTotalText.Text = $"Total: {total}"
                                  End Sub)
            Catch
            End Try
        End Sub

        Private Async Sub StartShareButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If Not _isConnected Then
                    MessageBox.Show("Connect VPN first before sharing.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                Dim listenItem = DirectCast(ShareListenHostComboBox.SelectedItem, ComboBoxItem)
                Dim listenHost = listenItem.Tag.ToString()

                Dim shareHttp = ShareHttpCheck.IsChecked.GetValueOrDefault(False)
                Dim shareSocks = ShareSocksCheck.IsChecked.GetValueOrDefault(False)

                If Not shareHttp AndAlso Not shareSocks Then
                    MessageBox.Show("Select at least one proxy type to share (HTTP or SOCKS5).", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                Dim publicHttpPort As Integer = 0
                Dim publicSocksPort As Integer = 0
                Integer.TryParse(ShareHttpPortTextBox.Text.Trim(), publicHttpPort)
                Integer.TryParse(ShareSocksPortTextBox.Text.Trim(), publicSocksPort)

                If shareHttp AndAlso publicHttpPort <= 0 Then
                    MessageBox.Show("Invalid public HTTP port.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If
                If shareSocks AndAlso publicSocksPort <= 0 Then
                    MessageBox.Show("Invalid public SOCKS5 port.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                Dim localHttp = _connectionManager.LocalHttpPort
                Dim localSocks = _connectionManager.LocalSocksPort
                If shareHttp AndAlso localHttp = 0 Then
                    MessageBox.Show("Underlying HTTP proxy not active yet.", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
                    Return
                End If
                If shareSocks AndAlso localSocks = 0 Then
                    MessageBox.Show("Underlying SOCKS5 proxy not active yet.", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
                    Return
                End If

                StartShareButton.IsEnabled = False
                StopShareButton.IsEnabled = False
                StartShareButton.Content = "⏳ Starting..."

                Dim started = Await _vpnShareManager.StartAsync(listenHost, publicHttpPort, publicSocksPort, shareHttp, shareSocks, localHttp, localSocks)
                If started Then
                    ' Attempt to auto-start hotspot (best-effort)
                    Dim hotspotStarted = False
                    If _hotspotManager IsNot Nothing AndAlso Not _hotspotManager.IsRunning Then
                        hotspotStarted = Await _hotspotManager.StartAsync()
                        If hotspotStarted Then
                            AddLog("Windows hosted network / hotspot attempt started.")
                        Else
                            AddLog("Hotspot auto-start failed or unsupported. You can still use existing LAN/Wi‑Fi IP.", True)
                        End If
                    End If

                    ' If binding to 0.0.0.0 show the hotspot/LAN IP for convenience
                    Dim displayHost = listenHost
                    If listenHost = "0.0.0.0" Then
                        displayHost = GetHotspotIPv4()
                        If String.IsNullOrEmpty(displayHost) Then displayHost = GetPrimaryLanIPv4()
                        If String.IsNullOrEmpty(displayHost) Then displayHost = "(LAN IP?)"
                        AddLog($"Sharing bound to 0.0.0.0. Use device IP {displayHost} for clients.")
                    End If
                    ShareHttpInfo.Text = If(shareHttp, $"HTTP: {displayHost}:{publicHttpPort}", "HTTP: (not shared)")
                    ShareSocksInfo.Text = If(shareSocks, $"SOCKS5: {displayHost}:{publicSocksPort}", "SOCKS5: (not shared)")
                    StartShareButton.Content = "✓ Sharing"
                    StopShareButton.IsEnabled = True
                    MessageBox.Show("VPN sharing started.", "Started", MessageBoxButton.OK, MessageBoxImage.Information)
                Else
                    StartShareButton.Content = "▶ Start Share"
                    StartShareButton.IsEnabled = True
                    MessageBox.Show("Failed to start sharing.", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
                End If
            Catch ex As Exception
                AddLog($"Share start error: {ex.Message}", True)
                StartShareButton.Content = "▶ Start Share"
                StartShareButton.IsEnabled = True
            End Try
        End Sub

        Private Sub StopShareButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If _vpnShareManager IsNot Nothing AndAlso _vpnShareManager.IsRunning Then
                    _vpnShareManager.StopShare()
                    ShareHttpInfo.Text = "HTTP: -"
                    ShareSocksInfo.Text = "SOCKS5: -"
                End If
                If _hotspotManager IsNot Nothing AndAlso _hotspotManager.IsRunning Then
                    _hotspotManager.StopHotspot()
                End If
                StartShareButton.IsEnabled = True
                StartShareButton.Content = "▶ Start Share"
                StopShareButton.IsEnabled = False
                MessageBox.Show("VPN sharing stopped.", "Stopped", MessageBoxButton.OK, MessageBoxImage.Information)
            Catch ex As Exception
                AddLog($"Share stop error: {ex.Message}", True)
            End Try
        End Sub

        ''' <summary>
        ''' Add URL to share filter list
        ''' </summary>
        Private Sub AddShareFilterButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim url = ShareFilterUrlTextBox.Text.Trim()
                If String.IsNullOrEmpty(url) Then
                    MessageBox.Show("Please enter a URL or domain.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                ' Normalize to domain
                Dim domain = NormalizeToDomain(url)

                ' Check if already exists
                For Each item In ShareFilteredUrlsListBox.Items
                    If item.ToString().Equals(domain, StringComparison.OrdinalIgnoreCase) Then
                        MessageBox.Show("This URL is already in the filter list.", "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information)
                        Return
                    End If
                Next

                ShareFilteredUrlsListBox.Items.Add(domain)
                ShareFilterUrlTextBox.Clear()
                AddLog($"Added URL to share filter: {domain}")
                MessageBox.Show($"Added: {domain}", "Success", MessageBoxButton.OK, MessageBoxImage.Information)
            Catch ex As Exception
                AddLog($"Add share filter error: {ex.Message}", True)
                MessageBox.Show($"Error adding URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

        ''' <summary>
        ''' Delete selected URL from share filter list
        ''' </summary>
        Private Sub DeleteShareFilterButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                If ShareFilteredUrlsListBox.SelectedItem Is Nothing Then
                    MessageBox.Show("Please select a URL to delete.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                Dim selectedUrl = ShareFilteredUrlsListBox.SelectedItem.ToString()
                ShareFilteredUrlsListBox.Items.Remove(ShareFilteredUrlsListBox.SelectedItem)
                AddLog($"Removed URL from share filter: {selectedUrl}")
                MessageBox.Show($"Removed: {selectedUrl}", "Success", MessageBoxButton.OK, MessageBoxImage.Information)
            Catch ex As Exception
                AddLog($"Delete share filter error: {ex.Message}", True)
                MessageBox.Show($"Error deleting URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

        ''' <summary>
        ''' Apply URL filtering to shared VPN
        ''' </summary>
        Private Sub ApplyShareFilterButton_Click(sender As Object, e As RoutedEventArgs)
            Try
                Dim filterEnabled = EnableShareFilterCheck.IsChecked.GetValueOrDefault(False)

                If filterEnabled Then
                    If ShareFilteredUrlsListBox.Items.Count = 0 Then
                        MessageBox.Show("Please add at least one URL to the filter list before enabling.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning)
                        Return
                    End If

                    ' Collect all URLs
                    Dim allowedUrls As New List(Of String)
                    For Each item In ShareFilteredUrlsListBox.Items
                        allowedUrls.Add(item.ToString())
                    Next

                    ' Apply to VPN share manager
                    If _vpnShareManager IsNot Nothing Then
                        _vpnShareManager.SetUrlFilter(allowedUrls, True)
                        ShareFilterStatusText.Text = $"Filter: Enabled ({allowedUrls.Count} URLs allowed)"
                        ShareFilterStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&H28, &HA7, &H45))
                        AddLog($"URL filtering enabled for shared VPN: {allowedUrls.Count} URLs allowed")
                        MessageBox.Show($"URL filtering enabled. Shared VPN clients can only access {allowedUrls.Count} allowed URL(s).", "Applied", MessageBoxButton.OK, MessageBoxImage.Information)
                    End If
                Else
                    ' Disable filtering
                    If _vpnShareManager IsNot Nothing Then
                        _vpnShareManager.SetUrlFilter(New List(Of String), False)
                        ShareFilterStatusText.Text = "Filter: Disabled"
                        ShareFilterStatusText.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(&H66, &H66, &H66))
                        AddLog("URL filtering disabled for shared VPN")
                        MessageBox.Show("URL filtering disabled. Shared VPN has unrestricted access.", "Applied", MessageBoxButton.OK, MessageBoxImage.Information)
                    End If
                End If
            Catch ex As Exception
                AddLog($"Apply share filter error: {ex.Message}", True)
                MessageBox.Show($"Error applying filter: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

        ''' <summary>
        ''' Normalize URL to domain format
        ''' </summary>
        Private Function NormalizeToDomain(url As String) As String
            Try
                ' Remove protocol if present
                Dim normalized = url.ToLower().Trim()
                normalized = normalized.Replace("http://", "").Replace("https://", "")

                ' Remove path if present
                Dim slashIndex = normalized.IndexOf("/"c)
                If slashIndex > 0 Then
                    normalized = normalized.Substring(0, slashIndex)
                End If

                ' Remove port if present
                Dim colonIndex = normalized.IndexOf(":"c)
                If colonIndex > 0 Then
                    normalized = normalized.Substring(0, colonIndex)
                End If

                Return normalized
            Catch
                Return url.ToLower().Trim()
            End Try
        End Function

        Private Function GetPrimaryLanIPv4() As String
            Try
                Dim nics = Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                For Each ni In nics
                    If ni.OperationalStatus = Net.NetworkInformation.OperationalStatus.Up AndAlso
                       ni.NetworkInterfaceType <> Net.NetworkInformation.NetworkInterfaceType.Loopback AndAlso
                       ni.NetworkInterfaceType <> Net.NetworkInformation.NetworkInterfaceType.Tunnel Then
                        Dim props = ni.GetIPProperties()
                        For Each unicast In props.UnicastAddresses
                            If unicast.Address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork Then
                                Dim ip = unicast.Address.ToString()
                                If Not ip.StartsWith("169.254") Then
                                    Return ip
                                End If
                            End If
                        Next
                    End If
                Next
            Catch
            End Try
            Return String.Empty
        End Function

        Private Function GetHotspotIPv4() As String
            Try
                Dim nics = Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                For Each ni In nics
                    If ni.OperationalStatus = Net.NetworkInformation.OperationalStatus.Up Then
                        Dim nameL = ni.Name.ToLower()
                        Dim descL = ni.Description.ToLower()
                        If descL.Contains("wi-fi direct") OrElse descL.Contains("hosted network") OrElse nameL.Contains("hotspot") Then
                            Dim props = ni.GetIPProperties()
                            For Each unicast In props.UnicastAddresses
                                If unicast.Address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork Then
                                    Return unicast.Address.ToString()
                                End If
                            Next
                        End If
                    End If
                Next
                ' Fallback commonly used by Windows ICS for hosted network
                Return "192.168.137.1"
            Catch
                Return String.Empty
            End Try
        End Function

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
        Protected Sub NotifyPropertyChanged(propertyName As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub

        Private Sub my_Click(sender As Object, e As RoutedEventArgs) Handles my.Click
            Process.Start(New ProcessStartInfo("https://isuruhub.site") With {.UseShellExecute = True})
        End Sub

        ''' <summary>
        ''' Handle hyperlink navigation in About tab
        ''' </summary>
        Private Sub Hyperlink_RequestNavigate(sender As Object, e As System.Windows.Navigation.RequestNavigateEventArgs)
            Try
                Process.Start(New ProcessStartInfo(e.Uri.AbsoluteUri) With {.UseShellExecute = True})
                e.Handled = True
            Catch ex As Exception
                AddLog($"Failed to open link: {ex.Message}", True)
            End Try
        End Sub

        ''' <summary>
        ''' Handle tab selection change
        ''' </summary>
        Private Sub MainTabControl_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Try
                If MainTabControl Is Nothing Then Return
                ' Tab selection handling if needed in future
            Catch ex As Exception
                ' Silently handle any errors to avoid disrupting tab navigation
            End Try
        End Sub

        ''' <summary>
        ''' Handle proxy mode selection change
        ''' </summary>
        Private Sub ProxyModeComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Try
                If ProxyModeComboBox?.SelectedItem Is Nothing Then Return

                Dim selectedItem = TryCast(ProxyModeComboBox.SelectedItem, ComboBoxItem)
                If selectedItem IsNot Nothing AndAlso selectedItem.Tag IsNot Nothing Then
                    _proxyMode = selectedItem.Tag.ToString()
                    AddLog($"Proxy mode changed to: {_proxyMode}")

                    ' If connected, notify user that they need to reconnect for changes to take effect
                    If _isConnected Then
                        AddLog("Note: Reconnect VPN for proxy mode change to take effect", False)
                    End If
                End If
            Catch ex As Exception
                AddLog($"Proxy mode selection error: {ex.Message}", True)
            End Try
        End Sub

        ''' <summary>
        ''' Detect current system proxy settings and set ComboBox to match
        ''' </summary>
        Private Sub DetectAndSetCurrentProxyMode()
            Try
                Dispatcher.Invoke(Sub()
                                      Dim registryKey = "Software\Microsoft\Windows\CurrentVersion\Internet Settings"
                                      Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey, False)
                                          If key Is Nothing Then
                                              ' Default to System mode if can't read registry
                                              SetProxyModeComboBox("System")
                                              AddLog("Proxy mode set to default: System")
                                              Return
                                          End If

                                          Dim proxyEnable As Integer = 0
                                          Dim proxyServer As String = ""
                                          Dim autoConfigURL As String = ""

                                          Try
                                              proxyEnable = CInt(key.GetValue("ProxyEnable", 0))
                                          Catch
                                          End Try

                                          Try
                                              proxyServer = key.GetValue("ProxyServer", "").ToString()
                                          Catch
                                          End Try

                                          Try
                                              autoConfigURL = key.GetValue("AutoConfigURL", "").ToString()
                                          Catch
                                          End Try

                                          ' Determine current mode
                                          If Not String.IsNullOrEmpty(autoConfigURL) Then
                                              ' PAC script is configured
                                              SetProxyModeComboBox("PAC")
                                              AddLog($"Detected current proxy mode: PAC Script ({autoConfigURL})")
                                          ElseIf proxyEnable = 1 AndAlso Not String.IsNullOrEmpty(proxyServer) Then
                                              ' Manual proxy is enabled
                                              If proxyServer.Contains(";") Then
                                                  ' Multiple protocols configured - likely Global or Manual
                                                  Dim proxyOverride As String = ""
                                                  Try
                                                      proxyOverride = key.GetValue("ProxyOverride", "").ToString()
                                                  Catch
                                                  End Try

                                                  If String.IsNullOrEmpty(proxyOverride) Then
                                                      SetProxyModeComboBox("Global")
                                                      AddLog($"Detected current proxy mode: Global ({proxyServer})")
                                                  Else
                                                      SetProxyModeComboBox("Manual")
                                                      AddLog($"Detected current proxy mode: Manual ({proxyServer})")
                                                  End If
                                              Else
                                                  ' Single proxy - likely System mode
                                                  SetProxyModeComboBox("System")
                                                  AddLog($"Detected current proxy mode: System ({proxyServer})")
                                              End If
                                          Else
                                              ' No proxy configured
                                              SetProxyModeComboBox("None")
                                              AddLog("Detected current proxy mode: No Proxy")
                                          End If
                                      End Using
                                  End Sub)
            Catch ex As Exception
                AddLog($"Failed to detect current proxy mode: {ex.Message}", True)
                ' Default to System on error
                Dispatcher.Invoke(Sub() SetProxyModeComboBox("System"))
            End Try
        End Sub

        ''' <summary>
        ''' Set the ProxyModeComboBox selection by tag value
        ''' </summary>
        Private Sub SetProxyModeComboBox(tag As String)
            Try
                If ProxyModeComboBox Is Nothing Then Return

                For Each item As ComboBoxItem In ProxyModeComboBox.Items
                    If item.Tag IsNot Nothing AndAlso item.Tag.ToString().Equals(tag, StringComparison.OrdinalIgnoreCase) Then
                        ProxyModeComboBox.SelectedItem = item
                        _proxyMode = tag
                        Exit For
                    End If
                Next
            Catch ex As Exception
                AddLog($"Failed to set proxy mode combobox: {ex.Message}", True)
            End Try
        End Sub
    End Class

    Public Class ServerConfigItem
        Public Property DisplayName As String
        Public Property Config As Object
    End Class
End Namespace