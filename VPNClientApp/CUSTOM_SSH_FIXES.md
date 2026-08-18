# Custom SSH Connection Fixes

## Problem Summary
User reported that custom SSH is not connecting, but HTTP proxy injector combined with Proxifier connects SSH properly. This indicates the issue was with the custom SSH implementation in the VPN client.

## Root Cause Analysis

### 1. **Missing Private Key Authentication Support**
The `StartOptimizedSSHTunnelAsync` function only supported password authentication. When users tried to use SSH key authentication (by checking "Use Private Key" checkbox), the code completely ignored the key files and always tried password authentication.

**Code Issue** ([VPNConnectionManager.vb](VPNConnectionManager.vb#L646-684)):
```vb
' OLD CODE - Only password auth:
Dim methods As New List(Of AuthenticationMethod)
If Not String.IsNullOrEmpty(config.Password) Then
    methods.Add(New PasswordAuthenticationMethod(config.Username, config.Password))
End If
```

### 2. **UseTLS Always Disabled for Custom SSH**
The custom SSH configuration always set `UseTLS = False` ([MainWindow.xaml.vb](MainWindow.xaml.vb#L2113)), which prevented TLS wrapping for SSH connections. This is critical when connecting to SSH servers behind TLS tunnels (common in HTTP proxy injector setups).

**Code Issue**:
```vb
' OLD CODE:
.UseTLS = False,  ' Always disabled!
```

### 3. **Poor Error Handling**
The SSH connection code didn't distinguish between different failure types:
- Authentication failures (wrong password/key)
- Connection failures (network issues, wrong host/port)
- Configuration errors (missing key file)

All errors were logged generically as "SSH.NET tunnel start failed", making troubleshooting impossible.

### 4. **Missing TLS Option in UI**
Users had no way to enable/disable TLS wrapping for custom SSH connections, even though the backend supported it. HTTP proxy injectors typically require TLS wrapping, which is why they work but direct custom SSH didn't.

## Fixes Applied

### 1. **Added Private Key Authentication** ([VPNConnectionManager.vb](VPNConnectionManager.vb#L648-738))
```vb
' Add authentication methods based on configuration
If config.UseKeyAuth Then
    ' Private key authentication
    If String.IsNullOrEmpty(config.PrivateKeyPath) OrElse Not IO.File.Exists(config.PrivateKeyPath) Then
        Log("SSH private key file not found", True)
        Return False
    End If
    
    Try
        Dim keyFile As PrivateKeyFile
        If Not String.IsNullOrEmpty(config.Passphrase) Then
            ' Encrypted key with passphrase
            keyFile = New PrivateKeyFile(config.PrivateKeyPath, config.Passphrase)
            Log("Using encrypted private key with passphrase")
        Else
            ' Unencrypted key
            keyFile = New PrivateKeyFile(config.PrivateKeyPath)
            Log("Using unencrypted private key")
        End If
        methods.Add(New PrivateKeyAuthenticationMethod(config.Username, keyFile))
    Catch ex As Exception
        Log($"Failed to load private key: {ex.Message}", True)
        Return False
    End Try
Else
    ' Password authentication
    If String.IsNullOrEmpty(config.Password) Then
        Log("SSH password is empty", True)
        Return False
    End If
    methods.Add(New PasswordAuthenticationMethod(config.Username, config.Password))
    Log("Using password authentication")
End If
```

**Benefits**:
- ✅ Supports both password and private key authentication
- ✅ Handles encrypted private keys with passphrases
- ✅ Validates key file existence before attempting connection
- ✅ Clear logging of which authentication method is being used

### 2. **Enabled TLS Wrapping for Custom SSH** ([MainWindow.xaml.vb](MainWindow.xaml.vb#L2098-2113))
```vb
' NEW CODE:
Dim useTLS = UseSSHTLSCheck.IsChecked.GetValueOrDefault(True)
Dim sshConfig As New SSHTLSConfig With {
    ...
    .UseTLS = useTLS,  ' Now controlled by checkbox!
    ...
}
```

**Benefits**:
- ✅ TLS wrapping now enabled by default (matches HTTP proxy injector behavior)
- ✅ Users can disable TLS for standard SSH (port 22)
- ✅ Automatically detects raw SSH and skips TLS when not needed

### 3. **Improved Error Handling** ([VPNConnectionManager.vb](VPNConnectionManager.vb#L726-738))
```vb
Catch authEx As Renci.SshNet.Common.SshAuthenticationException
    Log($"SSH authentication failed: {authEx.Message}. Check username/password or private key.", True)
    Return False
Catch connEx As Renci.SshNet.Common.SshConnectionException
    Log($"SSH connection error: {connEx.Message}. Check host/port and network.", True)
    Return False
Catch ex As Exception
    Log($"SSH.NET tunnel start failed: {ex.Message}", True)
    Return False
```

**Benefits**:
- ✅ Specific error messages for authentication failures
- ✅ Separate handling for connection vs authentication issues
- ✅ Clear guidance on what to check for each error type
- ✅ Better debugging with detailed logs

### 4. **Added TLS Checkbox to UI** ([MainWindow.xaml](MainWindow.xaml#L611-615))
```xml
<TextBlock Text="Connection Options" FontWeight="SemiBold" FontSize="13" 
           Foreground="{StaticResource TextSecondaryBrush}" Margin="0,0,0,8"/>
<CheckBox Name="UseSSHTLSCheck" 
          Content="Enable TLS wrapping (recommended for non-standard SSH ports)" 
          IsChecked="True" Margin="0,0,0,4"/>
<TextBlock Text="Tip: Disable TLS if connecting to standard SSH (port 22)" 
           FontSize="11" Foreground="#666" Margin="20,0,0,0" TextWrapping="Wrap"/>
```

**Benefits**:
- ✅ Clear UI control for TLS wrapping
- ✅ Checked by default (matches HTTP proxy injector behavior)
- ✅ Helpful tooltip explaining when to disable
- ✅ Positioned logically after SNI field

## How It Works Now

### Connection Flow
1. **User enters SSH credentials**:
   - Host, Port, Username
   - Password OR Private Key file path (with optional passphrase)
   
2. **Configure options**:
   - SNI (optional, for SNI routing)
   - Transport Type (Direct TCP, WebSocket, gRPC, HTTP/2)
   - **TLS Wrapping** (new checkbox, checked by default)

3. **Connection attempt**:
   - Validates authentication method (password or key)
   - Probes for raw SSH banner on target port
   - Enables TLS tunnel if `UseSSHTLS` is checked AND no raw SSH detected
   - Falls back to TLS tunnel if direct connection fails
   - Starts SSH dynamic SOCKS proxy
   - Bridges SSH SOCKS to Xray HTTP/SOCKS proxies
   - Configures system proxy

### Authentication Methods Supported
✅ **Password authentication**:
- Username + Password
- Most common for SSH servers

✅ **Private key authentication**:
- Username + Private Key file (PEM/OpenSSH format)
- Optional passphrase for encrypted keys
- Supports RSA, DSA, ECDSA, Ed25519 keys

✅ **TLS wrapping**:
- Automatic TLS tunnel for SSH-over-TLS servers
- Configurable via checkbox (on by default)
- Auto-detection of raw SSH vs TLS-wrapped SSH

### Error Messages
Now users get specific error messages:

❌ **Authentication Failed**:
```
SSH authentication failed: Permission denied (publickey,password). 
Check username/password or private key.
```

❌ **Connection Failed**:
```
SSH connection error: Connection refused. 
Check host/port and network.
```

❌ **Key File Error**:
```
Failed to load private key: Invalid private key file format.
```

## Comparison with HTTP Proxy Injector

### Why HTTP Proxy Injector + Proxifier Works
HTTP proxy injectors:
1. **Enable TLS wrapping by default** ✅ (Now fixed in our implementation)
2. Support both password and key authentication ✅ (Now fixed)
3. Provide clear error messages ✅ (Now fixed)
4. Handle TLS tunnel fallback automatically ✅ (Already implemented)

### Why Custom SSH Failed Before
1. ❌ TLS wrapping was always disabled
2. ❌ Only password authentication supported (no keys)
3. ❌ Generic error messages
4. ❌ No UI control for TLS option

### After Fixes
Our custom SSH implementation now matches or exceeds HTTP proxy injector functionality:
- ✅ TLS wrapping enabled by default
- ✅ Full key authentication support
- ✅ Better error handling
- ✅ User-friendly UI controls
- ✅ Automatic fallback mechanisms
- ✅ Same connection flow as HTTP injectors

## Testing Instructions

### Test 1: Password Authentication with TLS
1. Open Custom SSH tab
2. Enter: Host, Port, Username, Password
3. Ensure "Enable TLS wrapping" is **checked**
4. Click Connect
5. **Expected**: Connection succeeds, logs show "Using password authentication" and "TLS tunnel enabled"

### Test 2: Private Key Authentication
1. Open Custom SSH tab
2. Enter: Host, Port, Username
3. Check "Use Private Key (instead of password)"
4. Browse and select private key file (.pem, .key, or OpenSSH format)
5. Enter passphrase if key is encrypted (optional)
6. Ensure "Enable TLS wrapping" is **checked**
7. Click Connect
8. **Expected**: Connection succeeds, logs show "Using encrypted/unencrypted private key"

### Test 3: Standard SSH (Port 22) Without TLS
1. Open Custom SSH tab
2. Enter: Host, Port=22, Username, Password
3. **Uncheck** "Enable TLS wrapping"
4. Click Connect
5. **Expected**: Connection succeeds, logs show "Raw SSH banner detected: using direct SSH"

### Test 4: Error Handling
1. Try connecting with wrong password
2. **Expected**: Error message: "SSH authentication failed... Check username/password"
3. Try connecting with invalid host
4. **Expected**: Error message: "SSH connection error... Check host/port and network"

## Files Modified
1. [VPNConnectionManager.vb](VPNConnectionManager.vb) - Added key authentication and error handling
2. [MainWindow.xaml.vb](MainWindow.xaml.vb) - Enabled UseTLS checkbox binding
3. [MainWindow.xaml](MainWindow.xaml) - Added TLS checkbox to UI

## Build Status
✅ Project builds successfully with no errors

## Additional Notes

### Private Key Format Support
The SSH.NET library supports:
- OpenSSH format (modern default)
- PEM format (legacy but common)
- PuTTY format (.ppk) - requires conversion to OpenSSH/PEM first

### TLS Auto-Detection
The connection logic automatically:
1. Probes target port for SSH banner
2. If raw SSH detected → Direct connection
3. If no SSH banner AND UseTLS=True → Start TLS tunnel
4. If direct connection fails AND UseTLS=True → Retry with TLS tunnel

This ensures maximum compatibility with different SSH server configurations.

### Comparison Table

| Feature | HTTP Proxy Injector | Custom SSH (Before) | Custom SSH (After) |
|---------|-------------------|-------------------|------------------|
| Password Auth | ✅ | ✅ | ✅ |
| Private Key Auth | ✅ | ❌ | ✅ |
| TLS Wrapping | ✅ | ❌ | ✅ |
| TLS Auto-Detect | ✅ | ✅ | ✅ |
| Error Messages | ✅ | ❌ | ✅ |
| UI Controls | ✅ | ❌ | ✅ |
| Fallback Logic | ✅ | ✅ | ✅ |

**Result**: Custom SSH now has feature parity with HTTP proxy injectors.
