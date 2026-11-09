# Renegade IT Time Fix

IT Time Fix is a small Windows utility that checks the system clock against an online time source and applies the Eastern time zone. It opens a regular Windows window with a plain-language result and optional technical details.

## Run it

1. [Download IT Time Fix.exe](../../raw/refs/heads/main/IT%20Time%20Fix.exe).
2. Double-click the EXE. It runs by itself without an installer, a console window, or an admin prompt.
3. Read the result. Use **Show details** if IT needs diagnostic information.

## Run automatically at sign-in

When you open the EXE, it copies itself to `%LOCALAPPDATA%\IT Time Fix\IT Time Fix.exe` and registers **IT Time Fix** in Windows Startup for your account. The startup copy checks the clock quietly after you sign in. If it cannot reach a time source, it waits 15 seconds and retries once.

To turn off automatic checks, disable **IT Time Fix** in **Settings → Apps → Startup**.

## Clock permissions and offline use

Windows requires the **Change the system time** privilege to correct the system clock. On a standard account without that privilege, IT can run `Install-TimeFix.bat` as administrator once to install the SYSTEM scheduled task. The portable EXE can then request a correction through that task without prompting the user for admin access.

Clock recovery requires a reachable time source. A software timer measures elapsed time, so a PC that loses the date and time while powered off needs a working hardware clock for reliable offline startup. If the CMOS battery causes the resets, replace it.

## Build from source

From a Windows command prompt, build the GUI EXE with the .NET Framework C# compiler.

```bat
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:winexe /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:"IT Time Fix.exe" "IT-Time-Fix.cs"
```

Use `"IT Time Fix.exe" /quiet` to run a single check without opening a window or registering startup.
