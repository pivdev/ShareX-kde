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

using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Loads the user's pointer image from their Xcursor theme. Wayland doesn't let clients read the live cursor
    /// sprite, so cursor capture uses the theme's default arrow at the configured size.
    /// </summary>
    public static class XCursorLoader
    {
        private const uint ImageChunkType = 0xfffd0002;

        private static readonly string[] defaultCursorNames = { "default", "left_ptr", "arrow", "top_left_arrow" };
        private static (SKBitmap Bitmap, Point Hotspot)? cached;
        private static bool loadAttempted;

        /// <summary>Returns a copy of the default cursor image and its hotspot, or null when no theme is found.</summary>
        public static (SKBitmap Bitmap, Point Hotspot)? GetDefaultCursor()
        {
            if (!loadAttempted)
            {
                loadAttempted = true;
                try
                {
                    cached = Load(GetThemeName(), GetCursorSize());
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Unable to load Xcursor theme.");
                }
            }

            if (cached == null) return null;
            return (cached.Value.Bitmap.Copy(), cached.Value.Hotspot);
        }

        private static string GetThemeName()
        {
            string theme = Environment.GetEnvironmentVariable("XCURSOR_THEME");
            if (string.IsNullOrEmpty(theme)) theme = ReadKdeSetting("kcminputrc", "Mouse", "cursorTheme");
            if (string.IsNullOrEmpty(theme)) theme = ReadGtkCursorTheme();
            return string.IsNullOrEmpty(theme) ? "default" : theme;
        }

        private static int GetCursorSize()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("XCURSOR_SIZE"), out int size) && size > 0) return size;
            if (int.TryParse(ReadKdeSetting("kcminputrc", "Mouse", "cursorSize"), out size) && size > 0) return size;
            return 24;
        }

        private static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } dir ? dir :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        private static string ReadKdeSetting(string file, string group, string key) => ReadIni(Path.Combine(ConfigHome, file), group, key);

        private static string ReadGtkCursorTheme() => ReadIni(Path.Combine(ConfigHome, "gtk-3.0", "settings.ini"), "Settings", "gtk-cursor-theme-name");

        private static string ReadIni(string path, string group, string key)
        {
            if (!File.Exists(path)) return null;

            string currentGroup = null;
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentGroup = line[1..^1];
                }
                else if (currentGroup == group)
                {
                    int index = line.IndexOf('=');
                    if (index > 0 && line[..index].Trim() == key)
                    {
                        return line[(index + 1)..].Trim();
                    }
                }
            }
            return null;
        }

        private static IEnumerable<string> GetIconDirectories()
        {
            string xcursorPath = Environment.GetEnvironmentVariable("XCURSOR_PATH");
            if (!string.IsNullOrEmpty(xcursorPath))
            {
                foreach (string dir in xcursorPath.Split(':', StringSplitOptions.RemoveEmptyEntries)) yield return dir;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(home, ".local", "share");
            yield return Path.Combine(dataHome, "icons");
            yield return Path.Combine(home, ".icons");

            string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dd ? dd : "/usr/local/share:/usr/share";
            foreach (string dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)) yield return Path.Combine(dir, "icons");
            yield return "/usr/share/pixmaps";
        }

        private static (SKBitmap, Point)? Load(string theme, int size)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Queue<string> themes = new Queue<string>();
            themes.Enqueue(theme);
            themes.Enqueue("default");
            themes.Enqueue("Adwaita");
            themes.Enqueue("breeze_cursors");

            while (themes.Count > 0)
            {
                string current = themes.Dequeue();
                if (!visited.Add(current)) continue;

                foreach (string iconDir in GetIconDirectories())
                {
                    string themeDir = Path.Combine(iconDir, current);
                    foreach (string name in defaultCursorNames)
                    {
                        string file = Path.Combine(themeDir, "cursors", name);
                        if (File.Exists(file))
                        {
                            (SKBitmap, Point)? result = ParseXCursor(File.ReadAllBytes(file), size);
                            if (result != null) return result;
                        }
                    }

                    string inherits = ReadIni(Path.Combine(themeDir, "index.theme"), "Icon Theme", "Inherits");
                    if (!string.IsNullOrEmpty(inherits))
                    {
                        foreach (string parent in inherits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            themes.Enqueue(parent);
                        }
                    }
                }
            }

            return null;
        }

        private static (SKBitmap, Point)? ParseXCursor(byte[] data, int requestedSize)
        {
            if (data.Length < 16 || data[0] != 'X' || data[1] != 'c' || data[2] != 'u' || data[3] != 'r') return null;

            uint ReadUInt32(int offset) => BitConverter.ToUInt32(data, offset);

            uint headerSize = ReadUInt32(4);
            uint tocCount = ReadUInt32(12);
            List<(uint Size, uint Position)> images = new List<(uint, uint)>();

            for (int i = 0; i < tocCount; i++)
            {
                int entry = (int)headerSize + (i * 12);
                if (entry + 12 > data.Length) break;
                if (ReadUInt32(entry) == ImageChunkType)
                {
                    images.Add((ReadUInt32(entry + 4), ReadUInt32(entry + 8)));
                }
            }

            if (images.Count == 0) return null;

            uint bestSize = images.Select(x => x.Size).OrderBy(x => Math.Abs((int)x - requestedSize)).First();
            uint position = images.First(x => x.Size == bestSize).Position; // First frame of animated cursors.

            int p = (int)position;
            int width = (int)ReadUInt32(p + 16);
            int height = (int)ReadUInt32(p + 20);
            int xHot = (int)ReadUInt32(p + 24);
            int yHot = (int)ReadUInt32(p + 28);
            int pixels = p + 36;
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024 || pixels + (width * height * 4) > data.Length) return null;

            // Xcursor pixels are premultiplied ARGB in native (little-endian) order, i.e. BGRA bytes.
            SKBitmap bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(data, pixels, bitmap.GetPixels(), width * height * 4);

            if (bestSize != requestedSize && bestSize > 0)
            {
                float scale = requestedSize / (float)bestSize;
                SKBitmap scaled = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)),
                    SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
                bitmap.Dispose();
                return (scaled, new Point((int)Math.Round(xHot * scale), (int)Math.Round(yHot * scale)));
            }

            return (bitmap, new Point(xHot, yHot));
        }
    }
}
