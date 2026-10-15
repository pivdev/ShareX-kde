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
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    /// <summary>Shared session bus connection for ShareX's Linux integrations (separate from Avalonia's).</summary>
    public static class LinuxDBus
    {
        // MessageWriter is a mutable struct; it must be passed by reference.
        public delegate void BodyWriter(ref MessageWriter writer);

        private static readonly SemaphoreSlim connectLock = new SemaphoreSlim(1, 1);
        private static DBusConnection connection;

        public static async Task<DBusConnection> GetConnectionAsync()
        {
            if (connection != null) return connection;

            await connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (connection == null)
                {
                    string address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
                    if (string.IsNullOrEmpty(address))
                    {
                        address = $"unix:path=/run/user/{geteuid()}/bus";
                    }

                    DBusConnection newConnection = new DBusConnection(address);
                    await newConnection.ConnectAsync().ConfigureAwait(false);
                    connection = newConnection;
                }
                return connection;
            }
            finally
            {
                connectLock.Release();
            }
        }

        public static async Task<string> GetUniqueNameAsync() => (await GetConnectionAsync().ConfigureAwait(false)).UniqueName;

        public static async Task<T> CallAsync<T>(string destination, string path, string iface, string member, string signature,
            BodyWriter body, MessageValueReader<T> reader)
        {
            DBusConnection conn = await GetConnectionAsync().ConfigureAwait(false);
            MessageWriter writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(destination, path, iface, member, signature, MessageFlags.None);
            body?.Invoke(ref writer);
            return await conn.CallMethodAsync(writer.CreateMessage(), reader, null).ConfigureAwait(false);
        }

        public static async Task CallAsync(string destination, string path, string iface, string member, string signature = null,
            BodyWriter body = null)
        {
            DBusConnection conn = await GetConnectionAsync().ConfigureAwait(false);
            MessageWriter writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(destination, path, iface, member, signature, MessageFlags.None);
            body?.Invoke(ref writer);
            await conn.CallMethodAsync(writer.CreateMessage()).ConfigureAwait(false);
        }

        public static async Task<bool> NameHasOwnerAsync(string name)
        {
            try
            {
                return await CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s",
                    (ref MessageWriter w) => w.WriteString(name), (m, s) => m.GetBodyReader().ReadBool()).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return false;
            }
        }

        public static void WriteOptions(ref MessageWriter writer, IEnumerable<KeyValuePair<string, VariantValue>> options)
        {
            ArrayStart start = writer.WriteDictionaryStart();
            foreach (KeyValuePair<string, VariantValue> option in options)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(option.Key);
                writer.WriteVariant(option.Value);
            }
            writer.WriteDictionaryEnd(start);
        }

        public static async Task AddMethodHandlerAsync(IPathMethodHandler handler)
        {
            DBusConnection conn = await GetConnectionAsync().ConfigureAwait(false);
            conn.AddMethodHandler(handler);
        }

        /// <summary>Creates an anonymous pipe; the write end is meant to be passed to another process over D-Bus.</summary>
        public static (FileStream Read, SafeFileHandle Write) CreatePipe()
        {
            int[] fds = new int[2];
            if (pipe2(fds, O_CLOEXEC) != 0)
            {
                throw new IOException("pipe2 failed: " + Marshal.GetLastPInvokeError());
            }

            SafeFileHandle readHandle = new SafeFileHandle(fds[0], true);
            SafeFileHandle writeHandle = new SafeFileHandle(fds[1], true);
            return (new FileStream(readHandle, FileAccess.Read, 1 << 16), writeHandle);
        }

        /// <summary>Runs async D-Bus work from synchronous call sites without capturing the UI context.</summary>
        public static T RunSync<T>(Func<Task<T>> func) => Task.Run(func).GetAwaiter().GetResult();

        private const int O_CLOEXEC = 0x80000;

        [DllImport("libc", SetLastError = true)]
        private static extern int pipe2(int[] fds, int flags);

        [DllImport("libc")]
        private static extern uint geteuid();
    }
}
