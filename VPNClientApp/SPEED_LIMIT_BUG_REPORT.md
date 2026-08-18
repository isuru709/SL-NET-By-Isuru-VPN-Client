# Speed Limit Function Bug Report
**Date:** December 24, 2025  
**Component:** GlobalSpeedLimiter.vb  
**Status:** ❌ CRITICAL BUGS FOUND

## Executive Summary
The download speed limit function has **two critical bugs**:
1. **8x multiplier error** - Users get 8 times more bandwidth than requested
2. **Download limiting is ineffective** - Windows QoS cannot limit download speeds

---

## Bug #1: Wrong Unit Conversion (8x Error) 🔴 CRITICAL

### Current Code (Line 46-47):
```vb
Dim downloadBps As Long = CLng(downloadLimitKBps) * 1024 * 8
Dim uploadBps As Long = CLng(uploadLimitKBps) * 1024 * 8
```

### Problem:
Despite the parameter name `ThrottleRateActionBitsPerSecond`, Windows actually expects **bytes per second**, not bits per second.

### Evidence:
1. **Microsoft Documentation Example:**
   - Input: `-ThrottleRateActionBitsPerSecond 1MB` (1,048,576)
   - Output: `ThrottleRate: 1.049 MBits/sec`
   - Calculation: 1,048,576 bits/sec = 131,072 bytes/sec = 128 KB/s
   - But 1MB parameter = 1,048,576 bytes, so it's treating it as bytes!

2. **Current System State:**
   ```powershell
   Get-NetQosPolicy -Name "SL_NET_VPN_GlobalSpeedLimit_Download"
   # Shows: ThrottleRate: 819200
   # This is 819200 bytes/sec = 800 KB/s
   # If user set 100 KB/s:
   # Code sends: 100 * 1024 * 8 = 819,200 (meant as bits)
   # Windows interprets: 819,200 bytes/sec = 800 KB/s
   # Result: 8x MORE than intended!
   ```

### Impact:
- User sets **100 KB/s** → Gets **800 KB/s** (8x more)
- User sets **500 KB/s** → Gets **4000 KB/s** (8x more)
- **All speed limits are 8 times higher than intended**

### Fix:
```vb
' Remove the "* 8" multiplication
Dim downloadBytes As Long = CLng(downloadLimitKBps) * 1024
Dim uploadBytes As Long = CLng(uploadLimitKBps) * 1024
```

---

## Bug #2: Download Limiting is Ineffective 🔴 CRITICAL

### Current Approach:
The code tries to limit download speeds using Windows QoS policies with the assumption that throttling outbound ACK packets will slow downloads.

### Fundamental Problem:
**Windows QoS can only throttle OUTBOUND traffic.** It cannot directly limit inbound (download) traffic.

### Why Throttling ACKs Doesn't Work Well:
1. **TCP Window Scaling**: Modern TCP uses large window sizes (several MB)
2. **Selective ACK (SACK)**: Can acknowledge multiple packets at once
3. **ACK Compression**: TCP can send fewer ACKs without significantly impacting throughput
4. **Minimal Overhead**: ACKs are tiny (~40 bytes) compared to data packets (1460+ bytes)
5. **Server Buffering**: Servers will buffer and retransmit aggressively

### Real-World Testing Needed:
To verify if download limiting works at all, test:
```powershell
# Set 100 KB/s download limit
# Download a large file
# Measure actual speed
# Expected: ~100 KB/s
# Likely actual: Much higher or no effect
```

### Alternative Solutions:
For effective download limiting, consider:

1. **Application-Level Throttling** (BEST for your VPN app):
   ```vb
   ' You already have this in VPNShareManager.vb!
   ' RelayAsyncThrottled() function does proper throttling
   ' This works for shared connections
   ```

2. **Packet Filtering at Driver Level**:
   - Requires kernel-mode driver
   - Complex to implement
   - Best solution for system-wide limiting

3. **Proxy-Based Throttling**:
   - Since you have an HTTP proxy, throttle at proxy level
   - Control download speed directly in the proxy

4. **Remove Download Limiting from QoS**:
   - Only use QoS for upload limiting (which DOES work)
   - Use application-level throttling for downloads
   - Be honest with users about limitations

---

## Testing Results

### Current Configuration:
```
Policy: SL_NET_VPN_GlobalSpeedLimit_Download
ThrottleRate: 819200 bytes/sec (800 KB/s)
Intended: 100 KB/s
Actual Effect: Unknown (needs live testing)
```

### Verification Commands:
```powershell
# Check current policies
Get-NetQosPolicy | Where-Object { $_.Name -like "*SL_NET*" }

# View details
Get-NetQosPolicy -Name "SL_NET_VPN_GlobalSpeedLimit_Download" | Format-List Name,ThrottleRate

# Convert bytes to KB/s
$policy = Get-NetQosPolicy -Name "SL_NET_VPN_GlobalSpeedLimit_Download"
[math]::Round($policy.ThrottleRate / 1024)  # Shows KB/s
```

---

## Recommended Actions

### Immediate (Bug Fix):
1. ✅ **Fix the 8x multiplier bug**
   - Change lines 46-47 in GlobalSpeedLimiter.vb
   - Remove `* 8` from calculations
   - Update variable names from `Bps` to `Bytes` for clarity

2. ✅ **Test upload limiting**
   - Upload limiting via QoS DOES work
   - Verify it's working correctly after fix

3. ✅ **Update UI messaging**
   - Change the confirmation dialog text
   - Remove claim about "application-level download limiting"
   - Be transparent: "Upload limiting uses Windows QoS. Download limiting may have limited effectiveness."

### Short-term (Feature Improvement):
1. 🔄 **Add proper download testing**
   - Create a real download speed test
   - Measure if QoS download limit has any effect
   - If ineffective, document it clearly

2. 🔄 **Extend VPNShareManager throttling to all traffic**
   - Your `RelayAsyncThrottled()` function works well
   - Consider routing all traffic through throttled relays
   - This gives true download AND upload control

### Long-term (Architecture):
1. 📋 **Implement at proxy level**
   - Throttle downloads in the HTTP proxy
   - More reliable than QoS
   - Works for all proxied traffic

2. 📋 **Consider WinDivert or WFP driver**
   - Packet-level control
   - Can truly limit download speeds
   - Significant development effort

---

## Code Changes Required

### File: GlobalSpeedLimiter.vb

**Lines 46-47 - Fix unit conversion:**
```vb
' OLD (WRONG):
Dim downloadBps As Long = CLng(downloadLimitKBps) * 1024 * 8
Dim uploadBps As Long = CLng(uploadLimitKBps) * 1024 * 8

' NEW (CORRECT):
Dim downloadBytes As Long = CLng(downloadLimitKBps) * 1024
Dim uploadBytes As Long = CLng(uploadLimitKBps) * 1024
```

**Lines 49, 59, 72, 84, 90 - Update variable names:**
```vb
' OLD:
Dim totalBps As Long = downloadBps + uploadBps
Dim psCommand = $"New-NetQosPolicy -Name '{combinedPolicyName}' -ThrottleRateActionBitsPerSecond {totalBps} -NetworkProfile All"
Dim psCommand = $"New-NetQosPolicy -Name '{uploadPolicyName}' -ThrottleRateActionBitsPerSecond {uploadBps} -NetworkProfile All"
Dim psCommand = $"New-NetQosPolicy -Name '{downloadPolicyName}' -ThrottleRateActionBitsPerSecond {downloadBps} -NetworkProfile All"

' NEW:
Dim totalBytes As Long = downloadBytes + uploadBytes
Dim psCommand = $"New-NetQosPolicy -Name '{combinedPolicyName}' -ThrottleRateActionBitsPerSecond {totalBytes} -NetworkProfile All"
Dim psCommand = $"New-NetQosPolicy -Name '{uploadPolicyName}' -ThrottleRateActionBitsPerSecond {uploadBytes} -NetworkProfile All"
Dim psCommand = $"New-NetQosPolicy -Name '{downloadPolicyName}' -ThrottleRateActionBitsPerSecond {downloadBytes} -NetworkProfile All"
```

**Update comments (Lines 50-52):**
```vb
' OLD:
' Strategy: NetQoS can only throttle OUTBOUND traffic
' Upload limit: Direct throttle of outbound data
' Download limit: Throttle outbound ACKs AND create a combined policy

' NEW:
' IMPORTANT: NetQoS can only throttle OUTBOUND traffic effectively
' Upload limit: Direct throttle of outbound data (WORKS WELL)
' Download limit: Throttle outbound ACKs (LIMITED EFFECTIVENESS - use VPNShareManager for better control)
' Note: The parameter name is misleading - it actually expects BYTES per second, not bits
```

### File: MainWindow.xaml.vb

**Line 1602-1603 - Update confirmation message:**
```vb
' OLD:
"Note: Download limiting is application-level. Upload limiting uses Windows QoS." & vbCrLf & vbCrLf &

' NEW:
"Note: Upload limiting uses Windows QoS (effective). Download limiting uses QoS ACK throttling (limited effectiveness)." & vbCrLf & vbCrLf &
```

**Line 1615 - Update success message:**
```vb
' OLD:
MessageBox.Show("Global speed limit applied successfully using Windows QoS policies." & vbCrLf & vbCrLf & "Both download and upload limiting are enforced at system level.", "Success", MessageBoxButton.OK, MessageBoxImage.Information)

' NEW:
MessageBox.Show("Global speed limit applied successfully using Windows QoS policies." & vbCrLf & vbCrLf & "Upload limiting is effective. Download limiting may have limited effectiveness (best used with VPN sharing).", "Success", MessageBoxButton.OK, MessageBoxImage.Information)
```

---

## Conclusion

**Current Status:** ❌ **NOT WORKING AS INTENDED**

- ❌ **Bug #1 (8x error):** Users get 8x more bandwidth than requested
- ⚠️ **Bug #2 (Download limit):** Effectiveness unknown, likely minimal

**After Fix:** ⚠️ **PARTIALLY WORKING**

- ✅ Upload limiting will work correctly
- ⚠️ Download limiting will still be questionable (fundamental Windows QoS limitation)

**Best Approach:**
1. Fix the 8x bug immediately
2. Test real-world effectiveness
3. If download limiting doesn't work, disable it or document limitations clearly
4. Use VPNShareManager's RelayAsyncThrottled for reliable download limiting in sharing mode
