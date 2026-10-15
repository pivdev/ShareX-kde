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

using ShareX.HelpersLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX.NativeMessagingHost
{
    internal class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                try
                {
                    HelpersLib.NativeMessagingHost host = new HelpersLib.NativeMessagingHost();
                    string input = host.Read();

                    if (!string.IsNullOrEmpty(input))
                    {
                        host.Write(input);

                        string tempFilePath = FileHelpers.GetTempFilePath("json");
                        File.WriteAllText(tempFilePath, input, Encoding.UTF8);

                        if (OperatingSystem.IsLinux())
                        {
                            StartShareXDetached(FileHelpers.GetAbsolutePath("ShareX"), "-NativeMessagingInput", tempFilePath);
                        }
                        else
                        {
                            string filePath = FileHelpers.GetAbsolutePath("ShareX.exe");
                            string argument = $"-NativeMessagingInput \"{tempFilePath}\"";
                            NativeMethods.CreateProcess(filePath, argument, CreateProcessFlags.CREATE_BREAKAWAY_FROM_JOB);
                        }
                    }
                }
                catch (Exception e)
                {
                    e.ShowError();
                }
            }
            else
            {
                MessageBox.Show("This executable is used to receive data from browser addon and send it to ShareX.",
                    "ShareX NativeMessagingHost", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // Unix equivalent of CREATE_BREAKAWAY_FROM_JOB: browsers kill the host's process group when the port closes and
        // wait on its stdio pipes, so start ShareX in a new session with stdio detached from the browser.
        private static void StartShareXDetached(string filePath, params string[] arguments)
        {
            string script = LinuxHelpers.HasExecutable("setsid") ?
                "exec setsid -f \"$0\" \"$@\" </dev/null >/dev/null 2>&1" :
                "\"$0\" \"$@\" </dev/null >/dev/null 2>&1 &";

            ProcessStartInfo psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(filePath);

            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using (Process process = Process.Start(psi))
            {
                process?.WaitForExit(5000);
            }
        }
    }
}
