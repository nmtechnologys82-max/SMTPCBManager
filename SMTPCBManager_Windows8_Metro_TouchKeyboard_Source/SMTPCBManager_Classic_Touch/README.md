# SMT PCB Manager — Windows 8.1-inspired desktop edition

## Build
Windows 10/11, Visual Studio 2026 or .NET 8+ SDK. Open `SMTPCBManager.csproj`, restore NuGet packages, Build and Run. From a command prompt with the correct x64 dotnet in PATH: `dotnet build` then `dotnet run`.

## Included
- Native C# Windows Forms desktop UI with a Windows 8.1-inspired flat blue header, tabs, native grid, buttons and status strip. Normal resizable Windows window, initially maximized.
- Dashboard inventory summary and dynamic USB humidity tabs (up to 10). With no compatible devices, no tabs or no-device message appears.
- PCB IN: Full quantity and completed quantity entered only on IN. SQLite transaction.
- PCB OUT: SAP-only lookup, select a specific Stock ID, withdraw entire available quantity. No partial OUT; a withdrawn stock cannot be withdrawn twice.
- SAP-only Inventory lookup; record edit requires employee number and marks EDITED, preserving an edit history. Quantity fields are not editable.
- Read-only IN/OUT transaction history, CSV reports, optional manual backup/restore.
- SQLite database under `%LOCALAPPDATA%\\SMTPCBManager\\pcb_inventory.db`. Existing data is retained; no automatic reset.

## USB humidity device integration — IMPORTANT
**A generic USB sensor or USB flash drive will not work automatically.** A compatible external IoT device must present a **USB CDC/virtual COM port** and transmit a UTF-8 JSON line **at 9600 baud, 8N1** at least every 2 seconds:

`{"deviceId":"DC001","humidityRH":8.5}` followed by `\\n`.

The Windows app scans available COM ports, opens available ports, reads and validates the device's JSON and displays one tab per unique device ID (up to 10). Tabs disappear when a port disconnects or its last valid reading is more than 6 seconds old; reconnect restores the tab. The app does not measure, calibrate, control, or apply thresholds. No fake readings or placeholders. Different USB interfaces/protocols (HID, vendor USB, network) require a matching adapter and cannot be claimed supported without a hardware model/protocol. **Avoid connecting unrelated COM equipment while using the app**, as it attempts to open available serial ports. Use a dedicated workstation or restrict COM ports after hardware selection.

## Important limitations
- This build has not been compiled or hardware-tested in the authoring environment. The previous version compiled and launched on the user's PC; this revised version must be rebuilt and tested on Windows.
- Automatic duplicate prevention applies to repeated clicks while a save is processing; deliberately entering the same PCB again as a new operation is permitted.
- Windows touch keyboard placement is OS-controlled; verify on the actual touch panel.
- Backup/restore is a maintenance operation. Test on a copy of the database before deployment.
- This is a source project, not a signed or packaged installer.

## Windows 8 Metro palette update
- Header and default actions: `#00A4EF` Metro Blue.
- Selected navigation and humidity tabs: `#68217A` Metro Purple.
- Navigation tabs: `#1D1D1D` Charcoal.
- PCB IN Save: `#00A300` Metro Green.
- Full PCB OUT: `#E51400` Metro Red.
- Edit record: `#F09609` Metro Orange.
- Background: `#F2F2F2`; form surfaces remain white.
- PCB classification row colors are preserved independently.
- Humidity tabs appear only for recognized, actively responding USB serial devices (maximum 10); no placeholder message.

This source update has not been compiled or USB hardware-tested in this environment. Run `dotnet build` on Windows and test with your actual USB device before production use.

## Windows touch keyboard (Windows 10/11)
- Tapping a text field (SAP search, employee number, PCB IN, edit) requests the built-in Windows touch keyboard (`TabTip.exe`). If it is unavailable, the app attempts `osk.exe`.
- Keyboard launch is throttled to avoid repeatedly opening it as focus moves between fields.
- Scrollable PCB IN and Edit forms scroll focused inputs into view. Windows manages keyboard dismissal; the application does not forcibly terminate the keyboard process.
- On some desktop PCs, Windows touch keyboard visibility and automatic dismissal depend on Windows settings and hardware keyboard configuration. Test on the actual touch display. If needed, enable **Show the touch keyboard when there's no keyboard attached** in Windows settings and the Touch keyboard taskbar icon.
- This is source code, not a precompiled Windows installer. Run `dotnet build` then `dotnet run` from the project folder on Windows.
