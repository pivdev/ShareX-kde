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

using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Linux global hotkeys backed by KDE's KGlobalAccel over D-Bus. Hotkeys appear in KDE System Settings
    /// under the "ShareX" component while ShareX is running; ShareX's own hotkey config stays the source of truth.
    /// </summary>
    public sealed class LinuxHotkeyHost : IHotkeyHost
    {
        private const string ComponentUnique = "sharex";
        private const string ComponentFriendly = "ShareX";
        private const string ActionPrefix = "hotkey_";
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

        private sealed class Registration
        {
            public HotkeyInfo HotkeyInfo;
            public string[] ActionId;
        }

        private readonly int threadId = Environment.CurrentManagedThreadId;
        private readonly Stopwatch repeatLimitTimer = Stopwatch.StartNew();
        private readonly Dictionary<ushort, Registration> registrations = new();
        private readonly Func<HotkeyInfo, string> friendlyNameProvider;
        private KGlobalAccel kglobalaccel;
        private IDisposable signalWatcher;
        private bool initialized, available, closing;
        // Snapshot of registered action IDs for the exit handlers, which run off the UI thread.
        private string[][] registeredActionIds = Array.Empty<string[]>();
        private PosixSignalRegistration[] signalRegistrations;

        public event HotkeyEventHandler HotkeyPress;
        public event EventHandler Closed;

        // There is no native window on Linux, so no native messages are ever delivered.
        public event EventHandler<NativeWindowMessageEventArgs> NativeMessageReceived { add { } remove { } }

        public IntPtr Handle => IntPtr.Zero;
        public bool IsDisposed { get; private set; }
        public int HotkeyRepeatLimit { get; set; } = 1000;

        /// <param name="friendlyNameProvider">Optional; returns the description shown for a hotkey in KDE's shortcut settings.</param>
        public LinuxHotkeyHost(Func<HotkeyInfo, string> friendlyNameProvider = null)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("LinuxHotkeyHost is only supported on Linux.");
            this.friendlyNameProvider = friendlyNameProvider;
        }

        public void Initialize()
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (initialized) return;
            initialized = true;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            // .NET does not raise ProcessExit for SIGTERM/SIGINT/SIGHUP (logout, Ctrl+C, closed terminal).
            // The handlers don't cancel the signal, so the default termination still follows.
            signalRegistrations = new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT }
                .Select(signal => PosixSignalRegistration.Create(signal, _ => ReleaseRegisteredActions())).ToArray();

            try
            {
                kglobalaccel = new KGlobalAccel(DBusConnection.Session, ComponentUnique, ComponentFriendly);
                available = RunSync(kglobalaccel.IsAvailableAsync);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Unable to connect to the D-Bus session bus for global hotkeys");
                available = false;
                return;
            }

            if (!available)
            {
                DebugHelper.WriteLine($"Global hotkeys unavailable: {KGlobalAccel.ServiceName} is not running on the session bus (KDE Plasma is required).");
                return;
            }

            // Drop actions left behind by a previous session that did not exit cleanly.
            try
            {
                foreach (string[] actionId in RunSync(kglobalaccel.GetAllActionsAsync))
                {
                    if (actionId.Length >= 2 && actionId[1].StartsWith(ActionPrefix, StringComparison.Ordinal))
                    {
                        RunSync(() => kglobalaccel.UnRegisterAsync(actionId));
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteLine("Unable to remove stale KGlobalAccel actions: " + e.Message);
            }
        }

        public void RegisterHotkey(HotkeyInfo hotkeyInfo)
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(IsDisposed || closing, this);
            if (hotkeyInfo == null || hotkeyInfo.Status == HotkeyStatus.Registered) return;
            if (!hotkeyInfo.IsValidHotkey)
            {
                hotkeyInfo.Status = HotkeyStatus.NotConfigured;
                return;
            }

            Initialize();

            int qtKey = QtKeyMapping.ToQtShortcut(hotkeyInfo);
            if (qtKey == 0)
            {
                DebugHelper.WriteLine("Unable to register hotkey, key has no KDE equivalent: " + hotkeyInfo);
                hotkeyInfo.Status = HotkeyStatus.Failed;
                return;
            }

            if (!available)
            {
                hotkeyInfo.Status = HotkeyStatus.Failed;
                return;
            }

            if (hotkeyInfo.ID == 0 || registrations.ContainsKey(hotkeyInfo.ID))
            {
                hotkeyInfo.ID = AllocateID();
                if (hotkeyInfo.ID == 0)
                {
                    DebugHelper.WriteLine("Unable to generate unique hotkey ID: " + hotkeyInfo);
                    hotkeyInfo.Status = HotkeyStatus.Failed;
                    return;
                }
            }

            string[] actionId = kglobalaccel.GetActionId(ActionPrefix + hotkeyInfo.ID, GetFriendlyName(hotkeyInfo));

            try
            {
                // setShortcut with NoAutoloading does not reject keys owned by other components (e.g. Spectacle owns
                // Print by default), so check first. This also catches two ShareX hotkeys sharing one combination.
                if (!RunSync(() => kglobalaccel.IsShortcutAvailableAsync(qtKey)))
                {
                    DebugHelper.WriteLine("Unable to register hotkey, the shortcut is already in use: " + hotkeyInfo);
                    hotkeyInfo.ID = 0;
                    hotkeyInfo.Status = HotkeyStatus.Failed;
                    return;
                }

                RunSync(() => kglobalaccel.DoRegisterAsync(actionId));
                int[] assignedKeys = RunSync(() => kglobalaccel.SetShortcutAsync(actionId, new int[] { qtKey },
                    KGlobalAccel.SetShortcutFlags.SetPresent | KGlobalAccel.SetShortcutFlags.NoAutoloading));

                if (assignedKeys == null || !assignedKeys.Contains(qtKey))
                {
                    DebugHelper.WriteLine("Unable to register hotkey, KGlobalAccel did not assign the shortcut: " + hotkeyInfo);
                    TryUnregister(actionId);
                    hotkeyInfo.ID = 0;
                    hotkeyInfo.Status = HotkeyStatus.Failed;
                    return;
                }

                EnsureSignalWatcher();
            }
            catch (Exception e)
            {
                DebugHelper.WriteLine($"Unable to register hotkey: {hotkeyInfo} ({e.Message})");
                TryUnregister(actionId);
                hotkeyInfo.ID = 0;
                hotkeyInfo.Status = HotkeyStatus.Failed;
                return;
            }

            registrations[hotkeyInfo.ID] = new Registration { HotkeyInfo = hotkeyInfo, ActionId = actionId };
            UpdateRegisteredActionIds();
            hotkeyInfo.Status = HotkeyStatus.Registered;
        }

        public bool UnregisterHotkey(HotkeyInfo hotkeyInfo)
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (hotkeyInfo == null) return false;

            if (hotkeyInfo.ID > 0 && registrations.TryGetValue(hotkeyInfo.ID, out Registration registration) &&
                registration.HotkeyInfo == hotkeyInfo && TryUnregister(registration.ActionId))
            {
                registrations.Remove(hotkeyInfo.ID);
                UpdateRegisteredActionIds();
                hotkeyInfo.ID = 0;
                hotkeyInfo.Status = HotkeyStatus.NotConfigured;
                return true;
            }

            hotkeyInfo.Status = HotkeyStatus.Failed;
            return false;
        }

        public void Close()
        {
            VerifyAccess();
            if (IsDisposed || closing) return;
            closing = true;
            try
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                foreach (Registration registration in registrations.Values.ToArray())
                {
                    TryUnregister(registration.ActionId);
                    registration.HotkeyInfo.ID = 0;
                    registration.HotkeyInfo.Status = HotkeyStatus.NotConfigured;
                }
                registrations.Clear();
                UpdateRegisteredActionIds();
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                if (signalRegistrations != null)
                {
                    foreach (PosixSignalRegistration registration in signalRegistrations) registration.Dispose();
                    signalRegistrations = null;
                }
                signalWatcher?.Dispose();
                signalWatcher = null;
                IsDisposed = true;
                closing = false;
            }
        }

        public void Dispose() => Close();

        private void EnsureSignalWatcher()
        {
            if (signalWatcher != null) return;
            // The component object exists once the first action has been registered.
            string componentPath = RunSync(kglobalaccel.GetComponentPathAsync);
            signalWatcher = RunSync(() => kglobalaccel.WatchShortcutPressedAsync(componentPath, OnShortcutPressed));
            DebugHelper.WriteLine("Listening for KGlobalAccel shortcuts on " + componentPath);
        }

        // Called on a D-Bus thread.
        private void OnShortcutPressed(string actionUnique)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsDisposed || closing) return;
                Registration registration = registrations.Values.FirstOrDefault(x => x.ActionId[1] == actionUnique);
                if (registration != null && CheckRepeatLimitTime())
                {
                    HotkeyInfo hotkeyInfo = registration.HotkeyInfo;
                    HotkeyPress?.Invoke(hotkeyInfo.ID, hotkeyInfo.KeyCode, hotkeyInfo.ModifiersEnum);
                }
            });
        }

        private void UpdateRegisteredActionIds()
        {
            Volatile.Write(ref registeredActionIds, registrations.Values.Select(x => x.ActionId).ToArray());
        }

        private void OnProcessExit(object sender, EventArgs e) => ReleaseRegisteredActions();

        // Covers exits that skip Close() (e.g. SIGTERM on logout): otherwise KDE keeps grabbing the keys
        // until ShareX starts again and removes its stale actions. May run on any thread.
        private void ReleaseRegisteredActions()
        {
            foreach (string[] actionId in Interlocked.Exchange(ref registeredActionIds, Array.Empty<string[]>()))
            {
                TryUnregister(actionId);
            }
        }

        private bool CheckRepeatLimitTime()
        {
            if (HotkeyRepeatLimit > 0)
            {
                if (repeatLimitTimer.ElapsedMilliseconds < HotkeyRepeatLimit) return false;
                repeatLimitTimer.Restart();
            }
            return true;
        }

        private ushort AllocateID()
        {
            // Lowest free ID, so action names stay the same across restarts for an unchanged hotkey list.
            for (int id = 1; id <= ushort.MaxValue; id++)
            {
                if (!registrations.ContainsKey((ushort)id)) return (ushort)id;
            }
            return 0;
        }

        private string GetFriendlyName(HotkeyInfo hotkeyInfo)
        {
            string name = null;
            try
            {
                name = friendlyNameProvider?.Invoke(hotkeyInfo);
            }
            catch (Exception e)
            {
                DebugHelper.WriteLine("Unable to get hotkey description: " + e.Message);
            }
            return string.IsNullOrWhiteSpace(name) ? "ShareX hotkey " + hotkeyInfo.ID : name;
        }

        private bool TryUnregister(string[] actionId)
        {
            if (kglobalaccel == null || !available) return false;
            try
            {
                RunSync(() => kglobalaccel.UnRegisterAsync(actionId));
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteLine($"Unable to unregister KGlobalAccel action {actionId[1]}: {e.Message}");
                return false;
            }
        }

        // D-Bus calls are async; run them off the UI thread and wait so registration status is known synchronously,
        // like RegisterHotKey on Windows. KGlobalAccel is local and answers within milliseconds.
        private static T RunSync<T>(Func<Task<T>> call)
        {
            return Task.Run(call).WaitAsync(CallTimeout).GetAwaiter().GetResult();
        }

        private static void RunSync(Func<Task> call)
        {
            Task.Run(call).WaitAsync(CallTimeout).GetAwaiter().GetResult();
        }

        private void VerifyAccess()
        {
            if (Environment.CurrentManagedThreadId != threadId)
                throw new InvalidOperationException("The hotkey host must be accessed on the thread that created it.");
        }
    }
}

#endif
