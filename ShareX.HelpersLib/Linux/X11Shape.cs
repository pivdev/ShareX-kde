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
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShareX.HelpersLib
{
    /// <summary>X Shape extension helpers: the X11 counterpart of Win32 window regions (SetWindowRgn).</summary>
    public static class X11Shape
    {
        private const int ShapeBounding = 0;
        private const int ShapeInput = 2;
        private const int ShapeSet = 0;
        private const int Unsorted = 0;

        private static readonly object sync = new object();
        private static IntPtr display;
        private static bool unavailable;

        [StructLayout(LayoutKind.Sequential)]
        private struct XRectangle
        {
            public short X;
            public short Y;
            public ushort Width;
            public ushort Height;
        }

        /// <summary>
        /// Restricts both the visible area and the input area of an X11 window to the given rectangles (window-relative pixels),
        /// so everything else is see-through and click-through.
        /// </summary>
        public static bool SetWindowShape(IntPtr window, IEnumerable<Rectangle> rectangles)
        {
            if (window == IntPtr.Zero) return false;

            List<XRectangle> rects = new List<XRectangle>();
            foreach (Rectangle r in rectangles)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                rects.Add(new XRectangle
                {
                    X = (short)Math.Clamp(r.X, short.MinValue, short.MaxValue),
                    Y = (short)Math.Clamp(r.Y, short.MinValue, short.MaxValue),
                    Width = (ushort)Math.Min(r.Width, ushort.MaxValue),
                    Height = (ushort)Math.Min(r.Height, ushort.MaxValue)
                });
            }

            lock (sync)
            {
                try
                {
                    if (unavailable) return false;

                    if (display == IntPtr.Zero)
                    {
                        display = XOpenDisplay(IntPtr.Zero);
                        if (display == IntPtr.Zero)
                        {
                            unavailable = true;
                            DebugHelper.WriteLine("X11Shape: unable to open X display.");
                            return false;
                        }
                    }

                    XRectangle[] array = rects.ToArray();
                    XShapeCombineRectangles(display, window, ShapeBounding, 0, 0, array, array.Length, ShapeSet, Unsorted);
                    XShapeCombineRectangles(display, window, ShapeInput, 0, 0, array, array.Length, ShapeSet, Unsorted);
                    XFlush(display);
                    return true;
                }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
                {
                    unavailable = true;
                    DebugHelper.WriteException(e, "X11Shape: libX11/libXext not available.");
                    return false;
                }
            }
        }

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(IntPtr display);

        [DllImport("libXext.so.6")]
        private static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOffset, int yOffset,
            XRectangle[] rectangles, int count, int op, int ordering);
    }
}
