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

using Microsoft.Win32.SafeHandles;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    public enum LinuxCaptureBackend
    {
        None,
        KWin,
        Portal,
        CommandLine
    }

    /// <summary>
    /// Screen capture on Linux. Prefers KWin's ScreenShot2 D-Bus API (fast, no prompts; requires the ShareX .desktop
    /// file to declare X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2), then xdg-desktop-portal, then CLI tools.
    /// Rectangles are in ShareX desktop coordinates (the coordinate space of <see cref="DesktopScreen"/>).
    /// </summary>
    public static class LinuxScreenCapture
    {
        private const string KWinService = "org.kde.KWin";
        private const string KWinScreenShotPath = "/org/kde/KWin/ScreenShot2";
        private const string KWinScreenShotInterface = "org.kde.KWin.ScreenShot2";

        private static bool kwinUnauthorized;
        private static bool portalFailed;

        public static LinuxCaptureBackend LastBackend { get; private set; }

        public static bool IsKWinCaptureAuthorized => !kwinUnauthorized;

        /// <summary>Captures <paramref name="rect"/> out of the desktop whose full extent is <paramref name="desktopBounds"/>.</summary>
        public static SKBitmap CaptureRectangle(Rectangle rect, Rectangle desktopBounds, bool includeCursor)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;

            using SKBitmap desktop = CaptureDesktop(includeCursor);
            if (desktop == null) return null;

            return CropFromDesktop(desktop, desktopBounds, rect);
        }

        public static SKBitmap CaptureDesktop(bool includeCursor) => LinuxDBus.RunSync(() => CaptureDesktopAsync(includeCursor));

        public static async Task<SKBitmap> CaptureDesktopAsync(bool includeCursor)
        {
            List<Exception> errors = new List<Exception>();

            if (!kwinUnauthorized && KWinBridge.IsAvailable)
            {
                try
                {
                    SKBitmap bitmap = await KWinCaptureAsync("CaptureWorkspace", "a{sv}h", null, new Dictionary<string, VariantValue>
                    {
                        ["include-cursor"] = includeCursor,
                        ["native-resolution"] = true
                    }).ConfigureAwait(false);
                    LastBackend = LinuxCaptureBackend.KWin;
                    return bitmap;
                }
                catch (DBusErrorReplyException e) when (e.ErrorName?.EndsWith("NoAuthorized", StringComparison.Ordinal) == true)
                {
                    kwinUnauthorized = true;
                    DebugHelper.WriteLine("KWin ScreenShot2 is not authorized for this executable (" + Environment.ProcessPath +
                        "). Install ShareX with linux/install.sh so its .desktop file grants access. Falling back to xdg-desktop-portal.");
                }
                catch (Exception e)
                {
                    errors.Add(e);
                    DebugHelper.WriteException(e, "KWin screen capture failed.");
                }
            }

            if (!portalFailed)
            {
                try
                {
                    SKBitmap bitmap = await PortalCaptureAsync().ConfigureAwait(false);
                    if (bitmap != null)
                    {
                        LastBackend = LinuxCaptureBackend.Portal;
                        return bitmap;
                    }
                }
                catch (Exception e)
                {
                    portalFailed = true;
                    errors.Add(e);
                    DebugHelper.WriteException(e, "xdg-desktop-portal screen capture failed.");
                }
            }

            SKBitmap cliBitmap = CommandLineCapture(includeCursor);
            if (cliBitmap != null)
            {
                LastBackend = LinuxCaptureBackend.CommandLine;
                return cliBitmap;
            }

            LastBackend = LinuxCaptureBackend.None;
            throw new InvalidOperationException("No working screen capture backend was found (KWin ScreenShot2, xdg-desktop-portal, spectacle, grim).",
                errors.Count > 0 ? new AggregateException(errors) : null);
        }

        /// <summary>Captures a single window directly from the compositor (not affected by overlapping windows).</summary>
        public static SKBitmap CaptureWindow(IntPtr handle, bool includeCursor, bool includeDecoration, bool includeShadow)
        {
            LinuxWindow window = KWinBridge.GetWindow(handle);
            if (window == null || kwinUnauthorized || !KWinBridge.IsAvailable) return null;

            return LinuxDBus.RunSync(async () =>
            {
                try
                {
                    SKBitmap bitmap = await KWinCaptureAsync("CaptureWindow", "sa{sv}h", window.Id, new Dictionary<string, VariantValue>
                    {
                        ["include-cursor"] = includeCursor,
                        ["include-decoration"] = includeDecoration,
                        ["include-shadow"] = includeShadow,
                        ["native-resolution"] = true
                    }).ConfigureAwait(false);
                    LastBackend = LinuxCaptureBackend.KWin;
                    return bitmap;
                }
                catch (DBusErrorReplyException e) when (e.ErrorName?.EndsWith("NoAuthorized", StringComparison.Ordinal) == true)
                {
                    kwinUnauthorized = true;
                    return null;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "KWin window capture failed.");
                    return null;
                }
            });
        }

        public static SKBitmap CropFromDesktop(SKBitmap desktop, Rectangle desktopBounds, Rectangle rect)
        {
            // The compositor image may be in device pixels (HiDPI) while desktop coordinates are logical; scale proportionally.
            double scaleX = desktopBounds.Width > 0 ? (double)desktop.Width / desktopBounds.Width : 1;
            double scaleY = desktopBounds.Height > 0 ? (double)desktop.Height / desktopBounds.Height : 1;

            SKRectI source = SKRectI.Create(
                (int)Math.Round((rect.X - desktopBounds.X) * scaleX),
                (int)Math.Round((rect.Y - desktopBounds.Y) * scaleY),
                (int)Math.Round(rect.Width * scaleX),
                (int)Math.Round(rect.Height * scaleY));
            source.Intersect(SKRectI.Create(0, 0, desktop.Width, desktop.Height));
            if (source.Width <= 0 || source.Height <= 0) return null;

            SKBitmap result = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (SKCanvas canvas = new SKCanvas(result))
            {
                canvas.Clear(SKColors.Transparent);
                SKRect destination = SKRect.Create(
                    (float)((source.Left / scaleX) - (rect.X - desktopBounds.X)),
                    (float)((source.Top / scaleY) - (rect.Y - desktopBounds.Y)),
                    (float)(source.Width / scaleX),
                    (float)(source.Height / scaleY));
                using SKPaint paint = new SKPaint { BlendMode = SKBlendMode.Src };
                using SKImage image = SKImage.FromBitmap(desktop);
                SKSamplingOptions sampling = scaleX == 1 && scaleY == 1 ? SKSamplingOptions.Default : new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(image, source, destination, sampling, paint);
            }
            return result;
        }

        private static async Task<SKBitmap> KWinCaptureAsync(string method, string signature, string firstArgument, Dictionary<string, VariantValue> options)
        {
            (Dictionary<string, VariantValue> metadata, byte[] data) = await KWinCaptureRawAsync(method, signature,
                (ref MessageWriter w) => { if (firstArgument != null) w.WriteString(firstArgument); }, options).ConfigureAwait(false);
            return DecodeKWinImage(metadata, data);
        }

        private static async Task<(Dictionary<string, VariantValue> Metadata, byte[] Data)> KWinCaptureRawAsync(string method, string signature,
            LinuxDBus.BodyWriter leadingArguments, Dictionary<string, VariantValue> options)
        {
            (FileStream readStream, SafeFileHandle writeHandle) = LinuxDBus.CreatePipe();
            using (readStream)
            {
                Task<byte[]> readTask = Task.Run(() =>
                {
                    using MemoryStream ms = new MemoryStream();
                    readStream.CopyTo(ms);
                    return ms.ToArray();
                });

                Dictionary<string, VariantValue> metadata;
                using (writeHandle)
                {
                    metadata = await LinuxDBus.CallAsync(KWinService, KWinScreenShotPath, KWinScreenShotInterface, method, signature,
                        (ref MessageWriter w) =>
                        {
                            leadingArguments?.Invoke(ref w);
                            LinuxDBus.WriteOptions(ref w, options);
                            w.WriteHandle(writeHandle);
                        },
                        (m, s) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue()).ConfigureAwait(false);
                }
                // Our copy of the write end is closed now, so the read completes once KWin finishes writing.

                return (metadata, await readTask.ConfigureAwait(false));
            }
        }

        /// <summary>Maps a rectangle in ShareX desktop coordinates to KWin's logical compositor coordinates.</summary>
        public static Rectangle ToCompositorRectangle(Rectangle rect)
        {
            if (!KWinBridge.IsAvailable) return rect;

            KWinState state = KWinBridge.Query(0);
            if (state.Outputs.Count == 0) return rect;

            Rectangle compositorBounds = state.Outputs.Select(x => x.Bounds).Aggregate(Rectangle.Union);
            Rectangle desktopBounds = CaptureHelpers.GetScreenBounds();
            if (compositorBounds == desktopBounds || desktopBounds.Width <= 0 || desktopBounds.Height <= 0) return rect;

            double scaleX = (double)compositorBounds.Width / desktopBounds.Width;
            double scaleY = (double)compositorBounds.Height / desktopBounds.Height;
            return Rectangle.FromLTRB(
                compositorBounds.X + (int)Math.Floor((rect.Left - desktopBounds.X) * scaleX),
                compositorBounds.Y + (int)Math.Floor((rect.Top - desktopBounds.Y) * scaleY),
                compositorBounds.X + (int)Math.Ceiling((rect.Right - desktopBounds.X) * scaleX),
                compositorBounds.Y + (int)Math.Ceiling((rect.Bottom - desktopBounds.Y) * scaleY));
        }

        /// <summary>
        /// Captures an area (KWin logical coordinates) and returns tightly packed BGRA pixels of exactly
        /// <paramref name="outputSize"/>, as used for rawvideo encoder input. Requires KWin ScreenShot2 access.
        /// </summary>
        public static byte[] CaptureAreaRaw(Rectangle compositorArea, Size outputSize, bool includeCursor)
        {
            (Dictionary<string, VariantValue> metadata, byte[] data) = LinuxDBus.RunSync(() => KWinCaptureRawAsync("CaptureArea", "iiuua{sv}h",
                (ref MessageWriter w) =>
                {
                    w.WriteInt32(compositorArea.X);
                    w.WriteInt32(compositorArea.Y);
                    w.WriteUInt32((uint)compositorArea.Width);
                    w.WriteUInt32((uint)compositorArea.Height);
                },
                new Dictionary<string, VariantValue>
                {
                    ["include-cursor"] = includeCursor,
                    ["native-resolution"] = false
                }));

            int width = (int)metadata["width"].GetUInt32();
            int height = (int)metadata["height"].GetUInt32();
            int stride = (int)metadata["stride"].GetUInt32();
            uint format = metadata["format"].GetUInt32();
            int rowBytes = outputSize.Width * 4;

            // Fast path: same size, BGRA layout (screen content is opaque, so premultiplied == straight alpha).
            if (width == outputSize.Width && height == outputSize.Height && (format == 4 || format == 5 || format == 6))
            {
                if (stride == rowBytes && data.Length >= rowBytes * height)
                {
                    if (data.Length == rowBytes * height) return data;
                    return data.AsSpan(0, rowBytes * height).ToArray();
                }

                byte[] packed = new byte[rowBytes * height];
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(data, y * stride, packed, y * rowBytes, rowBytes);
                }
                return packed;
            }

            using SKBitmap source = DecodeKWinImage(metadata, data);
            using SKBitmap resized = source.Resize(new SKImageInfo(outputSize.Width, outputSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            byte[] result = new byte[rowBytes * outputSize.Height];
            using SKPixmap pixmap = resized.PeekPixels();
            for (int y = 0; y < outputSize.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixmap.GetPixels() + (y * pixmap.RowBytes), result, y * rowBytes, rowBytes);
            }
            return result;
        }

        private static SKBitmap DecodeKWinImage(Dictionary<string, VariantValue> metadata, byte[] data)
        {
            string type = metadata.TryGetValue("type", out VariantValue typeValue) ? typeValue.GetString() : "raw";
            if (type != "raw")
            {
                SKBitmap decoded = SKBitmap.Decode(data);
                return decoded ?? throw new InvalidDataException("Unsupported KWin screenshot type: " + type);
            }

            int width = (int)metadata["width"].GetUInt32();
            int height = (int)metadata["height"].GetUInt32();
            int stride = (int)metadata["stride"].GetUInt32();
            uint format = metadata["format"].GetUInt32();

            // QImage::Format values.
            (SKColorType colorType, SKAlphaType alphaType) = format switch
            {
                4 => (SKColorType.Bgra8888, SKAlphaType.Opaque),  // Format_RGB32
                5 => (SKColorType.Bgra8888, SKAlphaType.Unpremul), // Format_ARGB32
                6 => (SKColorType.Bgra8888, SKAlphaType.Premul),   // Format_ARGB32_Premultiplied
		16 => (SKColorType.Rgba8888, SKAlphaType.Opaque),   // Format_RGBX8888
		17 => (SKColorType.Rgba8888, SKAlphaType.Unpremul), // Format_RGBA8888
		18 => (SKColorType.Rgba8888, SKAlphaType.Premul),   // Format_RGBA8888_Premultiplied
                _ => throw new InvalidDataException("Unsupported KWin screenshot pixel format: " + format)
            };

            if (data.Length < (long)stride * height)
            {
                throw new InvalidDataException($"KWin screenshot data is truncated ({data.Length} bytes, expected {stride * height}).");
            }

            SKBitmap bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            unsafe
            {
                fixed (byte* source = data)
                {
                    SKImageInfo sourceInfo = new SKImageInfo(width, height, colorType, alphaType);
                    using SKPixmap sourcePixmap = new SKPixmap(sourceInfo, (IntPtr)source, stride);
                    using SKPixmap destinationPixmap = bitmap.PeekPixels();
                    if (!sourcePixmap.ReadPixels(destinationPixmap))
                    {
                        bitmap.Dispose();
                        throw new InvalidDataException("Unable to convert KWin screenshot pixels.");
                    }
                }
            }
            return bitmap;
        }

        private static async Task<SKBitmap> PortalCaptureAsync()
        {
            DBusConnection connection = await LinuxDBus.GetConnectionAsync().ConfigureAwait(false);
            string token = "sharex" + Guid.NewGuid().ToString("N");
            string sender = connection.UniqueName.TrimStart(':').Replace('.', '_');
            string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

            TaskCompletionSource<(uint Response, Dictionary<string, VariantValue> Results)> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using IDisposable subscription = await connection.WatchSignalAsync("org.freedesktop.portal.Desktop", requestPath,
                "org.freedesktop.portal.Request", "Response",
                (m, s) =>
                {
                    Reader reader = m.GetBodyReader();
                    uint code = reader.ReadUInt32();
                    Dictionary<string, VariantValue> results = reader.ReadDictionaryOfStringToVariantValue();
                    return (code, results);
                },
                (Exception e, (uint, Dictionary<string, VariantValue>) value) =>
                {
                    if (e != null) response.TrySetException(e);
                    else response.TrySetResult(value);
                }, null, false, ObserverFlags.None).ConfigureAwait(false);

            await LinuxDBus.CallAsync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Screenshot",
                "Screenshot", "sa{sv}",
                (ref MessageWriter w) =>
                {
                    w.WriteString("");
                    LinuxDBus.WriteOptions(ref w, new Dictionary<string, VariantValue>
                    {
                        ["handle_token"] = token,
                        ["interactive"] = false
                    });
                },
                (m, s) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);

            // The first portal screenshot may show a permission dialog, so allow plenty of time.
            Task finished = await Task.WhenAny(response.Task, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            if (finished != response.Task) throw new TimeoutException("xdg-desktop-portal did not respond.");

            (uint code, Dictionary<string, VariantValue> results) = await response.Task.ConfigureAwait(false);
            if (code != 0 || !results.TryGetValue("uri", out VariantValue uriValue))
            {
                DebugHelper.WriteLine("Portal screenshot was cancelled or denied (response " + code + ").");
                portalFailed = code == 2;
                return null;
            }

            string filePath = new Uri(uriValue.GetString()).LocalPath;
            try
            {
                return SKBitmap.Decode(filePath);
            }
            finally
            {
                // The portal stores the screenshot as a file; ShareX handles saving itself.
                try { File.Delete(filePath); } catch { }
            }
        }

        private static SKBitmap CommandLineCapture(bool includeCursor)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"sharex-capture-{Guid.NewGuid():N}.png");
            List<(string Tool, string[] Args)> candidates = new List<(string, string[])>
            {
                ("spectacle", includeCursor ? ["-b", "-n", "-f", "-p", "-o", tempFile] : ["-b", "-n", "-f", "-o", tempFile]),
                ("grim", includeCursor ? ["-c", tempFile] : [tempFile]),
                ("gnome-screenshot", includeCursor ? ["-p", "-f", tempFile] : ["-f", tempFile]),
                ("import", ["-window", "root", tempFile])
            };

            foreach ((string tool, string[] args) in candidates.Where(x => LinuxHelpers.HasExecutable(x.Tool)))
            {
                try
                {
                    var (exitCode, _, error) = LinuxHelpers.Run(tool, args, timeoutMs: 15000);
                    if (exitCode == 0 && File.Exists(tempFile))
                    {
                        return SKBitmap.Decode(tempFile);
                    }
                    DebugHelper.WriteLine($"{tool} failed ({exitCode}): {error}");
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }

            return null;
        }
    }
}
