Imports System.Web
Imports System.Text.RegularExpressions

Public Class VLessParser
    ''' <summary>
    ''' Parse VLESS link into VLessConfig object
    ''' Example: vless://UUID@host:port?security=tls&sni=example.com&type=ws&path=/path#tag
    ''' </summary>
    Public Shared Function ParseVLessLink(link As String) As VLessConfig
        Try
            ' Remove 'vless://' prefix
            If Not link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) Then
                Throw New FormatException("Invalid VLESS link format")
            End If

            Dim cleanLink = link.Substring(8)
            Dim config As New VLessConfig()
            config.RawLink = link

            ' Split by '#' to get tag
            Dim tagParts = cleanLink.Split("#"c)
            If tagParts.Length > 1 Then
                config.Tag = HttpUtility.UrlDecode(tagParts(1))
            End If

            cleanLink = tagParts(0)

            ' Split by '?' to get query parameters
            Dim queryParts = cleanLink.Split("?"c)
            Dim hostPart = queryParts(0)

            ' Parse UUID@host:port
            Dim atIndex = hostPart.LastIndexOf("@"c)
            If atIndex > 0 Then
                config.UUID = hostPart.Substring(0, atIndex)
                Dim hostPortPart = hostPart.Substring(atIndex + 1)

                ' Split host:port
                Dim colonIndex = hostPortPart.LastIndexOf(":"c)
                If colonIndex > 0 Then
                    config.Host = hostPortPart.Substring(0, colonIndex)
                    If Integer.TryParse(hostPortPart.Substring(colonIndex + 1), config.Port) Then
                        ' Port parsed successfully
                    Else
                        config.Port = 443 ' Default VLESS port
                    End If
                Else
                    config.Host = hostPortPart
                    config.Port = 443
                End If
            End If

            ' Parse query parameters
            If queryParts.Length > 1 Then
                Dim queryString = queryParts(1)
                ParseQueryParameters(queryString, config)
            End If

            ' Set default values
            If String.IsNullOrEmpty(config.Security) Then
                config.Security = "tls"
            End If

            If String.IsNullOrEmpty(config.TransportType) Then
                config.TransportType = "tcp"
            End If

            Return config
        Catch ex As Exception
            Throw New Exception($"Failed to parse VLESS link: {ex.Message}")
        End Try
    End Function

    ''' <summary>
    ''' Parse query parameters from VLESS link
    ''' </summary>
    Private Shared Sub ParseQueryParameters(queryString As String, config As VLessConfig)
        Try
            ' Split by '&' to get individual parameters
            Dim parameters = queryString.Split("&"c)

            For Each param In parameters
                Dim parts = param.Split("="c)
                If parts.Length = 2 Then
                    Dim key = parts(0).ToLower()
                    Dim value = HttpUtility.UrlDecode(parts(1))

                    Select Case key
                        Case "security"
                            config.Security = value
                        Case "sni"
                            config.SNI = value
                        Case "type"
                            config.TransportType = value
                        Case "path"
                            config.Path = value
                        Case "host"
                            ' host parameter is used for WebSocket/HTTP headers, store in SNI if SNI is empty
                            If String.IsNullOrEmpty(config.SNI) Then
                                config.SNI = value
                            End If
                        Case "encryption"
                            ' Store encryption type (usually "none" for VLESS)
                        Case "fp", "fingerprint"
                            ' Fingerprint for TLS
                        Case "alpn"
                            ' ALPN settings
                        Case "allowinsecure"
                            ' Allow insecure connections
                    End Select
                End If
            Next
        Catch ex As Exception
            ' Silently ignore parsing errors
        End Try
    End Sub

    ''' <summary>
    ''' Generate VLESS link from config (with user-editable SNI)
    ''' </summary>
    Public Shared Function GenerateVLessLink(config As VLessConfig) As String
        Try
            Dim link As String = $"vless://{config.UUID}@{config.Host}:{config.Port}"

            Dim queryParams As New List(Of String)

            If Not String.IsNullOrEmpty(config.Security) Then
                queryParams.Add($"security={config.Security}")
            End If

            ' User can edit SNI
            If Not String.IsNullOrEmpty(config.SNI) Then
                queryParams.Add($"sni={HttpUtility.UrlEncode(config.SNI)}")
            End If

            If Not String.IsNullOrEmpty(config.TransportType) Then
                queryParams.Add($"type={config.TransportType}")
            End If

            If Not String.IsNullOrEmpty(config.Path) Then
                queryParams.Add($"path={HttpUtility.UrlEncode(config.Path)}")
            End If

            If queryParams.Count > 0 Then
                link &= "?" & String.Join("&", queryParams)
            End If

            If Not String.IsNullOrEmpty(config.Tag) Then
                link &= $"#{HttpUtility.UrlEncode(config.Tag)}"
            End If

            Return link
        Catch ex As Exception
            Return config.RawLink
        End Try
    End Function

    ''' <summary>
    ''' Validate VLESS configuration
    ''' </summary>
    Public Shared Function ValidateConfig(config As VLessConfig) As Boolean
        Try
            If String.IsNullOrEmpty(config.UUID) OrElse
               String.IsNullOrEmpty(config.Host) OrElse
               config.Port <= 0 OrElse config.Port > 65535 Then
                Return False
            End If

            ' Validate UUID format (should be valid UUID)
            If Not IsValidUUID(config.UUID) Then
                Return False
            End If

            Return True
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Check if string is valid UUID format
    ''' </summary>
    Private Shared Function IsValidUUID(uuid As String) As Boolean
        Try
            ' Try to parse as GUID
            Dim guidValue = Guid.Parse(uuid)
            Return True
        Catch
            Return False
        End Try
    End Function
End Class
