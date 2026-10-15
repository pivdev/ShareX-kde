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
using System.Drawing;
using System.IO;
using System.Threading;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Feeds raw BGRA frames of a screen region to an encoder (FFmpeg rawvideo on stdin) at a constant frame rate.
    /// Frames come from KWin's ScreenShot2.CaptureArea, so no portal dialog is needed and the region can be anything.
    /// When a capture takes longer than a frame interval, the previous frame is repeated to keep real-time timing.
    /// </summary>
    public sealed class LinuxFramePump
    {
        public Rectangle Region { get; }
        public int FPS { get; }
        public bool DrawCursor { get; }

        public int CapturedFrames { get; private set; }
        public int WrittenFrames { get; private set; }

        public LinuxFramePump(Rectangle region, int fps, bool drawCursor)
        {
            Region = region;
            FPS = Math.Max(1, fps);
            DrawCursor = drawCursor;
        }

        public void Run(Stream output, Func<bool> stopRequested, Action firstFrameWritten = null)
        {
            Rectangle compositorRegion = LinuxScreenCapture.ToCompositorRectangle(Region);
            Stopwatch clock = null;
            byte[] lastFrame = null;
            double captureTimeTotal = 0;

            try
            {
                while (!stopRequested())
                {
                    Stopwatch captureTimer = Stopwatch.StartNew();
                    byte[] frame = LinuxScreenCapture.CaptureAreaRaw(compositorRegion, Region.Size, DrawCursor);
                    captureTimeTotal += captureTimer.Elapsed.TotalMilliseconds;
                    CapturedFrames++;

                    if (clock == null)
                    {
                        clock = Stopwatch.StartNew();
                    }

                    // Number of frames that should have been emitted by now, including this one.
                    long due = (long)(clock.Elapsed.TotalSeconds * FPS) + 1;

                    while (lastFrame != null && WrittenFrames < due - 1)
                    {
                        output.Write(lastFrame);
                        WrittenFrames++;
                    }

                    output.Write(frame);
                    WrittenFrames++;
                    lastFrame = frame;

                    if (WrittenFrames == 1)
                    {
                        output.Flush();
                        firstFrameWritten?.Invoke();
                    }

                    double sleepMilliseconds = (WrittenFrames * 1000.0 / FPS) - clock.Elapsed.TotalMilliseconds;
                    if (sleepMilliseconds > 1)
                    {
                        Thread.Sleep((int)sleepMilliseconds);
                    }
                }
            }
            catch (IOException)
            {
                // The encoder exited, e.g. because a fixed recording duration was reached.
            }
            finally
            {
                try
                {
                    output.Flush();
                    output.Close();
                }
                catch (IOException)
                {
                }

                DebugHelper.WriteLine("Linux frame pump finished. Region: {0}, FPS: {1}, captured frames: {2}, written frames: {3}, average capture time: {4:0.0} ms",
                    Region, FPS, CapturedFrames, WrittenFrames, CapturedFrames > 0 ? captureTimeTotal / CapturedFrames : 0);
            }
        }
    }
}
