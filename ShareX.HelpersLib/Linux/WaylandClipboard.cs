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

#if LINUX

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Wayland clipboard access through wl-clipboard (wl-copy / wl-paste).
    /// ShareX runs on XWayland, and an X11 selection set while no ShareX window has focus (e.g. after a hotkey
    /// capture) does not reliably reach native Wayland clients. wl-copy uses the data-control protocol and keeps
    /// serving the clipboard from a forked background process, so it works even after ShareX exits.
    /// </summary>
    internal static class WaylandClipboard
    {
        private const int Timeout = 5000;

        private const string MimeTextUtf8 = "text/plain;charset=utf-8";
        private const string MimePng = "image/png";
        private const string MimeUriList = "text/uri-list";

        private static readonly string[] TextTypes = { MimeTextUtf8, "text/plain", "UTF8_STRING", "STRING", "TEXT" };

        private static readonly Lazy<bool> isAvailable = new Lazy<bool>(() =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHAREX_DISABLE_WL_CLIPBOARD")) &&
            LinuxHelpers.HasExecutable("wl-copy") && LinuxHelpers.HasExecutable("wl-paste"));

        public static bool IsAvailable => isAvailable.Value;

        public static bool SetText(string text) => Copy(MimeTextUtf8, Encoding.UTF8.GetBytes(text));

        public static bool SetImage(byte[] png) => Copy(MimePng, png);

        public static bool SetFiles(string[] paths)
        {
            string[] uris = paths.Where(x => File.Exists(x) || Directory.Exists(x)).Select(LinuxShell.ToFileUri).ToArray();
            if (uris.Length == 0) return false;
            // RFC 2483: CRLF separated.
            return Copy(MimeUriList, Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n"));
        }

        public static bool Clear()
        {
            return RunCopy(new[] { "--clear" }, null);
        }

        public static string[] GetTypes()
        {
            (int exitCode, byte[] output) = RunPaste(new[] { "--list-types" });
            if (exitCode != 0 || output == null) return Array.Empty<string>();
            return Encoding.UTF8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public static string GetText()
        {
            string[] types = GetTypes();
            string type = TextTypes.FirstOrDefault(x => types.Contains(x, StringComparer.OrdinalIgnoreCase)) ??
                types.FirstOrDefault(x => x.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase));
            if (type == null) return null;
            byte[] data = GetData(type);
            return data == null ? null : Encoding.UTF8.GetString(data);
        }

        /// <summary>Returns encoded image bytes (PNG preferred, otherwise any other image/* type the clipboard offers).</summary>
        public static byte[] GetImage()
        {
            string[] types = GetTypes();
            string type = types.Contains(MimePng, StringComparer.OrdinalIgnoreCase) ? MimePng :
                types.FirstOrDefault(x => x.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !x.Contains("svg", StringComparison.OrdinalIgnoreCase));
            return type == null ? null : GetData(type);
        }

        public static string[] GetFiles()
        {
            string[] types = GetTypes();
            if (!types.Contains(MimeUriList, StringComparer.OrdinalIgnoreCase)) return null;
            byte[] data = GetData(MimeUriList);
            if (data == null) return null;
            return ParseUriList(Encoding.UTF8.GetString(data));
        }

        public static bool ContainsText(string[] types) =>
            types.Any(x => TextTypes.Contains(x, StringComparer.OrdinalIgnoreCase) || x.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase));

        public static bool ContainsImage(string[] types) =>
            types.Any(x => x.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !x.Contains("svg", StringComparison.OrdinalIgnoreCase));

        public static bool ContainsFiles(string[] types) => types.Contains(MimeUriList, StringComparer.OrdinalIgnoreCase);

        public static ClipboardData Capture()
        {
            ClipboardData snapshot = new ClipboardData();
            string[] types = GetTypes();

            if (ContainsText(types))
            {
                string text = GetText();
                if (text != null)
                {
                    snapshot.SetData(ClipboardDataFormats.UnicodeText, text);
                }
            }

            if (ContainsFiles(types))
            {
                string[] files = GetFiles();
                if (files != null && files.Length > 0)
                {
                    snapshot.SetData(ClipboardDataFormats.FileDrop, files);
                }
            }

            if (ContainsImage(types))
            {
                byte[] image = GetImage();
                if (image != null)
                {
                    snapshot.SetData(MimePng, image);
                }
            }

            foreach (string type in types)
            {
                if (snapshot.GetData(type) == null && (type.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("text/rtf", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] data = GetData(type);
                    if (data != null)
                    {
                        snapshot.SetData(type, Encoding.UTF8.GetString(data));
                    }
                }
            }

            return snapshot;
        }

        private static string[] ParseUriList(string list)
        {
            List<string> paths = new List<string>();

            foreach (string line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#')) continue;

                if (Uri.TryCreate(line, UriKind.Absolute, out Uri uri) && uri.IsFile)
                {
                    paths.Add(uri.LocalPath);
                }
                else if (line.StartsWith('/'))
                {
                    paths.Add(line);
                }
            }

            return paths.ToArray();
        }

        private static byte[] GetData(string type)
        {
            (int exitCode, byte[] output) = RunPaste(new[] { "--no-newline", "--type", type });
            return exitCode == 0 ? output : null;
        }

        private static bool Copy(string type, byte[] data)
        {
            return RunCopy(new[] { "--type", type }, data);
        }

        private static bool RunCopy(IEnumerable<string> args, byte[] stdin)
        {
            ProcessStartInfo psi = new ProcessStartInfo("wl-copy")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin != null,
                // wl-copy forks a child that keeps serving the selection. It inherits redirected stdout/stderr pipes,
                // so redirecting them would block reading until the clipboard changes. Leave them inherited.
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using (Process process = Process.Start(psi))
            {
                if (process == null) return false;

                if (stdin != null)
                {
                    using (Stream input = process.StandardInput.BaseStream)
                    {
                        input.Write(stdin, 0, stdin.Length);
                    }
                }

                if (!process.WaitForExit(Timeout))
                {
                    DebugHelper.WriteLine("wl-copy timed out.");
                    try { process.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0;
            }
        }

        private static (int ExitCode, byte[] Output) RunPaste(IEnumerable<string> args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("wl-paste")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using (Process process = Process.Start(psi))
            {
                if (process == null) return (-1, null);

                using MemoryStream output = new MemoryStream();
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
                Task<string> error = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(Timeout) || !copy.Wait(Timeout))
                {
                    DebugHelper.WriteLine("wl-paste timed out.");
                    try { process.Kill(); } catch { }
                    return (-1, null);
                }

                error.Wait(Timeout);
                return (process.ExitCode, output.ToArray());
            }
        }
    }
}

#endif
