# VPN Share URL Filtering Feature

## Overview
Added a URL filtering system to the **Share VPN** tab that allows you to restrict which URLs can be accessed through the shared VPN connection. Your PC maintains unrestricted access while devices connected through the shared VPN (like phones) can only access allowed URLs.

## Feature Components

### 1. UI Layout Changes
The Share VPN tab now has a **3-panel layout**:

- **Left Panel**: Original share configuration (listen address, ports, start/stop)
- **Middle Panel**: New URL filtering controls
- **Right Panel**: Share status + allowed URLs list

### 2. URL Filtering Controls (Middle Panel)

#### Add Allowed URL Section
- **TextBox**: Enter domain or full URL (e.g., `google.com` or `https://www.google.com`)
- **Add Button (➕)**: Adds the URL to the allowed list
- Automatically normalizes URLs to domain format (removes protocol, path, port)

#### Filter Toggle
- **Enable URL Filtering Checkbox**: Turn filtering on/off
- **Apply Filter Button**: Applies the current filter settings
- **Status Text**: Shows current filter state and count of allowed URLs

### 3. Allowed URLs List (Right Panel)
- **ListBox**: Displays all allowed URLs
- **Delete Selected Button**: Removes selected URL from the list
- Shows count of allowed URLs in status

## How It Works

### For PC Users (You)
- **Unrestricted Access**: Your PC has full internet access regardless of filter settings
- Filter only affects connections coming through the shared VPN proxy

### For Shared VPN Clients (Phone/Other Devices)
When URL filtering is **ENABLED**:
- Can **ONLY** access URLs in the allowed list
- All other URLs return **HTTP 403 Forbidden**
- Blocked requests are logged

When URL filtering is **DISABLED**:
- Full internet access through shared VPN (same as before)

## Implementation Details

### URL Normalization
URLs are automatically normalized to domain format:
```
Input: https://www.google.com/search?q=test
Output: www.google.com

Input: example.com:8080/path
Output: example.com
```

### Filtering Logic
1. **HTTP CONNECT Method** (HTTPS traffic): Extracts hostname from CONNECT request
2. **HTTP GET/POST** (HTTP traffic): Extracts hostname from Host header
3. **Domain Matching**: Supports exact match and subdomain match
   - Allowed: `google.com` → Allows `google.com` and `www.google.com`

### Technical Implementation

#### VPNShareManager.vb
- `_urlFilterEnabled`: Boolean flag for filter state
- `_allowedUrls`: List of allowed domain strings
- `SetUrlFilter()`: Public method to configure filtering
- `CheckAndFilterHttpConnectionAsync()`: Inspects HTTP requests and blocks/allows based on filter

#### MainWindow.xaml.vb
- `AddShareFilterButton_Click()`: Adds URL to filter list
- `DeleteShareFilterButton_Click()`: Removes URL from filter list
- `ApplyShareFilterButton_Click()`: Applies filter settings to VPNShareManager
- `NormalizeToDomain()`: Helper function to normalize URLs

## Usage Instructions

### Step 1: Configure Shared VPN
1. Connect to VPN on your PC
2. Go to **🔗 Share VPN** tab
3. Select listen address (0.0.0.0 for LAN access)
4. Configure ports (default: HTTP 8888, SOCKS5 1080)
5. Click **▶ Start Share**

### Step 2: Add Allowed URLs
1. In the middle panel, enter a URL or domain (e.g., `facebook.com`)
2. Click **➕ Add**
3. Repeat for all URLs you want to allow
4. The URLs appear in the right panel list

### Step 3: Enable Filtering
1. Check **Enable URL Filtering** checkbox
2. Click **✅ Apply Filter**
3. Status shows "Filter: Enabled (X URLs allowed)"

### Step 4: Connect Phone/Device
1. Connect device to same Wi-Fi network as PC
2. Configure Wi-Fi proxy settings on device:
   - **Server**: Your PC's IP (shown in right panel)
   - **Port**: 8888 (or your HTTP port)
3. Device can now only access allowed URLs

### Step 5: Disable/Modify Filter
- **To disable**: Uncheck "Enable URL Filtering" → Click Apply
- **To add URLs**: Add URLs → Click Apply (if already enabled)
- **To remove URLs**: Select in list → Click Delete Selected → Click Apply

## Example Use Cases

### 1. Parental Control
Allow only educational sites for kids:
```
- wikipedia.org
- khanacademy.org
- education.com
```

### 2. Work Device Restriction
Allow only work-related sites:
```
- company-domain.com
- gmail.com
- slack.com
- zoom.us
```

### 3. Social Media Only
Allow only specific social platforms:
```
- facebook.com
- instagram.com
- twitter.com
```

## Important Notes

### Limitations
1. **HTTP/HTTPS Only**: Filter works for HTTP proxy connections only
2. **SOCKS5**: Filtering is not applied to SOCKS5 connections (due to protocol design)
3. **PC Unaffected**: Filter does NOT affect your PC's internet access
4. **Performance**: Minimal overhead - only inspects first packet of each connection

### Best Practices
1. **Use domains without www**: Add `google.com` instead of `www.google.com` (automatically matches both)
2. **Test before deploying**: Add URLs and test from phone before giving to others
3. **Keep list updated**: Regularly review and update allowed URLs
4. **Log monitoring**: Check connection logs to see blocked attempts

### Security Considerations
- Filter is applied at proxy level (not firewall level)
- Savvy users can bypass by changing proxy settings
- For true security, combine with firewall rules or parental control software
- Shared VPN requires PC to remain on and connected to VPN

## Troubleshooting

### "Filter not working - all sites accessible"
- Verify **Enable URL Filtering** is checked
- Click **Apply Filter** after making changes
- Restart the share (Stop → Start) if issues persist

### "No sites work on phone"
- Ensure at least one URL is in the allowed list
- Check if filter is enabled (should show allowed count in status)
- Verify proxy settings on phone are correct

### "Some subdomains don't work"
- Add specific subdomain to allowed list
- Example: Add both `google.com` and `accounts.google.com` if needed

## Code Changes Summary

### Files Modified
1. **MainWindow.xaml**: Added 3-panel layout with URL filtering controls
2. **MainWindow.xaml.vb**: Added 4 new event handlers for URL filtering
3. **VPNShareManager.vb**: Added URL filtering logic and HTTP inspection

### New Methods
- `SetUrlFilter()`: Configure filter in VPNShareManager
- `CheckAndFilterHttpConnectionAsync()`: HTTP request inspection
- `AddShareFilterButton_Click()`: Add URL handler
- `DeleteShareFilterButton_Click()`: Remove URL handler
- `ApplyShareFilterButton_Click()`: Apply filter handler
- `NormalizeToDomain()`: URL normalization utility

## Future Enhancements (Ideas)

1. **Category-based filtering**: Pre-built lists (social media, gaming, adult content)
2. **Import/Export filter lists**: Save and load filter configurations
3. **Whitelist/Blacklist mode**: Option to block specific URLs instead of allowing only listed
4. **Regex patterns**: Advanced URL matching with regular expressions
5. **Time-based rules**: Different filters for different times of day
6. **Per-device filters**: Different filter rules for different connected devices
7. **SOCKS5 filtering**: Implement filtering for SOCKS5 protocol

---

**Version**: 1.0  
**Date**: November 2025  
**Author**: VPN Client App Development Team
