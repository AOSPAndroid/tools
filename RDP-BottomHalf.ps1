#requires -Version 5.1
<#
.SYNOPSIS
Opens a Remote Desktop Connection in the usable bottom half of the far-left
1440 x 2560 portrait monitor in the supplied four-monitor layout (screen 3).

.DESCRIPTION
Run on the Windows desktop that will own the mstsc window. The script reads
monitor coordinates, excludes the taskbar, calculates the window frame, and
requests a matching remote desktop resolution. It writes a new .rdp file
beside this script and opens it. Credentials are entered only in Windows' RDP UI.

If the layout does not match, the script asks you to point at the target monitor.
The script sends no close or logoff commands to existing connections. Normal
RDP reconnection can take over an existing session on the same remote PC.
No execution, authentication, registry, or machine display policies are changed.
This is a direct-PC RDP launcher;
it does not copy RD Gateway settings from another connection file.

.EXAMPLE
powershell.exe -NoProfile -File .\RDP-BottomHalf.ps1
.EXAMPLE
.\RDP-BottomHalf.ps1 -ComputerName DESKTOP123

.NOTES
Prepared for Windows 10/11, Windows PowerShell 5.1 or newer.
Reviewed statically; not executed on the user's Windows desktop.
Sources:
https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-adjustwindowrectexfordpi
https://techcommunity.microsoft.com/blog/microsoft-security-blog/specifying-the-ts-client-start-location-on-the-virtual-desktop/246610
#>
[CmdletBinding()]
param(
    [string]$ComputerName,
    [string]$OutputDirectory = $PSScriptRoot,
    [switch]$PickMonitor,
    [ValidateRange(10, 300)][int]$WaitSeconds = 30
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this script on the Windows PC that opens your RDP connection.' }

if (-not ('RdpBottomHalf.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace RdpBottomHalf {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    public static class Native {
        [DllImport("user32.dll", SetLastError=true)]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustWindowRectExForDpi(ref Rect r, uint style,
            [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);
        [DllImport("user32.dll", EntryPoint="GetWindowLongW", SetLastError=true)]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        public static uint Style(IntPtr hwnd) { return unchecked((uint)GetWindowLong(hwnd, -16)); }
        public static uint ExStyle(IntPtr hwnd) { return unchecked((uint)GetWindowLong(hwnd, -20)); }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        public static string ClassName(IntPtr hwnd) {
            StringBuilder text = new StringBuilder(256); GetClassName(hwnd, text, text.Capacity); return text.ToString();
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="CreateWindowExW", SetLastError=true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string name,
            uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
        public static uint DpiAt(int x, int y) {
            IntPtr hwnd = CreateWindowEx(0, "STATIC", "", 0x80000000U,
                x, y, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try { return GetDpiForWindow(hwnd); } finally { DestroyWindow(hwnd); }
        }
        [DllImport("user32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hwnd, int command);
    }
}
'@
}

function Get-FrameSize([uint32]$Style, [uint32]$ExStyle, [uint32]$Dpi) {
    # Scroll bars must not be included: the negotiated desktop fits its client.
    $Style = $Style -band [uint32]4291821567 # clear WS_HSCROLL and WS_VSCROLL
    $r = New-Object RdpBottomHalf.Rect
    if (-not [RdpBottomHalf.Native]::AdjustWindowRectExForDpi([ref]$r, $Style, $false, $ExStyle, $Dpi)) {
        throw 'Windows could not calculate the RDP window frame.'
    }
    [pscustomobject]@{ Width = $r.Right - $r.Left; Height = $r.Bottom - $r.Top }
}

if ([string]::IsNullOrWhiteSpace($ComputerName)) {
    $ComputerName = Read-Host 'Remote computer name or IP address (optional :port)'
}
$ComputerName = $ComputerName.Trim()
if ($ComputerName -notmatch '\A[A-Za-z0-9._:\[\]-]{1,255}\z') {
    throw 'Enter only a computer name, DNS name, or IP address, optionally followed by :port.'
}

$previousDpi = [RdpBottomHalf.Native]::SetThreadDpiAwarenessContext([IntPtr](-4))
if ($previousDpi -eq [IntPtr]::Zero) { throw 'Windows could not enable accurate per-monitor coordinates for this script.' }
try {
    Add-Type -AssemblyName System.Windows.Forms
    $screens = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { $_.Bounds.Left }, { $_.Bounds.Top })
    $targetScreen = $screens[0]
    if ($PickMonitor -or $targetScreen.Bounds.Width -ne 1440 -or $targetScreen.Bounds.Height -ne 2560) {
        Write-Host 'Place the mouse pointer on screen 3, then press Enter here. Do not click another monitor.'
        [void](Read-Host)
        $targetScreen = [System.Windows.Forms.Screen]::FromPoint([System.Windows.Forms.Cursor]::Position)
    }
    $bounds = $targetScreen.Bounds
    $work = $targetScreen.WorkingArea
    $halfTop = $bounds.Top + [int][Math]::Floor($bounds.Height / 2.0)
    $x = [int][Math]::Max($bounds.Left, $work.Left)
    $y = [int][Math]::Max($halfTop, $work.Top)
    $right = [int][Math]::Min($bounds.Right, $work.Right)
    $bottom = [int][Math]::Min($bounds.Bottom, $work.Bottom)
    $outerWidth = $right - $x
    $outerHeight = $bottom - $y
    $dpi = [RdpBottomHalf.Native]::DpiAt($x + 20, $y + 20)
    if ($dpi -eq 0) { throw 'Windows did not report the target display DPI.' }

    # Prefer the frame style of an existing, normal mstsc desktop window.
    # Otherwise use the standard resizable desktop frame, then verify at launch.
    [uint32]$style = 0x00CF0000
    [uint32]$exStyle = 0
    foreach ($p in @(Get-Process mstsc -ErrorAction SilentlyContinue)) {
        $hwnd = $p.MainWindowHandle
        if ($hwnd -eq [IntPtr]::Zero) { continue }
        if ([RdpBottomHalf.Native]::ClassName($hwnd) -ne 'TscShellContainerClass') { continue }
        if ([RdpBottomHalf.Native]::IsIconic($hwnd) -or [RdpBottomHalf.Native]::IsZoomed($hwnd)) { continue }
        $candidate = [RdpBottomHalf.Native]::Style($hwnd)
        if (($candidate -band 0x00C40000) -ne 0x00C40000) { continue }
        $style = $candidate
        $exStyle = [RdpBottomHalf.Native]::ExStyle($hwnd)
        break
    }
    $frame = Get-FrameSize $style $exStyle $dpi
    $clientWidth = [int]($outerWidth - $frame.Width)
    $clientHeight = [int]($outerHeight - $frame.Height)
    if ($clientWidth -lt 200 -or $clientHeight -lt 200) { throw 'The usable bottom half is too small for this RDP window.' }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = [Environment]::GetFolderPath('MyDocuments')
    }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) { throw 'The output folder does not exist.' }
    $safeName = ($ComputerName -replace '[^A-Za-z0-9._-]', '_')
    if ($safeName.Length -gt 80) { $safeName = $safeName.Substring(0,80) }
    $rdpPath = Join-Path $OutputDirectory ('RDP-{0}-bottom-{1}.rdp' -f $safeName, [Guid]::NewGuid().ToString('N').Substring(0,8))
    $lines = @(
        'screen mode id:i:1'
        'use multimon:i:0'
        'span monitors:i:0'
        "desktopwidth:i:$clientWidth"
        "desktopheight:i:$clientHeight"
        'smart sizing:i:0'
        "winposstr:s:0,1,$x,$y,$right,$bottom"
        "full address:s:$ComputerName"
    )
    [IO.File]::WriteAllLines($rdpPath, [string[]]$lines, [Text.Encoding]::Unicode)

    Write-Host ("Target: {0}, {1} x {2}, {3}% scaling" -f $targetScreen.DeviceName, $bounds.Width, $bounds.Height, [Math]::Round(100 * $dpi / 96))
    Write-Host ("Bottom-half window: {0} x {1}; requested desktop: {2} x {3}" -f $outerWidth, $outerHeight, $clientWidth, $clientHeight)
    Write-Host "Connection saved: $rdpPath"
    Write-Host 'Complete the normal Remote Desktop sign-in. Keep this console open.'
    $mstscPath = Join-Path $env:SystemRoot 'System32\mstsc.exe'
    $sessionProcess = Start-Process -FilePath $mstscPath -ArgumentList ('"{0}"' -f $rdpPath) -PassThru
    [void](Read-Host 'Once the remote desktop is fully visible, return here and press Enter to position it')
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $sessionWindow = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline) {
        $sessionProcess.Refresh()
        if ($sessionProcess.HasExited) { break }
        $hwnd = $sessionProcess.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero -and
            [RdpBottomHalf.Native]::ClassName($hwnd) -eq 'TscShellContainerClass' -and
            [RdpBottomHalf.Native]::IsWindowVisible($hwnd)) {
            $sessionWindow = $hwnd
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if ($sessionWindow -eq [IntPtr]::Zero) {
        Write-Warning 'The connected desktop window was not found before the wait ended. The saved .rdp still contains the requested resolution and initial position. Existing sessions were left alone.'
        return
    }
    [void][RdpBottomHalf.Native]::ShowWindow($sessionWindow, 9) # restore, never maximize
    # Move first without changing size, so mstsc adopts the target monitor DPI.
    if (-not [RdpBottomHalf.Native]::SetWindowPos($sessionWindow, [IntPtr]::Zero, $x, $y, 0, 0, 0x0015)) {
        throw 'Windows could not move the new RDP window.'
    }
    Start-Sleep -Milliseconds 400
    $actualDpi = [RdpBottomHalf.Native]::GetDpiForWindow($sessionWindow)
    if ($actualDpi -ne $dpi) {
        Write-Warning 'mstsc uses a different DPI context from the target monitor. The requested connection and position were saved, but exact sizing could not be verified.'
        return
    }
    $actualFrame = Get-FrameSize ([RdpBottomHalf.Native]::Style($sessionWindow)) ([RdpBottomHalf.Native]::ExStyle($sessionWindow)) $dpi
    $actualWidth = [int]($clientWidth + $actualFrame.Width)
    $actualHeight = [int]($clientHeight + $actualFrame.Height)
    if ($actualWidth -gt $outerWidth -or $actualHeight -gt $outerHeight) {
        Write-Warning 'This mstsc build has a larger window frame than predicted. Exact half-screen fit was not applied; the new connection remains open. Run the launcher again while this RDP window is open so its actual frame can be used.'
        return
    }
    $finalY = $bottom - $actualHeight
    if (-not [RdpBottomHalf.Native]::SetWindowPos($sessionWindow, [IntPtr]::Zero, $x, $finalY, $actualWidth, $actualHeight, 0x0014)) {
        throw 'Windows could not position the new RDP window.'
    }
    Start-Sleep -Milliseconds 400
    $client = New-Object RdpBottomHalf.Rect
    if (-not [RdpBottomHalf.Native]::GetClientRect($sessionWindow, [ref]$client)) { throw 'Windows could not read the RDP client area.' }
    if (($client.Right - $client.Left) -eq $clientWidth -and ($client.Bottom - $client.Top) -eq $clientHeight) {
        Write-Host 'The new RDP window is positioned in the usable bottom half, with the requested client dimensions.'
        Write-Host 'Keep this window size. Reuse the saved .rdp file for the same monitor layout.'
    } else {
        Write-Warning 'The window was moved, but its client size differs from the requested dimensions. Exact fit could not be verified on this mstsc build.'
    }
} finally {
    [void][RdpBottomHalf.Native]::SetThreadDpiAwarenessContext($previousDpi)
}
