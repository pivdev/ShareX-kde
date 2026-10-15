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
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    /// <summary>Freedesktop (XDG) desktop integration helpers: base directories, opening files and showing files in the file manager.</summary>
    public static class LinuxShell
    {
        // Own session bus connection (the shared DBusConnection.Session only connects on a method call, so signals sent on it before that are dropped).
        private static readonly Lazy<Task<DBusConnection>> sessionBus = new Lazy<Task<DBusConnection>>(async () =>
        {
            DBusConnection connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("D-Bus session bus address is unknown."));
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        });

        private static string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string XdgConfigHome => GetXdgDirectory("XDG_CONFIG_HOME", ".config");

        public static string XdgDataHome => GetXdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share"));

        /// <summary>$XDG_RUNTIME_DIR (per user, tmpfs, mode 0700). Falls back to the temp folder.</summary>
        public static string XdgRuntimeDir
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : Path.GetTempPath();
            }
        }

        private static string GetXdgDirectory(string variable, string fallback)
        {
            string dir = Environment.GetEnvironmentVariable(variable);
            return !string.IsNullOrEmpty(dir) && Path.IsPathRooted(dir) ? dir : Path.Combine(HomeFolder, fallback);
        }

        /// <summary>
        /// Path of the native ShareX executable (apphost). When the app was started with "dotnet ShareX.dll"
        /// Environment.ProcessPath points to the dotnet host, so the apphost next to the entry assembly is used instead.
        /// </summary>
        public static string GetApplicationExecutablePath(string appHostName = "ShareX")
        {
            string processPath = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(processPath) && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.Ordinal))
            {
                return processPath;
            }

            string appHost = Path.Combine(AppContext.BaseDirectory, appHostName);
            return File.Exists(appHost) ? appHost : processPath;
        }

        /// <summary>Converts a local path to a file:// URI with percent-encoded characters, as used by text/uri-list and D-Bus file manager APIs.</summary>
        public static string ToFileUri(string path)
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }

        /// <summary>Operating system name from os-release(5), e.g. "CachyOS Linux". Returns null when unavailable.</summary>
        public static string GetOSPrettyName()
        {
            foreach (string path in new string[] { "/etc/os-release", "/usr/lib/os-release" })
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    Dictionary<string, string> values = new Dictionary<string, string>();

                    foreach (string line in File.ReadAllLines(path))
                    {
                        int index = line.IndexOf('=');
                        if (index <= 0) continue;
                        string value = line.Substring(index + 1).Trim();
                        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                        {
                            value = value.Substring(1, value.Length - 2);
                        }
                        values[line.Substring(0, index).Trim()] = value;
                    }

                    if (values.TryGetValue("PRETTY_NAME", out string prettyName) && !string.IsNullOrEmpty(prettyName)) return prettyName;
                    if (values.TryGetValue("NAME", out string name) && !string.IsNullOrEmpty(name)) return name;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }

            return null;
        }

        /// <summary>Opens a file, folder or URL with the desktop's default handler (xdg-open). Never executes the target itself.</summary>
        public static bool Open(string target)
        {
            return LinuxHelpers.OpenWithDefault(target);
        }

        /// <summary>
        /// Shell thumbnail equivalent: reads the freedesktop thumbnail cache (~/.cache/thumbnails, filled by Dolphin/KIO,
        /// Nautilus, ...) and, for videos, falls back to ffmpegthumbnailer when installed. Returns null when unavailable.
        /// </summary>
        public static SKBitmap GetFileThumbnail(string filePath, int width, int height)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(filePath);
            string cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(cacheHome)) cacheHome = Path.Combine(HomeFolder, ".cache");
            string thumbnailsFolder = Path.Combine(cacheHome, "thumbnails");

            string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(ToFileUri(fullPath))));
            int requested = Math.Max(width, height);
            DateTime lastWrite = File.GetLastWriteTimeUtc(fullPath);

            (string Folder, int Size)[] sizes = { ("normal", 128), ("large", 256), ("x-large", 512), ("xx-large", 1024) };

            // Prefer the smallest cached thumbnail that is at least as large as requested, then any larger one.
            foreach ((string folder, _) in sizes.OrderBy(x => x.Size < requested ? 1 : 0).ThenBy(x => x.Size < requested ? -x.Size : x.Size))
            {
                string thumbnailPath = Path.Combine(thumbnailsFolder, folder, hash + ".png");

                try
                {
                    if (File.Exists(thumbnailPath) && File.GetLastWriteTimeUtc(thumbnailPath) >= lastWrite)
                    {
                        SKBitmap bitmap = SKBitmap.Decode(thumbnailPath);
                        if (bitmap != null) return bitmap;
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }

            if (FileHelpers.IsVideoFile(fullPath) && LinuxHelpers.HasExecutable("ffmpegthumbnailer"))
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"sharex-thumbnail-{Guid.NewGuid():N}.png");

                try
                {
                    (int exitCode, _, _) = LinuxHelpers.Run("ffmpegthumbnailer",
                        new string[] { "-i", fullPath, "-o", tempPath, "-s", Math.Max(requested, 32).ToString(), "-c", "png" }, timeoutMs: 15000);

                    if (exitCode == 0 && File.Exists(tempPath))
                    {
                        return SKBitmap.Decode(tempPath);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            return null;
        }

        /// <summary>Moves a file to the desktop trash (freedesktop trash spec) using gio, or KIO as a fallback.</summary>
        public static bool MoveToTrash(string path)
        {
            string fullPath = Path.GetFullPath(path);

            (string FileName, string[] Args)[] commands =
            {
                ("gio", new string[] { "trash", "--", fullPath }),
                ("kioclient6", new string[] { "move", fullPath, "trash:/" }),
                ("kioclient", new string[] { "move", fullPath, "trash:/" })
            };

            foreach ((string fileName, string[] args) in commands)
            {
                if (!LinuxHelpers.HasExecutable(fileName)) continue;

                try
                {
                    (int exitCode, _, string error) = LinuxHelpers.Run(fileName, args);

                    if (exitCode == 0 && !File.Exists(fullPath))
                    {
                        return true;
                    }

                    DebugHelper.WriteLine($"{fileName} failed to move file to trash: {error}");
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }

            return false;
        }

        /// <summary>
        /// Opens the containing folder in the file manager and selects the file, using org.freedesktop.FileManager1.ShowItems
        /// (implemented by Dolphin, Nautilus, Nemo, ...). Falls back to opening the parent folder with xdg-open.
        /// </summary>
        public static void ShowInFolder(string path)
        {
            string fullPath = Path.GetFullPath(path);

            Task.Run(async () =>
            {
                try
                {
                    await CallFileManagerShowItemsAsync(ToFileUri(fullPath)).ConfigureAwait(false);
                    return;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteLine("FileManager1.ShowItems failed, falling back to xdg-open: " + e.Message);
                }

                string folder = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                Open(folder);
            });
        }

        /// <summary>
        /// Taskbar progress through the Unity LauncherEntry D-Bus API, which the Plasma task manager implements.
        /// The entry is matched by desktop file id, so this needs the sharex.desktop file installed by linux/install.sh.
        /// </summary>
        /// <param name="progress">Progress between 0 and 1, or null to keep the last value.</param>
        /// <param name="visible">Whether the progress bar is shown.</param>
        public static void SetLauncherProgress(double? progress, bool visible, string desktopFileId = "sharex.desktop")
        {
            Task<DBusConnection> connectionTask = sessionBus.Value;

            if (connectionTask.IsCompletedSuccessfully)
            {
                SendLauncherEntryUpdate(connectionTask.Result, progress, visible, desktopFileId);
            }
            else if (!connectionTask.IsCompleted)
            {
                connectionTask.ContinueWith(x => SendLauncherEntryUpdate(x.Result, progress, visible, desktopFileId), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        private static void SendLauncherEntryUpdate(DBusConnection connection, double? progress, bool visible, string desktopFileId)
        {
            try
            {
                using (MessageWriter writer = connection.GetMessageWriter())
                {
                    writer.WriteSignalHeader(null, "/com/getsharex/ShareX/LauncherEntry", "com.canonical.Unity.LauncherEntry", "Update", "sa{sv}");
                    writer.WriteString("application://" + desktopFileId);

                    ArrayStart dictionary = writer.WriteDictionaryStart();

                    if (progress.HasValue)
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString("progress");
                        writer.WriteVariantDouble(Math.Clamp(progress.Value, 0, 1));
                    }

                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("progress-visible");
                    writer.WriteVariantBool(visible);

                    writer.WriteDictionaryEnd(dictionary);

                    connection.TrySendMessage(writer.CreateMessage());
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static async Task CallFileManagerShowItemsAsync(string uri)
        {
            DBusConnection connection = await sessionBus.Value.ConfigureAwait(false);

            MessageBuffer message;

            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: "org.freedesktop.FileManager1", path: "/org/freedesktop/FileManager1",
                    @interface: "org.freedesktop.FileManager1", member: "ShowItems", signature: "ass");
                writer.WriteArray(new string[] { uri });
                writer.WriteString("");
                message = writer.CreateMessage();
            }

            Task call = connection.CallMethodAsync(message);
            Task completed = await Task.WhenAny(call, Task.Delay(10000)).ConfigureAwait(false);

            if (completed != call)
            {
                throw new TimeoutException("FileManager1.ShowItems timed out.");
            }

            await call.ConfigureAwait(false);
        }
    }
}

#endif
