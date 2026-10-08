# RDP Viewer - first test build

A small **C# / Windows Forms / .NET Framework 4.8 x64** application hosting Microsoft's installed Remote Desktop ActiveX control. It is separate from `../RDP-BottomHalf.ps1`; that launcher is unchanged.

## What it does

- Requests a new **remote desktop resolution**, rather than stretching the old image, 300 ms after the viewport stops changing.
- Offers **bottom half, top half and full work area** on a selected monitor. The first run prefers the leftmost matching 1440 x 2560 portrait display.
- Measures the actual RDP content area, not the outer window. The title bar, toolbar and local taskbar are accounted for.
- Uses per-monitor DPI awareness; requests the monitor's scale factor when moving between displays.
- Shows **requested**, **confirmed**, **not confirmed** and **failed** states. A successful COM call alone is not treated as evidence of a server-side resize.
- Remembers only monitor identity and window bounds in `%LOCALAPPDATA%\RDP-Viewer\layout.xml`.
- Provides a bounded, in-memory diagnostics view with a manual Copy button.

This is a **direct-PC prototype**, not a full replacement for every `mstsc` connection type. It is not a Citrix ICA client and cannot dynamically resize an ICA session.

## Get the Windows build

In this repository, open **Actions -> Build RDP Viewer -> the latest successful run -> Artifacts -> RDP-Viewer-win-x64**. Extract the entire artifact. Keep these files together:

```text
RDP-Viewer.exe
RDP-Viewer.exe.config
MSTSCLib.dll
AxMSTSCLib.dll
README.md
BUILD-INFO.txt
SHA256SUMS.txt
```

The two interop DLLs are generated managed wrappers. The package does **not** bundle or replace Windows' `mstscax.dll`.

The executable is **unsigned**. Use the organisation's approved review/signing/deployment process. Do not disable SmartScreen, AppLocker, WDAC, antivirus, NLA or execution policies to run it. No administrator elevation is requested.

## First use

1. Launch `RDP-Viewer.exe` **on the desktop that should own the window**. For RDP nested inside Citrix, run it inside that Citrix desktop. It can only see monitors exposed in that environment.
2. Enter **Computer** (prefer its certificate-matching full DNS name), **Port** (usually 3389) and, optionally, **Account** (`DOMAIN\user` or `user@domain`). Do not put a password in any field.
3. Select the far-left **1440 x 2560** monitor, choose **Bottom half**, and click **Place window**. Monitor labels use Windows device names and coordinates; collection order is not assumed to equal Settings' display number.
4. Click **Connect** and complete Microsoft's native credential prompt.
5. Resize the window. The status bar should change from a request to **Resolution confirmed** or **Resolution matches**, with dimensions matching the content area.

All supported display changes use `IMsRdpClient9.UpdateSessionDisplaySettings` in the current session. The program does not intentionally reconnect to implement resizing. Clipboard redirection is off by default and can be opted into before connecting. Disconnect/close does not send a logoff command.

## Authentication and scope

CredSSP is enabled. Server authentication is required (`AuthenticationLevel = 1`), so a name/trust failure blocks connection rather than offering an ignore-certificate option. There is no password text box, password serialization, certificate bypass or authentication-policy modification. The native prompt's new credential-saving option is disabled; the viewer does not read or delete existing Windows Credential Manager entries.

**Not implemented or validated in this first version:** RD Gateway, RD Connection Broker/load-balancing profiles, RemoteApp, Azure Virtual Desktop/Windows 365 subscriptions, Entra web sign-in, Remote Credential Guard, Restricted Admin, smart-card-specific sign-in flows, or arbitrary `.rdp` import. Use your existing approved client for a connection that depends on those features. The viewer does not silently import only part of a working `.rdp` profile.

The account fields and target are not written to the layout file. Diagnostic text from the native control can contain connection metadata; review it before sharing. No automatic log upload or telemetry is implemented.

## Validation boundaries

The workflow compiles on Windows, runs deterministic offline tests and performs a separate **offline ActiveX smoke test**. The smoke test creates the native control, verifies required interfaces and applies the credential/security settings. It **does not call Connect**.

A green build is **not** a successful test of your bank's authentication, network, certificate infrastructure, endpoint policies or remote host's dynamic-display support. Inspect the workflow and test-report artifact for actual results rather than treating this README as a test-pass claim.

### Workstation acceptance test

- Confirm the ordinary approved client can connect to the same target from the same desktop.
- Connect with this viewer, place it in the bottom half of the portrait monitor and confirm the remote taskbar is visible.
- Resize taller, shorter, wider and narrower without a disconnect. Check the status, taskbar and remote Display Settings. Smart sizing stays off.
- Move between monitors at 100% and higher scaling, minimize/restore, and try all three placement presets.
- Close/reopen to verify layout restoration; disconnect a monitor to verify the window remains reachable.
- Cancel the native credential prompt and retry. Confirm an authentication or certificate failure does not lead to a weaker fallback.

If a resize is not confirmed after six seconds, use **Retry fit** and **Diagnostics**. A timeout is not proof that the host lacks support; it means the requested dimensions have not been confirmed. No automatic stretching or reconnect fallback is applied.

**Precision limitations:** the RDP display-control protocol requires an even width, so an odd-width window can have a one-pixel gutter. This is not letterboxing. Valid requested dimensions are 200..8192 pixels. Scale factors are requested but may not apply identically on every host; resolution acknowledgement does not prove DPI acknowledgement.

## Build locally

Use 64-bit Windows PowerShell, Visual Studio 2022 / Build Tools with the .NET desktop workload, and the .NET Framework 4.8 targeting pack + SDK tools. Then run from the repository root:

```powershell
.\RDP-Viewer\build.ps1 -RunTests -SmokeTest
```

`build.ps1` discovers Microsoft's installed `AxImp.exe`, generates typed wrappers from the local `System32\mstscax.dll`, and compiles `RdpViewer.csproj` with MSBuild. No third-party NuGet package is required. The package is placed in `RDP-Viewer\artifacts\RDP-Viewer-win-x64`.

After generating the wrappers once, `RdpViewer.csproj` can also be opened in Visual Studio. Do not commit `interop`, `bin`, `obj` or local `artifacts` folders.

Offline tests can be run separately (the executable writes a report and returns an exit code):

```powershell
$p = Start-Process .\RDP-Viewer.exe -ArgumentList '--self-test', 'self-test.txt' -Wait -PassThru
$p.ExitCode
Get-Content .\self-test.txt
```

## Source layout

| File | Responsibility |
| --- | --- |
| `src/RdpSession.cs` | ActiveX lifecycle, credential configuration, display updates and server-size confirmation |
| `src/MainForm.cs` | Compact controls, monitor selection, presets and debounce |
| `src/Core.cs` | Endpoint validation, display constraints, geometry and layout persistence |
| `src/SelfTests.cs` | Offline logic tests and disconnected ActiveX smoke test |
| `build.ps1` | Windows wrapper generation, compilation, checks and packaging |
| `../.github/workflows/rdp-viewer.yml` | Automated Windows build and downloadable artifacts |

## Microsoft references

- [UpdateSessionDisplaySettings](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/mt703457(v=vs.85))
- [Remote desktop size-change event](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onremotedesktopsizechange)
- [Display-control dimensions and scaling constraints](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpedisp/ea2de591-9203-42cd-9908-be7a55237d1c)
- [Server authentication levels](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings4-authenticationlevel)
- [Native credential prompt settings](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable4)
- [ActiveX wrapper generation](https://learn.microsoft.com/en-us/dotnet/framework/tools/aximp-exe-windows-forms-activex-control-importer)
