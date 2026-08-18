Imports System.Diagnostics

''' <summary>
''' Global speed limiter using Windows QoS policies via PowerShell
''' Applies bandwidth limits system-wide to all applications
''' </summary>
Public Class GlobalSpeedLimiter
    Private _isApplied As Boolean = False
    Private _policyName As String = "SL_NET_VPN_GlobalSpeedLimit"
    Private _interfaceName As String = ""

    Public Event LogMessage(message As String, isError As Boolean)

    Public ReadOnly Property IsApplied As Boolean
        Get
            Return _isApplied
        End Get
    End Property

    ''' <summary>
    ''' Apply global bandwidth limit using Windows QoS policies
    ''' </summary>
    ''' <param name="downloadLimitKBps">Download limit in KB/s (0 = unlimited)</param>
    ''' <param name="uploadLimitKBps">Upload limit in KB/s (0 = unlimited)</param>
    Public Function ApplyLimit(downloadLimitKBps As Integer, uploadLimitKBps As Integer) As Boolean
        Try
            ' First remove any existing policy
            RemoveLimit()

            If downloadLimitKBps <= 0 AndAlso uploadLimitKBps <= 0 Then
                Log("Global speed limit disabled (both limits are 0)")
                _isApplied = False
                Return True
            End If

            ' Get active network interface
            _interfaceName = GetActiveNetworkInterface()
            If String.IsNullOrEmpty(_interfaceName) Then
                Log("Failed to detect active network interface", True)
                Return False
            End If

            Log($"Using network interface: {_interfaceName}")

            ' Convert KB/s to bytes per second for QoS policy
            ' Note: Despite parameter name, Windows QoS expects BYTES per second, not bits
            Dim downloadBytes As Long = CLng(downloadLimitKBps) * 1024
            Dim uploadBytes As Long = CLng(uploadLimitKBps) * 1024
            Dim totalBytes As Long = downloadBytes + uploadBytes

            ' IMPORTANT: NetQoS can only throttle OUTBOUND traffic effectively
            ' Upload limit: Direct throttle of outbound data (WORKS WELL)
            ' Download limit: Throttle outbound ACKs (LIMITED EFFECTIVENESS)
            ' When both are set, we create a single policy for total bandwidth
            ' When only one is set, we use specific policies
            
            If uploadLimitKBps > 0 AndAlso downloadLimitKBps > 0 Then
                ' Both limits set - create combined policy for total bandwidth control
                Dim combinedPolicyName = $"{_policyName}_Combined"
                Dim psCommand = $"New-NetQosPolicy -Name '{combinedPolicyName}' -ThrottleRateActionBitsPerSecond {totalBytes} -NetworkProfile All"
                Dim result = RunPowerShellCommand(psCommand)
                
                If result.Contains("Error") OrElse result.Contains("Exception") Then
                    Log($"Failed to create combined QoS policy: {result}", True)
                    Return False
                End If
                
                Log($"Global speed limit applied: Download={downloadLimitKBps} KB/s, Upload={uploadLimitKBps} KB/s (Combined: {downloadLimitKBps + uploadLimitKBps} KB/s)")
            ElseIf uploadLimitKBps > 0 Then
                ' Only upload limit - straightforward outbound throttle
                Dim uploadPolicyName = $"{_policyName}_Upload"
                Dim psCommand = $"New-NetQosPolicy -Name '{uploadPolicyName}' -ThrottleRateActionBitsPerSecond {uploadBytes} -NetworkProfile All"
                Dim result = RunPowerShellCommand(psCommand)
                
                If result.Contains("Error") OrElse result.Contains("Exception") Then
                    Log($"Failed to create upload QoS policy: {result}", True)
                    Return False
                End If
                
                Log($"Global upload limit applied: {uploadLimitKBps} KB/s")
            ElseIf downloadLimitKBps > 0 Then
                ' Only download limit - throttle all outbound (which includes ACKs for downloads)
                Dim downloadPolicyName = $"{_policyName}_Download"
                Dim psCommand = $"New-NetQosPolicy -Name '{downloadPolicyName}' -ThrottleRateActionBitsPerSecond {downloadBytes} -NetworkProfile All"
                Dim result = RunPowerShellCommand(psCommand)
                
                If result.Contains("Error") OrElse result.Contains("Exception") Then
                    Log($"Failed to create download QoS policy: {result}", True)
                    Return False
                End If
                
                Log($"Global download limit applied: {downloadLimitKBps} KB/s")
            End If

            _isApplied = True
            Return True
        Catch ex As Exception
            Log($"Error applying global speed limit: {ex.Message}", True)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Remove global bandwidth limit
    ''' </summary>
    Public Function RemoveLimit() As Boolean
        Try
            ' Remove all possible policy variations
            Dim uploadPolicyName = $"{_policyName}_Upload"
            Dim downloadPolicyName = $"{_policyName}_Download"
            Dim combinedPolicyName = $"{_policyName}_Combined"
            
            Dim psCommand1 = $"Remove-NetQosPolicy -Name '{uploadPolicyName}' -Confirm:$false -ErrorAction SilentlyContinue"
            RunPowerShellCommand(psCommand1)
            
            Dim psCommand2 = $"Remove-NetQosPolicy -Name '{downloadPolicyName}' -Confirm:$false -ErrorAction SilentlyContinue"
            RunPowerShellCommand(psCommand2)
            
            Dim psCommand3 = $"Remove-NetQosPolicy -Name '{combinedPolicyName}' -Confirm:$false -ErrorAction SilentlyContinue"
            RunPowerShellCommand(psCommand3)
            
            ' Also remove old policy name if it exists
            Dim psCommand4 = $"Remove-NetQosPolicy -Name '{_policyName}' -Confirm:$false -ErrorAction SilentlyContinue"
            RunPowerShellCommand(psCommand4)
            
            _isApplied = False
            Log("Global speed limit removed")
            Return True
        Catch ex As Exception
            ' Silently ignore errors when removing (policy might not exist)
            _isApplied = False
            Return True
        End Try
    End Function

    ''' <summary>
    ''' Check if policy exists
    ''' </summary>
    Public Function CheckStatus() As String
        Try
            Dim uploadPolicyName = $"{_policyName}_Upload"
            Dim downloadPolicyName = $"{_policyName}_Download"
            Dim combinedPolicyName = $"{_policyName}_Combined"
            
            Dim psCommand = $"Get-NetQosPolicy -Name '{uploadPolicyName}','{downloadPolicyName}','{combinedPolicyName}' -ErrorAction SilentlyContinue | Select-Object Name, ThrottleRateActionBitsPerSecond | Format-List"
            Dim result = RunPowerShellCommand(psCommand)
            
            If String.IsNullOrWhiteSpace(result) OrElse result.Contains("does not exist") Then
                Return "No global speed limit policy found"
            End If
            
            Return result
        Catch ex As Exception
            Return "Error checking status"
        End Try
    End Function

    Private Function RunPowerShellCommand(command As String) As String
        Try
            Dim psi As New ProcessStartInfo()
            psi.FileName = "powershell.exe"
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command """ & command & """"
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True
            psi.Verb = "runas" ' Run as administrator

            Dim process As Process = Process.Start(psi)
            If process Is Nothing Then
                Return "Error: Failed to start PowerShell process"
            End If

            Using process
                Dim output = process.StandardOutput.ReadToEnd()
                Dim errorOutput = process.StandardError.ReadToEnd()
                process.WaitForExit(10000)

                If Not String.IsNullOrEmpty(errorOutput) Then
                    Return "Error: " & errorOutput
                End If

                Return output
            End Using
        Catch ex As Exception
            Return "Exception: " & ex.Message
        End Try
    End Function

    Private Sub Log(message As String, Optional isError As Boolean = False)
        RaiseEvent LogMessage(message, isError)
    End Sub

    ''' <summary>
    ''' Get the active network interface name
    ''' </summary>
    Private Function GetActiveNetworkInterface() As String
        Try
            Dim psCommand = "Get-NetAdapter | Where-Object {$_.Status -eq 'Up'} | Select-Object -First 1 -ExpandProperty Name"
            Dim result = RunPowerShellCommand(psCommand)
            Return result.Trim()
        Catch ex As Exception
            Return ""
        End Try
    End Function
End Class
