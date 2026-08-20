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
// Build: csc.exe /nologo /target:exe /win32icon:"icons\it-time-fix.ico"
//               /out:"IT Time Fix.exe" /r:System.dll "IT-Time-Fix.cs"

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

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

    private static int Main(string[] args)
    {
        bool quiet = Array.IndexOf(args, "/quiet") >= 0;

        try { Console.Title = "IT Time Fix"; } catch { }

        Console.WriteLine();
        Console.WriteLine("  IT Time Fix - correcting the time zone and clock");
        Console.WriteLine("  ------------------------------------------------");
        Console.WriteLine();

        ApplyTimeZone();

        Console.WriteLine();
        Console.WriteLine("  Checking the clock against an internet time source...");
        DateTime? trueUtc = GetNetworkUtc();
        if (trueUtc == null)
        {
            Fail("No internet time source could be reached. Check the network and try again.");
            return Done(2, quiet);
        }

        double drift = (DateTime.UtcNow - trueUtc.Value).TotalSeconds;
        Console.WriteLine("  Clock is off by " + drift.ToString("N1", CultureInfo.InvariantCulture) + " seconds.");

        if (Math.Abs(drift) <= ToleranceSeconds)
        {
            Ok("The clock is already correct. Nothing to do.");
            return Done(0, quiet);
        }

        // Path 1: we already hold the privilege (agent is an admin, or we were elevated).
        if (TrySetClockDirectly(trueUtc.Value))
        {
            Ok("Clock corrected. Local time is now " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return Done(0, quiet);
        }

        // Path 2: the normal case -- ask the pre-installed SYSTEM task to do it.
        Console.WriteLine("  This account cannot set the clock directly; asking the TimeFix task...");
        if (TriggerTask() && WaitForCorrection(out drift))
        {
            Ok("Clock corrected. Local time is now " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return Done(0, quiet);
        }

        Console.WriteLine();
        Console.WriteLine("  The TimeFix scheduled task is not installed on this PC.");

        string batPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            "Install-TimeFix.bat");

        if (File.Exists(batPath))
        {
            Console.WriteLine("  Launching the one-time installer with admin rights...");
            Console.WriteLine("  (A UAC prompt will appear -- click Yes to continue.)");
            Console.WriteLine();
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + batPath + "\"")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Ok("Installer launched. The clock will be corrected once setup completes.");
                return Done(0, quiet);
            }
            catch (Exception ex)
            {
                Fail("Could not launch the installer: " + ex.Message);
                Console.WriteLine("  Please run Install-TimeFix.bat manually as administrator.");
                return Done(5, quiet);
            }
        }

        Fail("The clock could not be corrected without administrator rights.");
        Console.WriteLine();
        Console.WriteLine("  The time zone was set correctly, but the clock itself is still wrong.");
        Console.WriteLine("  Ask IT to run Install-TimeFix.bat once on this PC (one admin prompt,");
        Console.WriteLine("  one time). After that this program will fix the clock on its own.");
        Console.WriteLine();
        Console.WriteLine("  Meanwhile you can try: right-click the clock -> Adjust date and time");
        Console.WriteLine("  -> Sync now.");
        return Done(5, quiet);
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
                        Console.WriteLine("    source ok: " + src[0] + " (" + sw.ElapsedMilliseconds + " ms)");
                        return utc;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    source failed: " + src[0] + " - " + Short(ex.Message));
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
                Console.WriteLine("    SetSystemTime failed with error " + err + ".");
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
                Console.WriteLine("    could not start the task: " + Short(outp));
                return false;
            }
        }
        catch (Exception ex) { Console.WriteLine("    " + Short(ex.Message)); return false; }
    }

    private static bool WaitForCorrection(out double drift)
    {
        drift = double.MaxValue;
        for (int i = 0; i < 8; i++)          // up to ~40 seconds
        {
            Thread.Sleep(5000);
            DateTime? t = GetNetworkUtc();
            if (t == null) continue;
            drift = (DateTime.UtcNow - t.Value).TotalSeconds;
            if (Math.Abs(drift) <= ToleranceSeconds) return true;
            Console.WriteLine("    still off by " + drift.ToString("N1", CultureInfo.InvariantCulture) + " s, waiting...");
        }
        return false;
    }

    // --- console helpers ----------------------------------------------------

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(no detail)";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
    }

    private static void Ok(string m)   { Write(ConsoleColor.Green,  "  [OK] "  + m); }
    private static void Fail(string m) { Write(ConsoleColor.Red,    "  [!!] "  + m); }

    private static void Write(ConsoleColor c, string m)
    {
        ConsoleColor old = Console.ForegroundColor;
        try { Console.ForegroundColor = c; Console.WriteLine(m); }
        finally { Console.ForegroundColor = old; }
    }

    private static int Done(int code, bool quiet)
    {
        if (!quiet)
        {
            Console.WriteLine();
            Console.Write("  Press Enter to close...");
            try { Console.ReadLine(); } catch { }
        }
        return code;
    }
}
