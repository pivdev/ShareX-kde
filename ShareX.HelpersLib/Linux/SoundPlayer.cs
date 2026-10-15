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
using System.Diagnostics;
using System.IO;

namespace ShareX.HelpersLib
{
    // Minimal replacement for System.Media.SoundPlayer that shells out to PipeWire/PulseAudio players.
    public sealed class SoundPlayer : IDisposable
    {
        private static readonly string[] players = { "pw-play", "paplay", "aplay" };

        private readonly string filePath;
        private readonly bool deleteFile;

        public SoundPlayer(string filePath)
        {
            this.filePath = filePath;
        }

        public SoundPlayer(Stream stream)
        {
            filePath = Path.Combine(Path.GetTempPath(), $"sharex-sound-{Guid.NewGuid():N}.wav");
            using (FileStream fs = File.Create(filePath))
            {
                stream.CopyTo(fs);
            }
            deleteFile = true;
        }

        public void Play() => Start()?.Dispose();

        public void PlaySync()
        {
            using Process process = Start();
            process?.WaitForExit(10000);
        }

        private Process Start()
        {
            foreach (string player in players)
            {
                string playerPath = LinuxHelpers.FindExecutable(player);
                if (playerPath == null) continue;

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(playerPath) { UseShellExecute = false, CreateNoWindow = true };
                    psi.ArgumentList.Add(filePath);
                    return Process.Start(psi);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (deleteFile)
            {
                // Players read the file asynchronously; delete a little later.
                string path = filePath;
                System.Threading.Tasks.Task.Delay(15000).ContinueWith(_ => { try { File.Delete(path); } catch { } });
            }
        }
    }
}

#endif
