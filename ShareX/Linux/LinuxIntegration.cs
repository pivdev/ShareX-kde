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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ShareX
{
    /// <summary>
    /// Freedesktop/KDE equivalents of the Windows shell integration in <see cref="IntegrationHelpers"/>:
    /// XDG autostart, shared-mime-info file types, Dolphin service menus and browser native messaging manifests.
    /// </summary>
    internal static class LinuxIntegration
    {
        /// <summary>Desktop file id written by linux/install.sh (~/.local/share/applications/sharex.desktop).</summary>
        public const string DesktopFileId = "sharex.desktop";

        public const string IconName = "sharex";

        public const string CustomUploaderMimeType = "application/x-sharex-custom-uploader";
        public const string ImageEffectMimeType = "application/x-sharex-image-effect";

        private const string ChromeHostName = "com.getsharex.sharex";
        private const string FirefoxHostName = "ShareX";

        private static readonly UnixFileMode ExecutableFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        private static string ExecutablePath => LinuxShell.GetApplicationExecutablePath();

        private static string AutostartFilePath => Path.Combine(LinuxShell.XdgConfigHome, "autostart", DesktopFileId);

        private static string MimeFolder => Path.Combine(LinuxShell.XdgDataHome, "mime");

        private static string MimePackageFilePath => Path.Combine(MimeFolder, "packages", "sharex.xml");

        private static string ServiceMenuFolder => Path.Combine(LinuxShell.XdgDataHome, "kio", "servicemenus");

        private static string UploadServiceMenuFilePath => Path.Combine(ServiceMenuFolder, "sharex-upload.desktop");

        private static string EditServiceMenuFilePath => Path.Combine(ServiceMenuFolder, "sharex-edit.desktop");

        #region Desktop entry helpers

        /// <summary>Quotes an argument for a desktop entry Exec key (Desktop Entry Specification, "The Exec key").</summary>
        public static string QuoteExecArgument(string argument)
        {
            const string reserved = " \t\n\"'\\><~|&;$*?#()`";

            string value = argument;

            if (value.IndexOfAny(reserved.ToCharArray()) >= 0)
            {
                StringBuilder sb = new StringBuilder("\"");

                foreach (char c in value)
                {
                    if (c == '"' || c == '`' || c == '$' || c == '\\')
                    {
                        sb.Append('\\');
                    }

                    sb.Append(c);
                }

                sb.Append('"');
                value = sb.ToString();
            }

            // The Exec value is a "string" value: backslashes need another level of escaping, and literal % is %%.
            return value.Replace("\\", "\\\\").Replace("%", "%%");
        }

        private static string BuildExec(params string[] arguments)
        {
            return string.Join(" ", new[] { QuoteExecArgument(ExecutablePath) }.Concat(arguments));
        }

        private static Dictionary<string, string> ReadDesktopEntryGroup(string filePath, string group = "Desktop Entry")
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

            if (!File.Exists(filePath))
            {
                return values;
            }

            bool inGroup = false;

            foreach (string rawLine in File.ReadAllLines(filePath))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inGroup = line[1..^1] == group;
                    continue;
                }

                int index = line.IndexOf('=');

                if (inGroup && index > 0)
                {
                    values[line[..index].Trim()] = line[(index + 1)..].Trim();
                }
            }

            return values;
        }

        private static bool ExecMatchesCurrentExecutable(string exec)
        {
            return !string.IsNullOrEmpty(exec) && exec.StartsWith(QuoteExecArgument(ExecutablePath) + " ", StringComparison.Ordinal);
        }

        private static void WriteTextFile(string filePath, string content, bool executable = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            // Raw string literals carry the source file's line endings; desktop entries and XML are written with LF.
            File.WriteAllText(filePath, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));

            if (executable)
            {
                File.SetUnixFileMode(filePath, ExecutableFileMode);
            }
        }

        private static void DeleteFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        private static void RunTool(string fileName, params string[] args)
        {
            if (!LinuxHelpers.HasExecutable(fileName))
            {
                DebugHelper.WriteLine($"{fileName} not found, skipping.");
                return;
            }

            try
            {
                (int exitCode, _, string error) = LinuxHelpers.Run(fileName, args, timeoutMs: 60000);

                if (exitCode != 0)
                {
                    DebugHelper.WriteLine($"{fileName} failed ({exitCode}): {error}");
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static string FindInstalledDesktopFile()
        {
            List<string> dataDirs = new List<string> { LinuxShell.XdgDataHome };
            string xdgDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
            dataDirs.AddRange(string.IsNullOrEmpty(xdgDataDirs) ? new[] { "/usr/local/share", "/usr/share" } :
                xdgDataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries));

            return dataDirs.Select(x => Path.Combine(x, "applications", DesktopFileId)).FirstOrDefault(File.Exists);
        }

        #endregion Desktop entry helpers

        #region Autostart

        public static StartupState GetStartupState()
        {
            Dictionary<string, string> entry = ReadDesktopEntryGroup(AutostartFilePath);

            if (entry.Count == 0 || !entry.TryGetValue("Exec", out string exec) || !ExecMatchesCurrentExecutable(exec))
            {
                return StartupState.Disabled;
            }

            if ((entry.TryGetValue("Hidden", out string hidden) && hidden.Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                (entry.TryGetValue("X-KDE-autostart-enabled", out string enabled) && enabled.Equals("false", StringComparison.OrdinalIgnoreCase)) ||
                (entry.TryGetValue("X-GNOME-Autostart-enabled", out string gnomeEnabled) && gnomeEnabled.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                return StartupState.Disabled;
            }

            return StartupState.Enabled;
        }

        public static void SetStartup(bool enable)
        {
            if (enable)
            {
                string content = $"""
                    [Desktop Entry]
                    Type=Application
                    Name=ShareX
                    Comment=Screen capture, file sharing and productivity tool
                    Icon={IconName}
                    Exec={BuildExec("-silent")}
                    Terminal=false
                    X-GNOME-Autostart-enabled=true

                    """;

                WriteTextFile(AutostartFilePath, content);
                DebugHelper.WriteLine("Autostart entry created: " + AutostartFilePath);
            }
            else
            {
                DeleteFile(AutostartFilePath);
            }
        }

        #endregion Autostart

        #region File types (shared-mime-info)

        private static string MimePackageContent => $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
              <mime-type type="{CustomUploaderMimeType}">
                <comment>ShareX custom uploader</comment>
                <sub-class-of type="application/json"/>
                <icon name="{IconName}"/>
                <generic-icon name="{IconName}"/>
                <glob pattern="*.sxcu"/>
              </mime-type>
              <mime-type type="{ImageEffectMimeType}">
                <comment>ShareX image effect</comment>
                <sub-class-of type="application/zip"/>
                <icon name="{IconName}"/>
                <generic-icon name="{IconName}"/>
                <glob pattern="*.sxie"/>
              </mime-type>
            </mime-info>

            """.ReplaceLineEndings("\n");

        public static bool CheckFileTypes()
        {
            try
            {
                return File.Exists(MimePackageFilePath) && File.ReadAllText(MimePackageFilePath) == MimePackageContent;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return false;
            }
        }

        /// <summary>Registers the .sxcu and .sxie MIME types (one package for both) and makes ShareX their default handler.</summary>
        public static void RegisterFileTypes()
        {
            if (!CheckFileTypes())
            {
                WriteTextFile(MimePackageFilePath, MimePackageContent);
                RunTool("update-mime-database", MimeFolder);
                DebugHelper.WriteLine("MIME types registered: " + MimePackageFilePath);
            }

            // The handler is the desktop entry installed by linux/install.sh (it lists both types in MimeType=).
            string desktopFile = FindInstalledDesktopFile();

            if (desktopFile != null)
            {
                RunTool("xdg-mime", "default", DesktopFileId, CustomUploaderMimeType, ImageEffectMimeType);
            }
        }

        public static void UnregisterFileTypes()
        {
            if (File.Exists(MimePackageFilePath))
            {
                File.Delete(MimePackageFilePath);
                RunTool("update-mime-database", MimeFolder);
            }
        }

        #endregion File types (shared-mime-info)

        #region Dolphin service menus

        private static string UploadServiceMenuContent => $"""
            [Desktop Entry]
            Type=Service
            MimeType=all/all;
            Actions=uploadWithShareX;
            X-KDE-Priority=TopLevel

            [Desktop Action uploadWithShareX]
            Name={Strings.IntegrationHelpers_UploadWithShareX}
            Icon={IconName}
            Exec={BuildExec("%F")}

            """;

        private static string EditServiceMenuContent => $"""
            [Desktop Entry]
            Type=Service
            MimeType=image/png;image/jpeg;image/bmp;image/gif;image/webp;image/tiff;image/x-icon;image/vnd.microsoft.icon;image/avif;image/heif;image/jxl;
            Actions=editWithShareX;
            X-KDE-Priority=TopLevel

            [Desktop Action editWithShareX]
            Name={Strings.IntegrationHelpers_EditWithShareX}
            Icon={IconName}
            Exec={BuildExec("-ImageEditor", "%f")}

            """;

        private static bool CheckServiceMenu(string filePath, string actionName)
        {
            try
            {
                Dictionary<string, string> action = ReadDesktopEntryGroup(filePath, "Desktop Action " + actionName);
                return action.TryGetValue("Exec", out string exec) && ExecMatchesCurrentExecutable(exec);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return false;
            }
        }

        public static bool CheckUploadServiceMenu() => CheckServiceMenu(UploadServiceMenuFilePath, "uploadWithShareX");

        public static bool CheckEditServiceMenu() => CheckServiceMenu(EditServiceMenuFilePath, "editWithShareX");

        public static void SetUploadServiceMenu(bool create)
        {
            if (create)
            {
                // KIO only loads service menus from user folders when they are executable.
                WriteTextFile(UploadServiceMenuFilePath, UploadServiceMenuContent, true);
            }
            else
            {
                DeleteFile(UploadServiceMenuFilePath);
            }
        }

        public static void SetEditServiceMenu(bool create)
        {
            if (create)
            {
                WriteTextFile(EditServiceMenuFilePath, EditServiceMenuContent, true);
            }
            else
            {
                DeleteFile(EditServiceMenuFilePath);
            }
        }

        #endregion Dolphin service menus

        #region Browser native messaging

        private static string NativeMessagingHostPath => Path.Combine(AppContext.BaseDirectory, "ShareX_NativeMessagingHost");

        private static IEnumerable<string> ChromeManifestFolders
        {
            get
            {
                string config = LinuxShell.XdgConfigHome;

                string[] browserFolders =
                {
                    "google-chrome", "google-chrome-beta", "google-chrome-unstable", "chromium",
                    Path.Combine("BraveSoftware", "Brave-Browser"), "microsoft-edge", "microsoft-edge-beta", "microsoft-edge-dev", "vivaldi", "opera"
                };

                List<string> folders = browserFolders.Select(x => Path.Combine(config, x)).Where(Directory.Exists).ToList();

                if (folders.Count == 0)
                {
                    // No Chromium based browser profile yet; register for Chrome and Chromium so it works once one is installed.
                    folders.Add(Path.Combine(config, "google-chrome"));
                    folders.Add(Path.Combine(config, "chromium"));
                }

                return folders.Select(x => Path.Combine(x, "NativeMessagingHosts"));
            }
        }

        private static IEnumerable<string> FirefoxManifestFolders
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                yield return Path.Combine(home, ".mozilla", "native-messaging-hosts");

                // Firefox with XDG base directory support and LibreWolf.
                string xdgMozilla = Path.Combine(LinuxShell.XdgConfigHome, "mozilla");
                if (Directory.Exists(xdgMozilla)) yield return Path.Combine(xdgMozilla, "native-messaging-hosts");

                string libreWolf = Path.Combine(home, ".librewolf");
                if (Directory.Exists(libreWolf)) yield return Path.Combine(libreWolf, "native-messaging-hosts");
            }
        }

        private static string CreateHostManifest(string templateFilePath, string hostName)
        {
            JObject manifest = JObject.Parse(File.ReadAllText(templateFilePath));
            manifest["name"] = hostName;
            // Linux browsers require an absolute path to the host executable.
            manifest["path"] = NativeMessagingHostPath;
            return manifest.ToString(Formatting.Indented);
        }

        private static bool CheckHostManifests(IEnumerable<string> folders, string hostName)
        {
            try
            {
                return folders.Select(x => Path.Combine(x, hostName + ".json")).Where(File.Exists).Any(x =>
                    (string)JObject.Parse(File.ReadAllText(x))["path"] == NativeMessagingHostPath) && File.Exists(NativeMessagingHostPath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return false;
            }
        }

        private static void SetHostManifests(bool create, IEnumerable<string> folders, string templateFilePath, string hostName)
        {
            string content = create ? CreateHostManifest(templateFilePath, hostName) : null;

            foreach (string folder in folders)
            {
                string filePath = Path.Combine(folder, hostName + ".json");

                if (create)
                {
                    WriteTextFile(filePath, content);
                    DebugHelper.WriteLine("Native messaging host manifest created: " + filePath);
                }
                else
                {
                    DeleteFile(filePath);
                }
            }
        }

        public static bool CheckChromeNativeMessaging() => CheckHostManifests(ChromeManifestFolders, ChromeHostName);

        public static void SetChromeNativeMessaging(bool create, string templateFilePath) =>
            SetHostManifests(create, ChromeManifestFolders, templateFilePath, ChromeHostName);

        public static bool CheckFirefoxNativeMessaging() => CheckHostManifests(FirefoxManifestFolders, FirefoxHostName);

        public static void SetFirefoxNativeMessaging(bool create, string templateFilePath) =>
            SetHostManifests(create, FirefoxManifestFolders, templateFilePath, FirefoxHostName);

        #endregion Browser native messaging
    }
}

#endif
