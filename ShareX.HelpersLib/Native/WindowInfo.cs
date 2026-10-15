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
using System.Diagnostics;
using System.Drawing;

namespace ShareX.HelpersLib
{
    public class WindowInfo
    {
        public IntPtr Handle { get; }

        public bool IsHandleCreated => Handle != IntPtr.Zero;

        public string Text => OperatingSystem.IsLinux() ? LinuxWindow?.Caption : NativeMethods.GetWindowText(Handle);

        public string ClassName => OperatingSystem.IsLinux() ? LinuxWindow?.ResourceClass ?? "" : NativeMethods.GetClassName(Handle);

        public Process Process => OperatingSystem.IsLinux() ? GetLinuxProcess() : NativeMethods.GetProcessByWindowHandle(Handle);

        public string ProcessName
        {
            get
            {
                using (Process process = Process)
                {
                    return process?.ProcessName;
                }
            }
        }

        public string ProcessFilePath
        {
            get
            {
                if (OperatingSystem.IsLinux())
                {
                    return LinuxWindow is { Pid: > 0 } window ? LinuxHelpers.GetProcessExecutablePath(window.Pid) : null;
                }

                using (Process process = Process)
                {
                    return process?.MainModule?.FileName;
                }
            }
        }

        public string ProcessFileName => FileHelpers.GetFileNameSafe(ProcessFilePath);

        public int ProcessId
        {
            get
            {
                if (OperatingSystem.IsLinux())
                {
                    return LinuxWindow?.Pid ?? 0;
                }

                using (Process process = Process)
                {
                    return process.Id;
                }
            }
        }

        public IntPtr Parent => OperatingSystem.IsLinux() ? IntPtr.Zero : NativeMethods.GetParent(Handle);

        public Rectangle Rectangle => CaptureHelpers.GetWindowRectangle(Handle);

        public Rectangle ClientRectangle => OperatingSystem.IsLinux() ? LinuxWindow?.ClientBounds ?? Rectangle.Empty : NativeMethods.GetClientRect(Handle);

        public WindowStyles Style
        {
            get
            {
                return (WindowStyles)(ulong)NativeMethods.GetWindowLong(Handle, NativeConstants.GWL_STYLE);
            }
            set
            {
                NativeMethods.SetWindowLong(Handle, NativeConstants.GWL_STYLE, (IntPtr)value);
            }
        }

        public WindowStyles ExStyle
        {
            get
            {
                return (WindowStyles)(ulong)NativeMethods.GetWindowLong(Handle, NativeConstants.GWL_EXSTYLE);
            }
            set
            {
                NativeMethods.SetWindowLong(Handle, NativeConstants.GWL_EXSTYLE, (IntPtr)value);
            }
        }

        public bool Layered
        {
            get
            {
                return ExStyle.HasFlag(WindowStyles.WS_EX_LAYERED);
            }
            set
            {
                if (value)
                {
                    ExStyle |= WindowStyles.WS_EX_LAYERED;
                }
                else
                {
                    ExStyle &= ~WindowStyles.WS_EX_LAYERED;
                }
            }
        }

        public bool TopMost
        {
            get
            {
                if (OperatingSystem.IsLinux()) return LinuxWindow?.KeepAbove ?? false;

                return ExStyle.HasFlag(WindowStyles.WS_EX_TOPMOST);
            }
            set
            {
                if (OperatingSystem.IsLinux())
                {
                    KWinBridge.SetWindowProperty(Handle, "keepAbove", value);
                    return;
                }

                SetWindowPos(value ? (IntPtr)NativeConstants.HWND_TOPMOST : (IntPtr)NativeConstants.HWND_NOTOPMOST,
                    SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE);
            }
        }

        public byte Opacity
        {
            get
            {
                if (OperatingSystem.IsLinux()) return 255;

                if (Layered)
                {
                    NativeMethods.GetLayeredWindowAttributes(Handle, out _, out byte alpha, out _);
                    return alpha;
                }

                return 255;
            }
            set
            {
                if (OperatingSystem.IsLinux())
                {
                    KWinBridge.SetWindowProperty(Handle, "opacity", value / 255.0);
                    return;
                }

                if (value < 255)
                {
                    Layered = true;
                    NativeMethods.SetLayeredWindowAttributes(Handle, 0, value, NativeConstants.LWA_ALPHA);
                }
                else
                {
                    Layered = false;
                }
            }
        }

        public Icon Icon => OperatingSystem.IsLinux() ? null : NativeMethods.GetApplicationIcon(Handle);

        public bool IsMaximized => !OperatingSystem.IsLinux() && NativeMethods.IsZoomed(Handle);

        public bool IsMinimized => OperatingSystem.IsLinux() ? LinuxWindow?.Minimized ?? false : NativeMethods.IsIconic(Handle);

        public bool IsVisible => OperatingSystem.IsLinux() ? LinuxWindow?.IsVisible ?? false : NativeMethods.IsWindowVisible(Handle);

        public bool IsCloaked => !OperatingSystem.IsLinux() && NativeMethods.IsWindowCloaked(Handle);

        public bool IsActive => OperatingSystem.IsLinux() ? CaptureHelpers.GetForegroundWindow() == Handle : NativeMethods.IsActive(Handle);

        public WindowInfo(IntPtr handle)
        {
            Handle = handle;
        }

        private LinuxWindow LinuxWindow => KWinBridge.GetWindow(Handle);

        private Process GetLinuxProcess()
        {
            int pid = LinuxWindow?.Pid ?? 0;
            if (pid <= 0) return null;

            try
            {
                return Process.GetProcessById(pid);
            }
            catch
            {
                return null;
            }
        }

        public void Activate()
        {
            if (OperatingSystem.IsLinux())
            {
                KWinBridge.ActivateWindowAsync(Handle).Wait(2000);
                return;
            }

            if (IsHandleCreated)
            {
                NativeMethods.SetForegroundWindow(Handle);
            }
        }

        public void BringToFront()
        {
            if (OperatingSystem.IsLinux())
            {
                Activate();
                return;
            }

            if (IsHandleCreated)
            {
                SetWindowPos(SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE);
            }
        }

        public void Restore()
        {
            if (OperatingSystem.IsLinux())
            {
                Activate();
                return;
            }

            if (IsHandleCreated)
            {
                NativeMethods.ShowWindow(Handle, (int)WindowShowStyle.Restore);
            }
        }

        public void SetWindowPos(SetWindowPosFlags flags)
        {
            SetWindowPos((IntPtr)NativeConstants.HWND_TOP, 0, 0, 0, 0, flags);
        }

        public void SetWindowPos(Rectangle rect, SetWindowPosFlags flags)
        {
            SetWindowPos((IntPtr)NativeConstants.HWND_TOP, rect.X, rect.Y, rect.Width, rect.Height, flags);
        }

        public void SetWindowPos(IntPtr insertAfter, SetWindowPosFlags flags)
        {
            SetWindowPos(insertAfter, 0, 0, 0, 0, flags);
        }

        public void SetWindowPos(IntPtr insertAfter, int x, int y, int width, int height, SetWindowPosFlags flags)
        {
            if (OperatingSystem.IsLinux()) return;

            NativeMethods.SetWindowPos(Handle, insertAfter, x, y, width, height, flags);
        }

        public override string ToString()
        {
            return Text;
        }
    }
}