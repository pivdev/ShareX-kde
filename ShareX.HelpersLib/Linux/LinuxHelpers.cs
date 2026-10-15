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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ShareX.HelpersLib
{
    public static class LinuxHelpers
    {
        public static bool IsWayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
            string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

        public static bool IsKDE => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);

        public static string FindExecutable(string name)
        {
            if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
            foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public static bool HasExecutable(string name) => FindExecutable(name) != null;

        public static string GetProcessExecutablePath(int pid)
        {
            try
            {
                return new FileInfo($"/proc/{pid}/exe").LinkTarget;
            }
            catch
            {
                return null;
            }
        }

        public static (int ExitCode, string Output, string Error) Run(string fileName, IEnumerable<string> args, string stdin = null, int timeoutMs = 30000)
        {
            ProcessStartInfo psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using Process process = Process.Start(psi);
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return (-1, output.IsCompleted ? output.Result : "", "Timed out");
            }
            return (process.ExitCode, output.Result, error.Result);
        }

        /// <summary>Opens a file, folder or URL with the desktop's default handler.</summary>
        public static bool OpenWithDefault(string target)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(target);
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return false;
            }
        }
    }
}
