# tools

Windows support utilities.

## RDP-BottomHalf.ps1

[RDP-BottomHalf.ps1](RDP-BottomHalf.ps1) opens a Remote Desktop Connection (`mstsc`) in the usable lower half of a portrait monitor. It is designed for a layout where **screen 3 is the far-left monitor at 1440 × 2560**.

The launcher reads the monitor coordinates, excludes the taskbar, accounts for the window frame and title bar, and requests a matching remote desktop resolution. It then positions the new connection and checks its client dimensions.

### Requirements

- Windows 10 or Windows 11 with Windows PowerShell 5.1 or later and Remote Desktop Connection.
- A remote PC you can already reach through a direct RDP connection.
- A writable folder containing the script, where it can save a new `.rdp` file.

Run the script on the **Windows desktop where you normally launch Remote Desktop**. If you launch RDP inside a Citrix desktop, run the script inside that Citrix desktop.

### Run

Open PowerShell in the folder containing the script and run:

```powershell
powershell.exe -NoProfile -File .\RDP-BottomHalf.ps1
```

1. Enter the remote computer name or IP address; an optional `:port` is accepted.
2. Finish signing in through the normal Windows Remote Desktop interface.
3. Once the remote desktop is fully visible, return to the PowerShell window and press **Enter** to position it.

By default, the launcher uses the far-left monitor if its resolution is 1440 × 2560. If the layout differs, it asks you to place the mouse pointer on the intended monitor and press Enter in the console.

### Options

Provide the computer name directly:

```powershell
.\RDP-BottomHalf.ps1 -ComputerName DESKTOP123
```

Choose a monitor using the mouse pointer, regardless of the default layout:

```powershell
.\RDP-BottomHalf.ps1 -ComputerName DESKTOP123 -PickMonitor
```

### Reuse

The launcher prints the path of a new `.rdp` file saved beside the script. Open that file for subsequent connections using the same monitor layout. **Keep the window at the generated dimensions**; this launcher sets a fixed desktop resolution. Run it again if the monitor layout or scaling changes.

This is a **direct-PC launcher**. It does not import RD Gateway settings from another connection file. Credentials are entered in the Windows RDP interface.

### Validation

The script has been **statically reviewed and has not been tested on the user's Windows machine**. It reports when exact sizing cannot be verified on the installed `mstsc` build.

### References

- [Microsoft: mstsc command reference](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc)
- [Microsoft: RDP window placement with winposstr](https://techcommunity.microsoft.com/blog/microsoft-security-blog/specifying-the-ts-client-start-location-on-the-virtual-desktop/246610)
