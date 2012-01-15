//
// SystemInformation.cs
//
// Copyright (C) 2004-2006 Novell, Inc.
//

//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Beagrep.Util {

	public class SystemInformation {

		static int getloadavg (double[] loadavg, int nelem) {
			for (int i = 0; i < nelem; i++) {
				loadavg[i] = 1;
			}
			return nelem;
		}

		const double loadavg_poll_delay = 3;
		private static DateTime proc_loadavg_time  = DateTime.MinValue;
		private static double cached_loadavg_1min  = -1;
		private static double cached_loadavg_5min  = -1;
		private static double cached_loadavg_15min = -1;

		private static void CheckLoadAverage ()
		{
			// Only call getloadavg() at most once every 10 seconds
			if ((DateTime.UtcNow - proc_loadavg_time).TotalSeconds < loadavg_poll_delay)
				return;

			double [] loadavg = new double [3];
			int retval = getloadavg (loadavg, 3);

			if (retval == -1)
				throw new IOException ("Could not get system load average: " + Mono.Unix.Native.Stdlib.strerror (Mono.Unix.Native.Stdlib.GetLastError ()));
			else if (retval != 3)
				throw new IOException ("Could not get system load average: getloadavg() returned an unexpected number of samples");

			cached_loadavg_1min  = loadavg [0];
			cached_loadavg_5min  = loadavg [1];
			cached_loadavg_15min = loadavg [2];

			proc_loadavg_time = DateTime.UtcNow;
		}

		public static double LoadAverageOneMinute {
			get {
				CheckLoadAverage ();
				return cached_loadavg_1min;
			}
		}
		
		public static double LoadAverageFiveMinute {
			get {
				CheckLoadAverage ();
				return cached_loadavg_5min;
			}
		}

		public static double LoadAverageFifteenMinute {
			get {
				CheckLoadAverage ();
				return cached_loadavg_15min;
			}
		}

		///////////////////////////////////////////////////////////////

		private static bool use_screensaver = false;
		const double screensaver_poll_delay = 1;
		private static DateTime screensaver_time = DateTime.MinValue;
		private static bool cached_screensaver_running = false;
		private static double cached_screensaver_idle_time = 0;

		private enum ScreenSaverState {
			Off      = 0,
			On       = 1,
			Cycle    = 2,
			Disabled = 3
		}

		private enum ScreenSaverKind {
			Blanked  = 0,
			Internal = 1,
			External = 2
		}

		/// <summary>
		/// BeagrepDaemon needs to monitor screensaver status
		/// for faster scheduling when user is idle.
		/// IndexHelper does not need to monitor screensaver status.
		/// XssInit is only called from the BeagrepDaemon.
		///
		/// </summary>
		/// <returns>
		/// A <see cref="System.Boolean"/>
		/// </returns>
		public static bool XssInit ()
		{
			int has_xss = 0;
			use_screensaver = (has_xss == 1);
			return use_screensaver;
		}


		private static void CheckScreenSaver ()
		{
			return;
		}

		public static bool ScreenSaverRunning {
			get {
				return false;
			}
		}

		/// <value>
		///  returns number of seconds since input was received
		/// from the user on any input device
		/// </value>
		public static double InputIdleTime {
			get {
				CheckScreenSaver ();
				return cached_screensaver_idle_time;
			}
		}

		///////////////////////////////////////////////////////////////

		public static int VmSize {
			get { return 1000; }
		}

		public static int VmRss {
			get { return 1000; }
		}

		public static void LogMemoryUsage ()
		{
			int vm_size = VmSize;
			int vm_rss = VmRss;

			Logger.Log.Debug ("Memory usage: VmSize={0:.0} MB, VmRSS={1:.0} MB,  GC.GetTotalMemory={2} ({3} colls)",
					  vm_size/1024.0, vm_rss/1024.0, GC.GetTotalMemory (false), GC.CollectionCount (2));
		}

		///////////////////////////////////////////////////////////////

		// Internal calls have to be installed before mono accesses the
		// class that uses them.  That's why we have this retarded extra
		// class and initializer function.  Paolo says this is a *HUGE*
		// unsupported hack and not to be surprised if it doesn't work.

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		public extern static int GetObjectSizeIcall (object o);

		public static int GetObjectSize (object o)
		{
			try {
				return GetObjectSizeIcall (o);
			} catch (MissingMethodException) {
				return -1;
			}
		}

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		public extern static int GetObjectPointerIcall (object o);

		public static long GetObjectPointer (object o)
		{
			try {
				return GetObjectPointerIcall (o);
			} catch (MissingMethodException) {
				return -1;
			}
		}

		///////////////////////////////////////////////////////////////

		private static int disk_stats_read_reqs;
		private static int disk_stats_write_reqs;
		private static int disk_stats_read_bytes;
		private static int disk_stats_write_bytes;

		private static DateTime disk_stats_time = DateTime.MinValue;
		private static double disk_stats_delay = 1.0;

		private static uint major, minor;

		// Update the disk statistics with data for block device on the (major,minor) pair.
		private static void UpdateDiskStats ()
		{
			string buffer;

			if (major == 0)
				return;

			// We only refresh the stats once per second
			if ((DateTime.Now - disk_stats_time).TotalSeconds < disk_stats_delay)
				return;

			// Read in all of the disk stats
			using (StreamReader stream = new StreamReader ("/proc/diskstats"))
				buffer = stream.ReadToEnd ();

			// Find our partition and parse it
			const string REGEX = "[\\s]+{0}[\\s]+{1}[\\s]+[a-zA-Z0-9]+[\\s]+([0-9]+)[\\s]+([0-9]+)[\\s]+([0-9]+)[\\s]+([0-9]+)";
			string regex = String.Format (REGEX, major, minor);
			Regex r = new Regex (regex, RegexOptions.IgnoreCase | RegexOptions.Compiled);
			for (System.Text.RegularExpressions.Match m = r.Match (buffer); m.Success; m = m.NextMatch ()) {
				disk_stats_read_reqs = Convert.ToInt32 (m.Groups[1].ToString ());
				disk_stats_read_bytes = Convert.ToInt32 (m.Groups[2].ToString ());
				disk_stats_write_reqs = Convert.ToInt32 (m.Groups[3].ToString ());
				disk_stats_write_bytes = Convert.ToInt32 (m.Groups[4].ToString ());
			}

			disk_stats_time = DateTime.Now;
		}

		// Get the (major,minor) pair for the block device from which the index is mounted.
		private static void GetIndexDev ()
		{
			try {
				throw new Exception("hello world GetIndexDev");
			} catch (Exception e) {
				Console.WriteLine("GetIndexDev called: {0}", e);
			}

			Mono.Unix.Native.Stat stat;
			if (Mono.Unix.Native.Syscall.stat (PathFinder.StorageDir, out stat) != 0)
				return;

			major = (uint) stat.st_dev >> 8;
			minor = (uint) stat.st_dev & 0xff;
		}

		public static int DiskStatsReadReqs {
			get {
				if (major == 0)
					 GetIndexDev ();
				UpdateDiskStats ();
				return disk_stats_read_reqs;
			}
		}

		public static int DiskStatsReadBytes {
			get {
				if (major == 0)
					 GetIndexDev ();
				UpdateDiskStats ();
				return disk_stats_read_bytes;
			}
		}

		public static int DiskStatsWriteReqs {
			get {
				if (major == 0)
					 GetIndexDev ();
				UpdateDiskStats ();
				return disk_stats_write_reqs;
			}
		}

		public static int DiskStatsWriteBytes {
			get {
				if (major == 0)
					 GetIndexDev ();
				UpdateDiskStats ();
				return disk_stats_write_bytes;
			}
		}

		public static bool IsPathOnBlockDevice (string path)
		{
			return true;
		}

		///////////////////////////////////////////////////////////////

		// From /usr/include/linux/prctl.h
		private const int PR_SET_NAME = 15;

		public static void SetProcessName(string name)
		{
		}

		///////////////////////////////////////////////////////////////

		public static string MonoRuntimeVersion {
			get {
				Type t = typeof (object).Assembly.GetType ("Mono.Runtime");

				string ver = (string) t.InvokeMember ("GetDisplayName",
								      BindingFlags.InvokeMethod
								      | BindingFlags.NonPublic
								      | BindingFlags.Static
								      | BindingFlags.DeclaredOnly
								      | BindingFlags.ExactBinding,
								      null, null, null);
				
				return ver;
			}
		}

		///////////////////////////////////////////////////////////////

#if false
		public static void Main ()
		{
			while (true) {
				Console.WriteLine ("{0} {1} {2} {3} {4} {5} {6}",
						   LoadAverageOneMinute,
						   LoadAverageFiveMinute,
						   LoadAverageFifteenMinute,
						   ScreenSaverRunning,
						   InputIdleTime,
						   DiskStatsReadReqs,
						   VmSize);
				System.Threading.Thread.Sleep (1000);
			}
		}
#endif
	}
}
