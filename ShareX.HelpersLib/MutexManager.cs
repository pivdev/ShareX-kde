#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.IO;
using System.Threading;

namespace ShareX.HelpersLib
{
    public class MutexManager : IDisposable
    {
        public bool HasHandle { get; private set; }

        private Mutex mutex;
#if LINUX
        // On Unix .NET named mutexes are scoped to the Unix session (getsid), so apps started from the desktop
        // menu, autostart and a terminal would not see each other. Use an flock()-based lock file in the
        // per-user runtime directory instead; the kernel releases it automatically if the process dies.
        private FileStream lockFile;

        private static string GetLockFilePath(string mutexName)
        {
            return Path.Combine(LinuxShell.XdgRuntimeDir, $"sharex-{FileHelpers.SanitizeFileName(mutexName)}.lock");
        }

        private static FileStream TryAcquireLockFile(string mutexName)
        {
            try
            {
                // FileShare.None makes .NET take an exclusive flock() on the file.
                return new FileStream(GetLockFilePath(mutexName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException)
            {
                return null;
            }
        }
#endif

        public MutexManager(string mutexName) : this(mutexName, Timeout.Infinite)
        {
        }

        public MutexManager(string mutexName, int timeout)
        {
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                long start = Environment.TickCount64;

                while ((lockFile = TryAcquireLockFile(mutexName)) == null)
                {
                    if (timeout != Timeout.Infinite && Environment.TickCount64 - start >= timeout)
                    {
                        break;
                    }

                    Thread.Sleep(50);
                }

                HasHandle = lockFile != null;
                return;
            }
#endif

            mutex = new Mutex(false, mutexName);

            try
            {
                HasHandle = mutex.WaitOne(timeout, false);
            }
            catch (AbandonedMutexException)
            {
                HasHandle = true;
            }
        }

        public static bool IsRunning(string mutexName)
        {
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                using (FileStream stream = TryAcquireLockFile(mutexName))
                {
                    return stream == null;
                }
            }
#endif

            try
            {
                using (Mutex mutex = new Mutex(false, mutexName, out bool createdNew))
                {
                    return !createdNew;
                }
            }
            catch
            {
            }

            return false;
        }

        public void Dispose()
        {
#if LINUX
            if (lockFile != null)
            {
                lockFile.Dispose();
                lockFile = null;
            }
#endif

            if (mutex != null)
            {
                if (HasHandle)
                {
                    mutex.ReleaseMutex();
                }

                mutex.Dispose();
            }
        }
    }
}