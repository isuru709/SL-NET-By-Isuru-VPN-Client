Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Text.Json
Imports System.Diagnostics
Imports System.Net
Imports System.Security.Authentication
Imports System.Linq

Public Class XrayUpdater

    ' Pin to last Xray-core version that supports allowInsecure (v26+ removed it)
    ' Do NOT update this without verifying allowInsecure compatibility
    Private Const PINNED_VERSION As String = "v25.12.18"
    Private Const PINNED_DOWNLOAD_URL As String = "https://github.com/XTLS/Xray-core/releases/download/v25.12.18/Xray-windows-64.zip"


    Public Class UpdateResult
        Public Property Success As Boolean
        Public Property Version As String
        Public Property ErrorMessage As String
    End Class

    Private Shared ReadOnly Http As HttpClient = CreateHttp(allowInsecureCRL:=False)

    Private Shared Function CreateHttp(Optional allowInsecureCRL As Boolean = False) As HttpClient
        Try
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
        Catch
        End Try

        Dim webProxy = WebRequest.DefaultWebProxy
        If webProxy IsNot Nothing Then
            Try
                webProxy.Credentials = CredentialCache.DefaultCredentials
            Catch
            End Try
        End If

        Dim handler As New HttpClientHandler() With {
            .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate,
            .UseProxy = True,
            .Proxy = webProxy
        }

        Try
            handler.SslProtocols = SslProtocols.Tls12
        Catch
        End Try

        Try
            handler.CheckCertificateRevocationList = Not allowInsecureCRL
        Catch
        End Try

        Dim client As New HttpClient(handler)
        If Not client.DefaultRequestHeaders.Contains("User-Agent") Then
            client.DefaultRequestHeaders.Add("User-Agent", "VPNClientApp-XrayUpdater")
        End If
        Return client
    End Function

    Public Sub New()
        If Not Http.DefaultRequestHeaders.Contains("User-Agent") Then
            Http.DefaultRequestHeaders.Add("User-Agent", "VPNClientApp-XrayUpdater")
        End If
    End Sub

    Public Async Function UpdateXrayAsync(log As Action(Of String), Optional beforeInstall As Action = Nothing, Optional provideLocalZip As Func(Of String) = Nothing) As Task(Of UpdateResult)
        Dim result As New UpdateResult()
        Dim appDir = AppContext.BaseDirectory
        Dim xrayExe = Path.Combine(appDir, "xray.exe")

        Try
            ' Use pinned version to avoid breaking changes in newer Xray-core
            ' (v26+ removed allowInsecure which is needed for servers with expired certs)
            log?.Invoke($"Using pinned Xray-core version: {PINNED_VERSION}")
            log?.Invoke("(Newer versions removed allowInsecure support, which breaks many servers)")
            Dim latest As New ReleaseInfo With {
                .Version = PINNED_VERSION,
                .DownloadUrl = PINNED_DOWNLOAD_URL
            }

            result.Version = latest.Version
            log?.Invoke($"Target version: {result.Version}")

            ' Prepare temp dir
            Dim tempDir = Path.Combine(Path.GetTempPath(), "VPNClientApp_XrayUpdate")
            If Directory.Exists(tempDir) Then
                Try
                    Directory.Delete(tempDir, recursive:=True)
                Catch
                End Try
            End If
            Directory.CreateDirectory(tempDir)

            Dim zipPath = Path.Combine(tempDir, "xray_windows_64.zip")
            log?.Invoke("Downloading Xray-core (Windows x64)…")
            Dim downloaded As Boolean = Await TryDownloadAsync(latest.DownloadUrl, zipPath, log)
            If Not downloaded Then
                If provideLocalZip IsNot Nothing Then
                    log?.Invoke("Network download failed. Please select a previously downloaded Xray-windows-64.zip…")
                    Dim localPath As String = Nothing
                    Try
                        localPath = provideLocalZip()
                    Catch
                    End Try
                    If Not String.IsNullOrEmpty(localPath) AndAlso File.Exists(localPath) Then
                        zipPath = localPath
                    Else
                        result.Success = False
                        result.ErrorMessage = "Download failed and no local package selected."
                        Return result
                    End If
                Else
                    result.Success = False
                    result.ErrorMessage = "Download failed due to SSL/Network error."
                    Return result
                End If
            End If

            ' Extract
            log?.Invoke("Extracting package…")
            ZipFile.ExtractToDirectory(zipPath, tempDir)

            ' Find extracted xray.exe
            Dim candidateExe = Directory.EnumerateFiles(tempDir, "xray.exe", SearchOption.AllDirectories).FirstOrDefault()
            If String.IsNullOrEmpty(candidateExe) Then
                result.Success = False
                result.ErrorMessage = "Downloaded package did not contain xray.exe."
                Return result
            End If

            ' Ask caller to disconnect just before install
            Try
                beforeInstall?.Invoke()
            Catch
            End Try

            ' Kill running xray processes
            Try
                For Each p In Process.GetProcessesByName("xray")
                    Try
                        p.Kill()
                        p.WaitForExit(3000)
                    Catch
                    End Try
                Next
            Catch
            End Try

            ' Install files
            log?.Invoke("Installing xray.exe…")
            File.Copy(candidateExe, xrayExe, True)

            Dim geoip = Directory.EnumerateFiles(tempDir, "geoip.dat", SearchOption.AllDirectories).FirstOrDefault()
            If Not String.IsNullOrEmpty(geoip) Then
                Try
                    File.Copy(geoip, Path.Combine(appDir, "geoip.dat"), True)
                Catch
                End Try
            End If
            Dim geosite = Directory.EnumerateFiles(tempDir, "geosite.dat", SearchOption.AllDirectories).FirstOrDefault()
            If Not String.IsNullOrEmpty(geosite) Then
                Try
                    File.Copy(geosite, Path.Combine(appDir, "geosite.dat"), True)
                Catch
                End Try
            End If

            result.Success = True
            log?.Invoke("Xray-core installed successfully.")
            Return result
        Catch ex As Exception
            result.Success = False
            result.ErrorMessage = ex.Message
            Return result
        End Try
    End Function

    Private Class ReleaseInfo
        Public Property Version As String
        Public Property DownloadUrl As String
    End Class

    Private Async Function TryDownloadAsync(url As String, toPath As String, log As Action(Of String)) As Task(Of Boolean)
        ' Attempt 1: normal client
        Dim firstError As Exception = Nothing
        Dim isSslIssue As Boolean = False
        Try
            Using resp = Await Http.GetAsync(url)
                resp.EnsureSuccessStatusCode()
                Using fs = New FileStream(toPath, FileMode.Create, FileAccess.Write, FileShare.None)
                    Await resp.Content.CopyToAsync(fs)
                End Using
            End Using
            Return True
        Catch ex As Exception
            firstError = ex
            isSslIssue = TypeOf ex Is HttpRequestException OrElse (ex.InnerException IsNot Nothing AndAlso ex.InnerException.Message.ToLower().Contains("ssl"))
        End Try

        If isSslIssue Then
            log?.Invoke("SSL/CRL error detected. Retrying without CRL check…")
            Try
                Using insecureClient = CreateHttp(allowInsecureCRL:=True)
                    If Not insecureClient.DefaultRequestHeaders.Contains("User-Agent") Then
                        insecureClient.DefaultRequestHeaders.Add("User-Agent", "VPNClientApp-XrayUpdater")
                    End If
                    Using resp = Await insecureClient.GetAsync(url)
                        resp.EnsureSuccessStatusCode()
                        Using fs = New FileStream(toPath, FileMode.Create, FileAccess.Write, FileShare.None)
                            Await resp.Content.CopyToAsync(fs)
                        End Using
                    End Using
                    Return True
                End Using
            Catch ex2 As Exception
                log?.Invoke($"Second attempt failed: {ex2.Message}")
                Return False
            End Try
        Else
            log?.Invoke($"Download failed: {firstError.Message}")
            Return False
        End If
    End Function

    Private Async Function GetLatestReleaseAsync() As Task(Of ReleaseInfo)
        Dim api = "https://api.github.com/repos/XTLS/Xray-core/releases/latest"
        Using resp = Await Http.GetAsync(api)
            resp.EnsureSuccessStatusCode()
            Dim json = Await resp.Content.ReadAsStringAsync()

            Using doc = JsonDocument.Parse(json)
                Dim root = doc.RootElement
                Dim tagEl As JsonElement
                Dim tag As String = Nothing
                If root.TryGetProperty("tag_name", tagEl) Then
                    tag = tagEl.GetString()
                End If
                Dim assets = root.GetProperty("assets")
                Dim download As String = Nothing

                For Each asset In assets.EnumerateArray()
                    Dim name = asset.GetProperty("name").GetString()
                    If name IsNot Nothing AndAlso name.ToLower().Contains("windows") AndAlso name.ToLower().Contains("64") AndAlso name.ToLower().EndsWith(".zip") Then
                        download = asset.GetProperty("browser_download_url").GetString()
                        Exit For
                    End If
                Next

                If String.IsNullOrEmpty(download) Then
                    For Each asset In assets.EnumerateArray()
                        Dim name = asset.GetProperty("name").GetString()
                        If name IsNot Nothing AndAlso name.ToLower().Contains("windows-64") AndAlso name.ToLower().EndsWith(".zip") Then
                            download = asset.GetProperty("browser_download_url").GetString()
                            Exit For
                        End If
                    Next
                End If

                If String.IsNullOrEmpty(download) Then Return Nothing
                Return New ReleaseInfo With {.Version = tag, .DownloadUrl = download}
            End Using
        End Using
    End Function
End Class
