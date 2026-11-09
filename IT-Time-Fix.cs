// "IT Time Fix.exe" -- agent-facing "my clock is wrong" button.
//
// Runs as a STANDARD USER with no UAC prompt.
//
// It cannot write the system clock by itself -- that needs SeSystemtimePrivilege,
// which no standard user token has, and packaging as an .exe does not change that.
// Instead it triggers the SYSTEM scheduled task that Install-TimeFix.bat registers
// once per PC. The task holds the privilege; this program just pulls the lever.
//
// Setting the TIME ZONE does work unprivileged, so that always happens.
//
// Build: csc.exe /nologo /target:winexe /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:"IT Time Fix.exe" "IT-Time-Fix.cs"

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// Shown in the file's Properties -> Details tab and in Task Manager.
[assembly: AssemblyTitle("IT Time Fix")]
[assembly: AssemblyProduct("IT Time Fix")]
[assembly: AssemblyDescription("Corrects the time zone and system clock so Hubstaff will accept the login.")]
[assembly: AssemblyCompany("Renegade Furniture Group")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class FixTime
{
    private const string WantedTimeZone = "Eastern Standard Time";   // = (UTC-05:00) Eastern Time (US & Canada)
    private const string TaskName       = "TimeFix - Sync clock and time zone";
    private const double ToleranceSeconds = 2.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemTime(ref SYSTEMTIME st);

    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;

    // HTTPS first; the plain-HTTP entries are the ones that still work when the
    // clock is so wrong that every certificate looks "not yet valid".
    private static readonly string[][] Sources =
    {
        new[] { "https://cloudflare.com/cdn-cgi/trace",          "trace"  },
        new[] { "https://www.google.com",                        "header" },
        new[] { "http://cloudflare.com/cdn-cgi/trace",           "trace"  },
        new[] { "http://www.msftconnecttest.com/connecttest.txt","header" },
        new[] { "http://www.google.com",                         "header" }
    };

    private static Action<string> report = delegate { };

    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, "/startup") >= 0)
        {
            int result = FixClock();
            if (result != 2)
                return result;

            // The network may still be coming up at sign-in.
            Thread.Sleep(TimeSpan.FromSeconds(15));
            return FixClock();
        }

        if (Array.IndexOf(args, "/quiet") >= 0)
            return FixClock();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using (var window = new Form())
        {
            window.Text = "IT Time Fix";
            window.StartPosition = FormStartPosition.CenterScreen;
            window.AutoScaleMode = AutoScaleMode.Dpi;
            window.AutoSize = true;
            window.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            window.FormBorderStyle = FormBorderStyle.FixedDialog;
            window.MaximizeBox = false;
            window.BackColor = SystemColors.Window;
            window.ForeColor = SystemColors.WindowText;
            window.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            using (var headingFont = new Font("Segoe UI", 19, FontStyle.Bold))
            using (var bodyFont = new Font("Segoe UI", 11))
            {
                window.Font = bodyFont;

                var layout = new TableLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(24),
                    ColumnCount = 1
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                var status = new Label
                {
                    AutoSize = true,
                    Font = headingFont,
                    MaximumSize = new Size(440, 0),
                    Margin = new Padding(0, 0, 0, 8),
                    Text = "Checking your clock…"
                };
                var guidance = new Label
                {
                    AutoSize = true,
                    MinimumSize = new Size(440, 0),
                    MaximumSize = new Size(440, 0),
                    Margin = new Padding(0, 0, 0, 16),
                    Text = "This may take a moment, especially if your PC just started."
                };
                var progress = new ProgressBar
                {
                    Dock = DockStyle.Fill,
                    Height = 8,
                    Margin = new Padding(0, 0, 0, 16),
                    Style = ProgressBarStyle.Marquee,
                    AccessibleName = "Checking the clock"
                };
                var startupStatus = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(440, 0),
                    Margin = new Padding(0, 0, 0, 16),
                    Text = "Setting up automatic checks at sign-in…"
                };
                var details = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Height = 140,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Margin = new Padding(0, 0, 0, 16),
                    AccessibleName = "Technical details",
                    Visible = false
                };
                var actions = new TableLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 1,
                    Margin = Padding.Empty
                };
                actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                var showDetails = new Button
                {
                    Text = "Show details",
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = Padding.Empty
                };
                showDetails.Click += delegate
                {
                    details.Visible = !details.Visible;
                    showDetails.Text = details.Visible ? "Hide details" : "Show details";
                };
                var close = new Button
                {
                    Text = "Close",
                    AutoSize = true,
                    MinimumSize = new Size(88, 0),
                    Anchor = AnchorStyles.Right,
                    Margin = Padding.Empty,
                    Enabled = false
                };
                close.Click += delegate { window.Close(); };

                actions.Controls.Add(showDetails, 0, 0);
                actions.Controls.Add(close, 1, 0);
                layout.Controls.Add(status, 0, 0);
                layout.Controls.Add(guidance, 0, 1);
                layout.Controls.Add(progress, 0, 2);
                layout.Controls.Add(startupStatus, 0, 3);
                layout.Controls.Add(details, 0, 4);
                layout.Controls.Add(actions, 0, 5);
                window.Controls.Add(layout);
                window.AcceptButton = close;
                window.CancelButton = close;

                int result = 3;
                bool running = true;
                window.FormClosing += (sender, e) =>
                {
                    if (running && e.CloseReason == CloseReason.UserClosing)
                        e.Cancel = true;
                };
                window.Shown += async delegate
                {
                    report = message => details.BeginInvoke((Action)(() =>
                        details.AppendText(message + Environment.NewLine)));
                    try
                    {
                        bool startupRegistered = await Task.Run((Func<bool>)RegisterStartup);
                        startupStatus.Text = startupRegistered
                            ? "Time Fix will run when you sign in."
                            : "Automatic startup couldn’t be set up. See details.";
                        result = await Task.Run((Func<int>)FixClock);
                        ShowResult(result, status, guidance);
                    }
                    catch (Exception ex)
                    {
                        status.Text = "Something went wrong";
                        guidance.Text = "Try again, or share the details below with IT.";
                        details.AppendText(ex.Message + Environment.NewLine);
                    }
                    finally
                    {
                        running = false;
                        progress.Visible = false;
                        close.Enabled = true;
                        close.Focus();
                    }
                };
                Application.Run(window);
                return result;
            }
        }
    }

    private static bool RegisterStartup()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string directory = Path.Combine(localAppData, "IT Time Fix");
            string startupExe = Path.Combine(directory, "IT Time Fix.exe");
            string command = "\"" + startupExe + "\" /startup";
            if (command.Length > 260)
                throw new IOException("The startup command exceeds the Windows Run entry limit.");

            if (!string.Equals(Application.ExecutablePath, startupExe, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(directory);
                string stagedExe = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(Application.ExecutablePath, stagedExe);
                    if (File.Exists(startupExe))
                        File.Replace(stagedExe, startupExe, null);
                    else
                        File.Move(stagedExe, startupExe);
                }
                finally
                {
                    File.Delete(stagedExe);
                }
            }

            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (run == null)
                    throw new IOException("Windows did not open the current user's Run key.");

                run.SetValue("IT Time Fix", command, RegistryValueKind.String);
            }
            return true;
        }
        catch (Exception ex)
        {
            Fail("Could not set up automatic startup: " + ex.Message);
            return false;
        }
    }


    private static void ShowResult(int result, Label status, Label guidance)
    {
        switch (result)
        {
            case 0:
                status.Text = "Your clock is correct";
                guidance.Text = "Windows shows " + DateTime.Now.ToString("f", CultureInfo.CurrentCulture) + ".";
                break;
            case 2:
                status.Text = "Couldn’t check the clock";
                guidance.Text = "Check your internet connection, then open IT Time Fix again.";
                break;
            case 5:
                status.Text = "Your clock needs IT help";
                guidance.Text = "This PC didn’t let Time Fix change the clock. Share the details below with IT.";
                break;
            default:
                status.Text = "Couldn’t confirm the fix";
                guidance.Text = "Wait a moment, then open IT Time Fix again. If it still fails, contact IT.";
                break;
        }
    }


    private static int FixClock()
    {
        ApplyTimeZone();

        report("  Checking the clock against an internet time source...");
        DateTime? trueUtc = GetNetworkUtc();
        if (trueUtc == null)
        {
            Fail("No internet time source could be reached. Check the network and try again.");
            return 2;
        }

        var elapsedSinceSample = Stopwatch.StartNew();
        double drift = (DateTime.UtcNow - trueUtc.Value).TotalSeconds;
        report("  Clock is off by " + drift.ToString("N1", CultureInfo.InvariantCulture) + " seconds.");

        if (Math.Abs(drift) <= ToleranceSeconds)
        {
            Ok("The clock is already correct. Nothing to do.");
            return 0;
        }

        // Path 1: we already hold the privilege (agent is an admin, or we were elevated).
        if (TrySetClockDirectly(trueUtc.Value))
        {
            Ok("Clock corrected. Local time is now " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return 0;
        }

        // An existing task can apply the correction without elevating this process.
        report("  Trying the TimeFix scheduled task...");
        if (!TriggerTask())
        {
            Fail("The clock is still wrong. This account could not set it, and the TimeFix task could not be started.");
            report("  Ask IT to enable clock correction on this PC.");
            return 5;
        }

        if (WaitForCorrection(trueUtc.Value, elapsedSinceSample))
        {
            Ok("Clock corrected. Local time is now " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return 0;
        }

        Fail("The TimeFix task was started, but the clock correction could not be verified.");
        return 3;
    }


    // --- time zone (works unprivileged) -------------------------------------

    private static void ApplyTimeZone()
    {
        try
        {
            if (string.Equals(TimeZoneInfo.Local.Id, WantedTimeZone, StringComparison.OrdinalIgnoreCase))
            {
                Ok("Time zone already correct: " + TimeZoneInfo.Local.DisplayName);
                return;
            }

            var psi = new ProcessStartInfo("tzutil.exe", "/s \"" + WantedTimeZone + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode == 0)
                {
                    TimeZoneInfo.ClearCachedData();   // else ToLocalTime() keeps the old offset
                    Ok("Time zone set to: " + TimeZoneInfo.FindSystemTimeZoneById(WantedTimeZone).DisplayName);
                }
                else
                {
                    Fail("Could not set the time zone (tzutil exit code " + p.ExitCode + ").");
                }
            }
        }
        catch (Exception ex) { Fail("Time zone error: " + ex.Message); }
    }

    // --- true time from the network ----------------------------------------

    private static DateTime? GetNetworkUtc()
    {
        try
        {
            // 3072 = Tls12, 768 = Tls11; named constants are absent on older targets.
            ServicePointManager.SecurityProtocol =
                (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;
        }
        catch { }

        foreach (var src in Sources)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var req = (HttpWebRequest)WebRequest.Create(src[0]);
                req.Timeout = 10000;
                req.UserAgent = "TimeFix/1.0";
                req.CachePolicy = new System.Net.Cache.RequestCachePolicy(
                                      System.Net.Cache.RequestCacheLevel.NoCacheNoStore);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    DateTime utc;
                    bool got = false;

                    if (src[1] == "trace")
                    {
                        string body;
                        using (var sr = new StreamReader(resp.GetResponseStream())) body = sr.ReadToEnd();
                        sw.Stop();
                        utc = default(DateTime);
                        foreach (var line in body.Split('\n'))
                        {
                            if (line.StartsWith("ts=", StringComparison.Ordinal))
                            {
                                double epoch = double.Parse(line.Substring(3).Trim(), CultureInfo.InvariantCulture);
                                utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch);
                                got = true;
                            }
                        }
                    }
                    else
                    {
                        sw.Stop();
                        string d = resp.Headers["Date"];
                        utc = default(DateTime);
                        if (!string.IsNullOrEmpty(d))
                        {
                            utc = DateTime.Parse(d, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                            got = true;
                        }
                    }

                    if (got)
                    {
                        // The reply was produced roughly mid-flight.
                        utc = utc.AddMilliseconds(sw.ElapsedMilliseconds / 2.0);
                        report("    source ok: " + src[0] + " (" + sw.ElapsedMilliseconds + " ms)");
                        return utc;
                    }
                }
            }
            catch (Exception ex)
            {
                report("    source failed: " + src[0] + " - " + Short(ex.Message));
            }
        }
        return null;
    }

    // --- applying the correction -------------------------------------------

    private static bool TrySetClockDirectly(DateTime utc)
    {
        try
        {
            var st = new SYSTEMTIME
            {
                Year = (ushort)utc.Year, Month = (ushort)utc.Month, Day = (ushort)utc.Day,
                DayOfWeek = (ushort)utc.DayOfWeek, Hour = (ushort)utc.Hour,
                Minute = (ushort)utc.Minute, Second = (ushort)utc.Second,
                Milliseconds = (ushort)utc.Millisecond
            };
            if (SetSystemTime(ref st)) return true;

            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_PRIVILEGE_NOT_HELD)
                report("    SetSystemTime failed with error " + err + ".");
            return false;
        }
        catch { return false; }
    }

    private static bool TriggerTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/run /tn \"" + TaskName + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0) return true;
                report("    could not start the task: " + Short(outp));
                return false;
            }
        }
        catch (Exception ex) { report("    " + Short(ex.Message)); return false; }
    }

    private static bool WaitForCorrection(DateTime sampledUtc, Stopwatch elapsedSinceSample)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(5000);
            DateTime expectedUtc = sampledUtc + elapsedSinceSample.Elapsed;
            double drift = (DateTime.UtcNow - expectedUtc).TotalSeconds;
            if (Math.Abs(drift) <= ToleranceSeconds) return true;
            report("    still off by " + drift.ToString("N1", CultureInfo.InvariantCulture) + " s, waiting...");
        }
        return false;
    }


    // --- message helpers ----------------------------------------------------

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(no detail)";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
    }

    private static void Ok(string message)   { report("[OK] " + message); }
    private static void Fail(string message) { report("[Error] " + message); }
}
