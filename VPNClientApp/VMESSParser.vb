Imports System.Web
Imports System.Text
Imports System.Text.Json

Public Class VMESSParser
    ''' <summary>
    ''' Parse VMESS link into VMESSConfig object
    ''' VMESS links are base64 encoded JSON
    ''' Example: vmess://base64_encoded_json
    ''' </summary>
    Public Shared Function ParseVMESSLink(link As String) As VMESSConfig
        Try
            ' Remove 'vmess://' prefix
            If Not link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) Then
                Throw New FormatException("Invalid VMESS link format")
            End If

            Dim base64Part = link.Substring(8).Trim()
            
            ' Decode base64
            Dim jsonBytes = Convert.FromBase64String(base64Part)
            Dim jsonString = Encoding.UTF8.GetString(jsonBytes)
            
            ' Parse JSON
            Dim config = ParseVMESSJson(jsonString)
            
            Return config
        Catch ex As Exception
            Throw New Exception($"Failed to parse VMESS link: {ex.Message}")
        End Try
    End Function

    ''' <summary>
    ''' Parse VMESS JSON configuration
    ''' </summary>
    Private Shared Function ParseVMESSJson(jsonString As String) As VMESSConfig
        Try
            Using doc = JsonDocument.Parse(jsonString)
                Dim root = doc.RootElement
                
                Dim config As New VMESSConfig()
                
                ' Parse required fields
                Dim hostProp As JsonElement
                If root.TryGetProperty("add", hostProp) Then
                    config.Host = hostProp.GetString()
                ElseIf root.TryGetProperty("address", hostProp) Then
                    config.Host = hostProp.GetString()
                End If
                
                Dim portProp As JsonElement
                If root.TryGetProperty("port", portProp) Then
                    If portProp.ValueKind = JsonValueKind.Number Then
                        config.Port = portProp.GetInt32()
                    ElseIf portProp.ValueKind = JsonValueKind.String Then
                        Integer.TryParse(portProp.GetString(), config.Port)
                    End If
                End If
                
                Dim idProp As JsonElement
                If root.TryGetProperty("id", idProp) Then
                    config.UUID = idProp.GetString()
                End If
                
                ' Parse optional fields
                Dim aidProp As JsonElement
                If root.TryGetProperty("aid", aidProp) Then
                    If aidProp.ValueKind = JsonValueKind.Number Then
                        config.AlterID = aidProp.GetInt32()
                    End If
                ElseIf root.TryGetProperty("alterId", aidProp) Then
                    If aidProp.ValueKind = JsonValueKind.Number Then
                        config.AlterID = aidProp.GetInt32()
                    End If
                End If
                
                Dim netProp As JsonElement
                If root.TryGetProperty("net", netProp) Then
                    config.Network = netProp.GetString()
                End If
                
                Dim scyProp As JsonElement
                If root.TryGetProperty("type", scyProp) Then
                    config.Security = scyProp.GetString()
                ElseIf root.TryGetProperty("scy", scyProp) Then
                    config.Security = scyProp.GetString()
                End If
                
                Dim pathProp As JsonElement
                If root.TryGetProperty("path", pathProp) Then
                    config.Path = pathProp.GetString()
                End If
                
                Dim tlsProp As JsonElement
                If root.TryGetProperty("tls", tlsProp) Then
                    config.TLS = tlsProp.GetString()
                ElseIf root.TryGetProperty("security", tlsProp) Then
                    config.TLS = tlsProp.GetString()
                End If
                
                Dim psProp As JsonElement
                If root.TryGetProperty("ps", psProp) Then
                    config.Tag = psProp.GetString()
                ElseIf root.TryGetProperty("remarks", psProp) Then
                    config.Tag = psProp.GetString()
                End If
                
                ' Set defaults
                If String.IsNullOrEmpty(config.Network) Then
                    config.Network = "tcp"
                End If
                
                If String.IsNullOrEmpty(config.Security) Then
                    config.Security = "auto"
                End If
                
                If String.IsNullOrEmpty(config.TLS) Then
                    config.TLS = "none"
                End If
                
                Return config
            End Using
        Catch ex As Exception
            Throw New Exception($"Failed to parse VMESS JSON: {ex.Message}")
        End Try
    End Function

    ''' <summary>
    ''' Generate VMESS link from config
    ''' </summary>
    Public Shared Function GenerateVMESSLink(config As VMESSConfig) As String
        Try
            ' Build JSON object
            Dim jsonObject As New Dictionary(Of String, Object) From {
                {"v", "2"},
                {"ps", config.Tag},
                {"add", config.Host},
                {"port", config.Port},
                {"id", config.UUID},
                {"aid", config.AlterID},
                {"net", config.Network},
                {"type", config.Security},
                {"path", config.Path},
                {"tls", config.TLS}
            }
            
            ' Serialize to JSON
            Dim jsonString = JsonSerializer.Serialize(jsonObject)
            
            ' Encode to base64
            Dim jsonBytes = Encoding.UTF8.GetBytes(jsonString)
            Dim base64 = Convert.ToBase64String(jsonBytes)
            
            Return "vmess://" & base64
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' Validate VMESS configuration
    ''' </summary>
    Public Shared Function ValidateConfig(config As VMESSConfig) As Boolean
        Try
            If String.IsNullOrEmpty(config.UUID) OrElse
               String.IsNullOrEmpty(config.Host) OrElse
               config.Port <= 0 OrElse config.Port > 65535 Then
                Return False
            End If

            ' Validate UUID format
            If Not IsValidUUID(config.UUID) Then
                Return False
            End If

            ' Validate AlterID range
            If config.AlterID < 0 OrElse config.AlterID > 65535 Then
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
            Dim guidValue = Guid.Parse(uuid)
            Return True
        Catch
            Return False
        End Try
    End Function
End Class
