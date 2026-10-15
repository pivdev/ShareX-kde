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

using System.Drawing;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenRecordingLib.Video;

// linux-port: Windows.Graphics.Capture / Direct3D are unavailable. The native recorder is
// replaced by LinuxScreenRecorder on Linux; these stubs only keep the shared pipeline compiling.
internal sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device => throw new PlatformNotSupportedException();
    public ID3D11DeviceContext Context => throw new PlatformNotSupportedException();

    public GraphicsDevice(nint monitor) => throw new PlatformNotSupportedException("Direct3D capture is not available on Linux.");

    public ID3D11Texture2D CreateTexture(int width, int height, Format format, BindFlags bindFlags) => throw new PlatformNotSupportedException();

    public void Dispose() { }
}

internal sealed class GraphicsCapture : IDisposable
{
    internal sealed record Target(object Item, Rectangle Source, Point Destination, nint Monitor, nint WindowHandle = 0);

    public int Width => 0;
    public int Height => 0;
    public bool HasFrame => false;
    public long LatestTimestamp => 0;
    public bool HasEnded => true;

    public static List<Target> GetTargets(RecordingOptions options, out int width, out int height) =>
        throw new PlatformNotSupportedException("Windows Graphics Capture is not available on Linux.");

    public GraphicsCapture(GraphicsDevice graphics, GpuVideoProcessor processor, List<Target> targets, bool cursor, int fps) =>
        throw new PlatformNotSupportedException();

    public void Retarget(List<Target> targets) { }
    public void Start() { }
    public void Update() { }
    public void Dispose() { }
}

#endif
