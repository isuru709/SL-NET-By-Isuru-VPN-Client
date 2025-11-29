Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

Public Class AdBlockListManager
    Private ReadOnly _appDataDir As String
    Private ReadOnly _listsDir As String
    Private ReadOnly OisdSmallCandidates As String() = New String() {
        "https://small.oisd.nl/dnsmasq",
        "https://small.oisd.nl/hosts",
        "https://small.oisd.nl/abp",
        "https://small.oisd.nl/domains"
    }

    Private ReadOnly OisdMediumCandidates As String() = New String() {
        "https://easylist.to/easylist/easylist.txt",
        "https://secure.fanboy.co.nz/fanboy-cookiemonster.txt",
        "https://fanboy.co.nz/fanboy-cookiemonster.txt",
        "https://raw.githubusercontent.com/ryanbr/fanboy-adblock/master/fanboy-cookiemonster.txt"
    }

    Private ReadOnly OisdFullCandidates As String() = New String() {
        "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext",
        "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts"
    }

    ' EasyList family and Peter Lowe (explicit requests)
    Private ReadOnly EasyListCandidates As String() = New String() {
        "https://easylist.to/easylist/easylist.txt",
        "https://raw.githubusercontent.com/easylist/easylist/master/easylist/easylist.txt"
    }

    Private ReadOnly EasyPrivacyCandidates As String() = New String() {
        "https://easylist.to/easylist/easyprivacy.txt",
        "https://raw.githubusercontent.com/easylist/easylist/master/easyprivacy/easyprivacy.txt"
    }

    Private ReadOnly PeterLoweCandidates As String() = New String() {
        "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext"
    }

    ' uBlock Origin maintained filters (ads, trackers, fixes)
    Private ReadOnly UblockCandidates As String() = New String() {
        "https://ublockorigin.github.io/uAssets/filters/filters.txt",
        "https://ublockorigin.github.io/uAssets/filters/privacy.txt",
        "https://ublockorigin.github.io/uAssets/filters/badware.txt",
        "https://ublockorigin.github.io/uAssets/filters/resource-abuse.txt",
        "https://ublockorigin.github.io/uAssets/filters/unbreak.txt",
        "https://ublockorigin.github.io/uAssets/filters/quick-fixes.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/privacy.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/badware.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/resource-abuse.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/unbreak.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/quick-fixes.txt"
    }

    Public Sub New()
        Dim appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        _appDataDir = Path.Combine(appDataPath, "VPNClientApp")
        _listsDir = Path.Combine(_appDataDir, "blocklists")
        If Not Directory.Exists(_listsDir) Then Directory.CreateDirectory(_listsDir)
    End Sub

    Public Async Function UpdateListsAsync(settings As BlockerSettings, log As Action(Of String)) As Task(Of Integer)
        Dim total As Integer = 0
        Dim merged As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        If settings Is Nothing Then Return 0

        ' Ads category: include requested public lists when Ads is enabled
        If settings.AdsEnabled Then
            ' EasyList
            Dim easyListPath = Path.Combine(_listsDir, "easylist.txt")
            Dim elCount As Integer = 0
            For Each url In EasyListCandidates
                elCount = Await FetchDomainListAsync(url, easyListPath, log)
                If elCount > 0 Then
                    log?.Invoke($"EasyList loaded from: {url}")
                    Exit For
                End If
            Next
            For Each d In LoadDomainsFromFile(easyListPath)
                merged.Add(d)
            Next
            total += elCount

            ' EasyPrivacy
            Dim easyPrivacyPath = Path.Combine(_listsDir, "easyprivacy.txt")
            Dim epCount As Integer = 0
            For Each url In EasyPrivacyCandidates
                epCount = Await FetchDomainListAsync(url, easyPrivacyPath, log)
                If epCount > 0 Then
                    log?.Invoke($"EasyPrivacy loaded from: {url}")
                    Exit For
                End If
            Next
            For Each d In LoadDomainsFromFile(easyPrivacyPath)
                merged.Add(d)
            Next
            total += epCount

            ' Peter Lowe
            Dim peterPath = Path.Combine(_listsDir, "peterlowe-hosts.txt")
            Dim plCount As Integer = 0
            For Each url In PeterLoweCandidates
                plCount = Await FetchDomainListAsync(url, peterPath, log)
                If plCount > 0 Then
                    log?.Invoke($"Peter Lowe list loaded from: {url}")
                    Exit For
                End If
            Next
            For Each d In LoadDomainsFromFile(peterPath)
                merged.Add(d)
            Next
            total += plCount

            ' uBlock filters (multiple files)
            Dim ublockAddedBefore = merged.Count
            Dim idx As Integer = 0
            While idx < UblockCandidates.Length
                Dim url = UblockCandidates(idx)
                Dim name As String
                Try
                    Dim u = New Uri(url)
                    name = Path.GetFileName(u.LocalPath)
                    If String.IsNullOrEmpty(name) Then name = $"uassets-{idx}.txt"
                Catch
                    name = $"uassets-{idx}.txt"
                End Try
                Dim save = Path.Combine(_listsDir, $"ublock-{name}")
                Dim cnt = Await FetchDomainListAsync(url, save, log)
                If cnt > 0 Then
                    For Each d In LoadDomainsFromFile(save)
                        merged.Add(d)
                    Next
                End If
                idx += 1
            End While
            Dim uAdded = merged.Count - ublockAddedBefore
            If uAdded > 0 Then
                total += uAdded
                log?.Invoke($"uBlock filters merged: {uAdded} domains")
            End If
        End If

        If settings.UseOisdSmall Then
            Dim savedPath = Path.Combine(_listsDir, "oisd-small.txt")
            Dim count As Integer = 0
            For Each url In OisdSmallCandidates
                count = Await FetchDomainListAsync(url, savedPath, log)
                If count > 0 Then
                    log?.Invoke($"OISD small loaded from: {url}")
                    Exit For
                End If
            Next
            Dim list = LoadDomainsFromFile(savedPath)
            For Each d In list
                merged.Add(d)
            Next
            total += count
        End If

        If settings.UseOisdMedium Then
            Dim savedPath = Path.Combine(_listsDir, "easylist.txt")
            Dim count As Integer = 0
            For Each url In OisdMediumCandidates
                count = Await FetchDomainListAsync(url, savedPath, log)
                If count > 0 Then
                    log?.Invoke($"EasyList (Medium) loaded from: {url}")
                    Exit For
                End If
            Next
            Dim list = LoadDomainsFromFile(savedPath)
            For Each d In list
                merged.Add(d)
            Next
            total += count
        End If

        If settings.UseOisdFull Then
            Dim savedPath = Path.Combine(_listsDir, "fullhosts.txt")
            Dim count As Integer = 0
            For Each url In OisdFullCandidates
                count = Await FetchDomainListAsync(url, savedPath, log)
                If count > 0 Then
                    log?.Invoke($"Full blocklist loaded from: {url}")
                    Exit For
                End If
            Next
            Dim list = LoadDomainsFromFile(savedPath)
            For Each d In list
                merged.Add(d)
            Next
            total += count
        End If

        ' Fallback: if Medium/Full selected but nothing merged, try Small automatically
        If merged.Count = 0 AndAlso (settings.UseOisdMedium OrElse settings.UseOisdFull) Then
            log?.Invoke("Selected lists returned no data; falling back to OISD small")
            Dim savedPath = Path.Combine(_listsDir, "oisd-small.txt")
            Dim count As Integer = 0
            For Each url In OisdSmallCandidates
                count = Await FetchDomainListAsync(url, savedPath, log)
                If count > 0 Then
                    log?.Invoke($"Fallback OISD small loaded from: {url}")
                    Exit For
                End If
            Next
            Dim list = LoadDomainsFromFile(savedPath)
            For Each d In list
                merged.Add(d)
            Next
            total += count
        End If

        ' Integrate local uBlock Origin assets (if provided in workspace) for better coverage
        Try
            If settings.AdsEnabled Then
                Dim added = MergeLocalUboAssets(merged, log)
                If added > 0 Then
                    log?.Invoke($"Added {added} domain(s) from local uBO assets")
                    total += added
                End If
            End If
        Catch ex As Exception
            log?.Invoke($"Local uBO assets merge skipped: {ex.Message}")
        End Try

        ' Save merged only if we have updates; otherwise keep previous file
        Dim mergedPath = GetMergedPath()
        If merged.Count > 0 Then
            File.WriteAllLines(mergedPath, merged.OrderBy(Function(s) s), Encoding.UTF8)
            Return merged.Count
        Else
            ' No updates from selected sources; keep previous merged list if it exists
            If File.Exists(mergedPath) Then
                Try
                    Dim existing = 0
                    Using sr As New StreamReader(mergedPath)
                        While sr.ReadLine() IsNot Nothing
                            existing += 1
                        End While
                    End Using
                    log?.Invoke($"No lists updated; keeping previous merged list ({existing} domains)")
                    Return existing
                Catch
                    ' Fall through to empty
                End Try
            End If
            log?.Invoke("No lists available and no previous merged list found")
            Return 0
        End If
    End Function

    Private Function MergeLocalUboAssets(merged As HashSet(Of String), log As Action(Of String)) As Integer
        Dim baseDir As String = AppDomain.CurrentDomain.BaseDirectory
        Dim uboDir As String = Path.Combine(baseDir, "u block origine", "assets")
        If Not Directory.Exists(uboDir) Then
            ' Try relative to project root
            Dim exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
            uboDir = Path.Combine(exeDir, "u block origine", "assets")
        End If
        If Not Directory.Exists(uboDir) Then Return 0

        Dim candidates As New List(Of String)
        ' Third-party EasyList/EasyPrivacy
        Dim third = Path.Combine(uboDir, "thirdparties", "easylist")
        candidates.Add(Path.Combine(third, "easylist.txt"))
        candidates.Add(Path.Combine(third, "easyprivacy.txt"))
        ' uBlock core filters (minified)
        Dim ublockCore = Path.Combine(uboDir, "ublock")
        candidates.Add(Path.Combine(ublockCore, "filters.min.txt"))
        candidates.Add(Path.Combine(ublockCore, "privacy.min.txt"))
        candidates.Add(Path.Combine(ublockCore, "quick-fixes.min.txt"))
        candidates.Add(Path.Combine(ublockCore, "unbreak.min.txt"))
        candidates.Add(Path.Combine(ublockCore, "badlists.txt"))

        Dim before = merged.Count
        For Each assetPath In candidates
            Try
                If System.IO.File.Exists(assetPath) Then
                    Dim content = System.IO.File.ReadAllText(assetPath)
                    Dim list = ParseToDomains(content)
                    For Each d In list
                        merged.Add(d)
                    Next
                    log?.Invoke($"Parsed {list.Count} item(s) from {Path.GetFileName(assetPath)}")
                End If
            Catch ex As Exception
                log?.Invoke($"uBO asset parse failed for {assetPath}: {ex.Message}")
            End Try
        Next

        Return merged.Count - before
    End Function

    Public Function GetMergedPath() As String
        Return Path.Combine(_listsDir, "merged.txt")
    End Function

    Public Function LoadMergedDomains(Optional maxCount As Integer = 8000) As List(Of String)
        Try
            Dim p = GetMergedPath()
            If Not File.Exists(p) Then Return New List(Of String)
            Dim all = File.ReadAllLines(p)
            Dim domains = New List(Of String)
            For Each line In all
                Dim d = NormalizeDomain(line)
                If Not String.IsNullOrWhiteSpace(d) Then domains.Add(d)
                If domains.Count >= maxCount Then Exit For
            Next
            Return domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Catch
            Return New List(Of String)
        End Try
    End Function

    Private Async Function FetchDomainListAsync(url As String, savePath As String, log As Action(Of String)) As Task(Of Integer)
        Try
            Using handler As New HttpClientHandler()
                handler.UseProxy = False
                ' Many blocklists are served compressed; enable automatic decompression
                handler.AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate Or DecompressionMethods.Brotli
                Using hc As New HttpClient(handler)
                    hc.Timeout = TimeSpan.FromSeconds(45)
                    hc.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VPNClientApp/1.0")
                    hc.DefaultRequestHeaders.Accept.ParseAdd("text/plain, */*; q=0.8")
                    hc.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br")

                    Dim resp = Await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    resp.EnsureSuccessStatusCode()
                    Dim data = Await resp.Content.ReadAsStringAsync()

                    Dim lines = ParseToDomains(data)

                    ' If parsing produced 0 domains, try to detect if content is not the expected list (e.g., HTML) and warn
                    If lines.Count = 0 AndAlso Not String.IsNullOrWhiteSpace(data) Then
                        ' Keep a snapshot for debugging
                        Try
                            File.WriteAllText(Path.Combine(_listsDir, "oisd-last-response.txt"), data)
                        Catch
                        End Try
                    End If

                    File.WriteAllLines(savePath, lines, Encoding.UTF8)
                    log?.Invoke($"Downloaded {lines.Count} domains from {url}")
                    Return lines.Count
                End Using
            End Using
        Catch ex As Exception
            log?.Invoke($"Blocklist fetch failed from {url}: {ex.Message}")
            Return 0
        End Try
    End Function

    Private Function LoadDomainsFromFile(path As String) As IEnumerable(Of String)
        Try
            If Not File.Exists(path) Then Return Enumerable.Empty(Of String)()
            Dim out As New List(Of String)
            For Each line In File.ReadAllLines(path)
                Dim d = NormalizeDomain(line)
                If Not String.IsNullOrWhiteSpace(d) Then out.Add(d)
            Next
            Return out
        Catch
            Return Enumerable.Empty(Of String)()
        End Try
    End Function

    Private Function ParseToDomains(content As String) As List(Of String)
        Dim output As New List(Of String)
        Using sr As New StringReader(content)
            While True
                Dim line = sr.ReadLine()
                If line Is Nothing Then Exit While
                ' Skip common comment prefixes used by some lists
                If line.StartsWith("!") OrElse line.StartsWith("#") OrElse line.StartsWith("[") Then
                    Continue While
                End If

                ' Skip ABP exception rules ("@@") entirely
                If line.StartsWith("@@") Then
                    Continue While
                End If

                ' If rule contains options (e.g., "$image,script"), strip them for host extraction
                Dim optIdx = line.IndexOf("$"c)
                If optIdx > 0 Then
                    line = line.Substring(0, optIdx)
                End If

                Dim d = NormalizeDomain(line)
                If Not String.IsNullOrWhiteSpace(d) Then output.Add(d)
            End While
        End Using
        Return output.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    Private Function NormalizeDomain(input As String) As String
        If String.IsNullOrWhiteSpace(input) Then Return String.Empty
        Dim s = input.Trim()
        ' Remove comments and hosts file IPs
        Dim hash = s.IndexOf("#"c)
        If hash >= 0 Then s = s.Substring(0, hash)
        s = s.Trim()
        If s.Length = 0 Then Return String.Empty

        ' dnsmasq style: address=/domain/ or server=/domain/
        If s.Contains("address=/") OrElse s.Contains("server=/") Then
            Dim firstSlash = s.IndexOf("/"c)
            If firstSlash >= 0 Then
                Dim secondSlash = s.IndexOf("/"c, firstSlash + 1)
                If secondSlash > firstSlash Then
                    s = s.Substring(firstSlash + 1, secondSlash - firstSlash - 1)
                End If
            End If
        End If
        ' If hosts format (e.g., 0.0.0.0 domain.com)
        Dim parts = Regex.Split(s, "\s+")
        If parts.Length >= 2 AndAlso (parts(0) = "0.0.0.0" OrElse parts(0) = "127.0.0.1") Then
            s = parts(1)
        End If
        s = s.Trim().ToLowerInvariant()
        If s.StartsWith("http://") OrElse s.StartsWith("https://") Then
            Try
                Dim u = New Uri(s)
                s = u.Host
            Catch
            End Try
        End If
        If s.StartsWith("*.") Then s = s.Substring(2)
        s = s.Trim("."c)
        If s.Length = 0 Then Return String.Empty
        ' Filter out rule-style prefixes like ||domain^ or 0.0.0.0	
        If s.StartsWith("||") Then s = s.Substring(2)
        If s.EndsWith("^") Then s = s.TrimEnd("^"c)

        ' basic domain validation
        If s.Contains(" ") OrElse s.Contains("/") OrElse s.Contains("^") Then Return String.Empty
        ' Exclude pure IP addresses
        If IPAddress.TryParse(s, Nothing) Then Return String.Empty
        Return s
    End Function
End Class
