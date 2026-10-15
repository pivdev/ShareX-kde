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
using System.Linq;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    /// <summary>Minimal client for KDE's global shortcut daemon (org.kde.kglobalaccel) on the session bus.</summary>
    internal sealed class KGlobalAccel
    {
        public const string ServiceName = "org.kde.kglobalaccel";
        private const string ObjectPath = "/kglobalaccel";
        private const string Interface = "org.kde.KGlobalAccel";
        private const string ComponentInterface = "org.kde.kglobalaccel.Component";

        [Flags]
        public enum SetShortcutFlags : uint
        {
            None = 0,
            IsDefault = 1,
            SetPresent = 2,
            NoAutoloading = 4
        }

        private readonly DBusConnection connection;

        public string ComponentUnique { get; }
        public string ComponentFriendly { get; }

        public KGlobalAccel(DBusConnection connection, string componentUnique, string componentFriendly)
        {
            this.connection = connection;
            ComponentUnique = componentUnique;
            ComponentFriendly = componentFriendly;
        }

        public string[] GetActionId(string actionUnique, string actionFriendly)
        {
            return new string[] { ComponentUnique, actionUnique, ComponentFriendly, actionFriendly ?? "" };
        }

        public async Task<bool> IsAvailableAsync()
        {
            string[] services = await connection.ListServicesAsync().ConfigureAwait(false);
            return services.Contains(ServiceName);
        }

        public Task DoRegisterAsync(string[] actionId)
        {
            return CallAsync("doRegister", actionId);
        }

        public Task UnRegisterAsync(string[] actionId)
        {
            return CallAsync("unRegister", actionId);
        }

        public Task SetInactiveAsync(string[] actionId)
        {
            return CallAsync("setInactive", actionId);
        }

        public Task<int[]> SetShortcutAsync(string[] actionId, int[] keys, SetShortcutFlags flags)
        {
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: ServiceName, path: ObjectPath, @interface: Interface, member: "setShortcut", signature: "asaiu");
                writer.WriteArray(actionId);
                writer.WriteArray(keys);
                writer.WriteUInt32((uint)flags);
                message = writer.CreateMessage();
            }
            return connection.CallMethodAsync(message, (Message reply, object state) => reply.GetBodyReader().ReadArrayOfInt32(), null);
        }

        /// <summary>Returns false if any action (including one of this component's own) already uses the key.</summary>
        public Task<bool> IsShortcutAvailableAsync(int key)
        {
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: ServiceName, path: ObjectPath, @interface: Interface, member: "isGlobalShortcutAvailable", signature: "is");
                writer.WriteInt32(key);
                writer.WriteString(ComponentUnique);
                message = writer.CreateMessage();
            }
            return connection.CallMethodAsync(message, (Message reply, object state) => reply.GetBodyReader().ReadBool(), null);
        }

        /// <summary>Returns the action IDs (component, action, component friendly, action friendly) registered for this component.</summary>
        public Task<string[][]> GetAllActionsAsync()
        {
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: ServiceName, path: ObjectPath, @interface: Interface, member: "allActionsForComponent", signature: "as");
                writer.WriteArray(new string[] { ComponentUnique });
                message = writer.CreateMessage();
            }
            return connection.CallMethodAsync(message, (Message reply, object state) =>
            {
                Reader reader = reply.GetBodyReader();
                System.Collections.Generic.List<string[]> actions = new System.Collections.Generic.List<string[]>();
                ArrayEnd arrayEnd = reader.ReadArrayStart(DBusType.Array);
                while (reader.HasNext(arrayEnd))
                {
                    actions.Add(reader.ReadArrayOfString());
                }
                return actions.ToArray();
            }, null);
        }

        public Task<string> GetComponentPathAsync()
        {
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: ServiceName, path: ObjectPath, @interface: Interface, member: "getComponent", signature: "s");
                writer.WriteString(ComponentUnique);
                message = writer.CreateMessage();
            }
            return connection.CallMethodAsync(message, (Message reply, object state) => reply.GetBodyReader().ReadObjectPathAsString(), null);
        }

        /// <summary>Watches globalShortcutPressed on the component object. The handler receives the action unique name and runs on a D-Bus thread.</summary>
        public async Task<IDisposable> WatchShortcutPressedAsync(string componentPath, Action<string> handler)
        {
            return await connection.WatchSignalAsync(null, componentPath, ComponentInterface, "globalShortcutPressed",
                (Message message, object state) =>
                {
                    Reader reader = message.GetBodyReader();
                    string component = reader.ReadString();
                    string action = reader.ReadString();
                    return (component, action);
                },
                (Exception exception, (string component, string action) value) =>
                {
                    if (exception != null)
                    {
                        DebugHelper.WriteLine("KGlobalAccel signal watch failed: " + exception.Message);
                    }
                    else if (value.component == ComponentUnique)
                    {
                        handler(value.action);
                    }
                }, null, false, ObserverFlags.None).ConfigureAwait(false);
        }

        private Task CallAsync(string member, string[] actionId)
        {
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: ServiceName, path: ObjectPath, @interface: Interface, member: member, signature: "as");
                writer.WriteArray(actionId);
                message = writer.CreateMessage();
            }
            return connection.CallMethodAsync(message);
        }
    }
}

#endif
