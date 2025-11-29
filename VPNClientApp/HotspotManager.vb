Imports System.Diagnostics
Imports System.Text

Public Class HotspotManager
    Private _isRunning As Boolean = False

    Public Event LogMessage(message As String, isError As Boolean)

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _isRunning
        End Get
    End Property

    Public Async Function StartAsync(Optional ssid As String = "SLNET", Optional key As String = "SLnet@12345") As Task(Of Boolean)
        Try
            ' Check if hosted network is supported
            Dim supported = Await IsHostedNetworkSupportedAsync()
            If Not supported Then
                Log($"Windows hosted network isn't supported by this Wi‑Fi adapter. Opening Mobile Hotspot settings...", True)
                OpenMobileHotspotSettings()
                Return False
            End If

            ' Allow hosted network and set credentials
            Dim ok1 = RunNetsh($"wlan set hostednetwork mode=allow ssid={ssid} key={key} keyUsage=persistent")
            ' Try to start hosted network
            Dim ok2 = RunNetsh("wlan start hostednetwork")

            ' If not started, try elevated once (UAC prompt)
            If Not ok2 Then
                Log("Retrying hotspot start with elevation...")
                ok2 = RunNetshElevated("wlan start hostednetwork")
            End If

            If ok1 AndAlso ok2 Then
                _isRunning = True
                Log($"Hotspot started (SSID: {ssid}). If clients can't see it, start Windows Mobile Hotspot manually.")
                Return True
            Else
                Log("Failed to start hosted network. Opening Mobile Hotspot settings—toggle it on manually.", True)
                OpenMobileHotspotSettings()
                Return False
            End If
        Catch ex As Exception
            Log($"Hotspot start error: {ex.Message}", True)
            Return False
        End Try
    End Function

    Public Sub StopHotspot()
        Try
            RunNetsh("wlan stop hostednetwork")
            _isRunning = False
            Log("Hotspot stopped")
        Catch ex As Exception
            Log($"Hotspot stop error: {ex.Message}")
        End Try
    End Sub

    Private Sub Log(message As String, Optional isError As Boolean = False)
        RaiseEvent LogMessage(message, isError)
    End Sub

    Private Function IsHostedNetworkSupportedAsync() As Task(Of Boolean)
        Return Task.Run(Function()
                            Try
                                Dim psi As New ProcessStartInfo("netsh", "wlan show drivers") With {
                                    .UseShellExecute = False,
                                    .RedirectStandardOutput = True,
                                    .RedirectStandardError = True,
                                    .CreateNoWindow = True
                                }
                                Using p = Process.Start(psi)
                                    Dim output = p.StandardOutput.ReadToEnd()
                                    p.WaitForExit(3000)
                                    ' Look for either of these (varies by locale/version)
                                    If output.IndexOf("Hosted network supported", StringComparison.OrdinalIgnoreCase) >= 0 Then
                                        Dim line = output.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries).
                                            FirstOrDefault(Function(l) l.IndexOf("Hosted network supported", StringComparison.OrdinalIgnoreCase) >= 0)
                                        If line IsNot Nothing AndAlso line.ToLower().Contains("yes") Then Return True
                                    End If
                                    ' Fallback: some drivers use "Network hosted" phrasing; assume not supported if not found
                                    Return False
                                End Using
                            Catch
                                Return False
                            End Try
                        End Function)
    End Function

    Private Function RunNetsh(args As String) As Boolean
        Try
            Dim psi As New ProcessStartInfo("netsh", args) With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }
            Using p = Process.Start(psi)
                p.WaitForExit(5000)
                Dim code = p.ExitCode
                Dim err = p.StandardError.ReadToEnd()
                If code <> 0 Then
                    Log($"netsh failed: {args} (code {code}) {If(String.IsNullOrEmpty(err), "", " - " & err)}", True)
                End If
                Return code = 0
            End Using
        Catch ex As Exception
            Log($"netsh error: {ex.Message}", True)
            Return False
        End Try
    End Function

    Private Function RunNetshElevated(args As String) As Boolean
        Try
            Dim psi As New ProcessStartInfo("netsh", args) With {
                .UseShellExecute = True,
                .Verb = "runas",
                .CreateNoWindow = True
            }
            Using p = Process.Start(psi)
                p.WaitForExit(7000)
                Return p.ExitCode = 0
            End Using
        Catch ex As Exception
            Log($"Elevated netsh failed: {ex.Message}", True)
            Return False
        End Try
    End Function

    Private Sub OpenMobileHotspotSettings()
        Try
            Process.Start(New ProcessStartInfo("ms-settings:network-mobilehotspot") With {.UseShellExecute = True})
        Catch
        End Try
    End Sub
End Class
