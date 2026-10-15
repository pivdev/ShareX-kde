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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    public sealed class LinuxWindow
    {
        public string Id { get; init; }
        public IntPtr Handle { get; init; }
        public string Caption { get; init; }
        public string ResourceClass { get; init; }
        public string ResourceName { get; init; }
        public int Pid { get; init; }
        public Rectangle Bounds { get; init; }
        public Rectangle ClientBounds { get; init; }
        public bool Minimized { get; init; }
        public bool NormalWindow { get; init; }
        public bool Dialog { get; init; }
        public bool DesktopWindow { get; init; }
        public bool Dock { get; init; }
        public bool OnCurrentDesktop { get; init; }
        public bool FullScreen { get; init; }
        public bool KeepAbove { get; init; }
        public bool SkipTaskbar { get; init; }
        public bool Active { get; init; }
        public string Output { get; init; }

        /// <summary>Visible, user-facing window on the current virtual desktop.</summary>
        public bool IsVisible => !Minimized && OnCurrentDesktop && !DesktopWindow && Bounds.Width > 0 && Bounds.Height > 0;

        public override string ToString() => Caption;
    }

    public sealed class KWinOutput
    {
        public string Name { get; init; }
        public Rectangle Bounds { get; init; }
        public double DevicePixelRatio { get; init; }
    }

    public sealed class KWinState
    {
        public Point CursorPosition { get; init; }
        public LinuxWindow ActiveWindow { get; init; }
        /// <summary>Windows ordered from top-most to bottom-most.</summary>
        public IReadOnlyList<LinuxWindow> Windows { get; init; }
        public IReadOnlyList<KWinOutput> Outputs { get; init; }
        public long Timestamp { get; init; }
    }

    /// <summary>
    /// Window manager queries on KDE Plasma (Wayland and X11) through short-lived KWin scripts.
    /// Scripts report back to ShareX over D-Bus because KWin scripting has no return channel.
    /// KWin window UUIDs are mapped to synthetic handles so existing IntPtr-based code keeps working.
    /// </summary>
    public static class KWinBridge
    {
        private const string BridgePath = "/ShareX/KWinBridge";
        private const string BridgeInterface = "org.sharex.KWinBridge";

        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pending = new();
        private static readonly ConcurrentDictionary<string, IntPtr> handlesById = new();
        private static readonly ConcurrentDictionary<IntPtr, LinuxWindow> windowsByHandle = new();
        private static long nextHandle = 0x10000;
        private static int handlerRegistered;
        private static bool? available;
        private static KWinState lastState;

        public static bool IsAvailable
        {
            get
            {
                available ??= LinuxDBus.RunSync(() => LinuxDBus.NameHasOwnerAsync("org.kde.KWin"));
                return available.Value;
            }
        }

        public static KWinState Query(int maxAgeMilliseconds = 100)
        {
            KWinState state = lastState;
            if (state != null && Stopwatch.GetElapsedTime(state.Timestamp).TotalMilliseconds <= maxAgeMilliseconds)
            {
                return state;
            }

            return LinuxDBus.RunSync(QueryAsync);
        }

        public static async Task<KWinState> QueryAsync()
        {
            const string script = """
                var active = workspace.activeWindow;
                var o = { cursor: { x: workspace.cursorPos.x, y: workspace.cursorPos.y }, active: active ? active.internalId.toString() : "", windows: [], outputs: [] };
                var stack = workspace.stackingOrder;
                for (var i = stack.length - 1; i >= 0; i--) {
                    var w = stack[i];
                    if (w.deleted) continue;
                    var g = w.frameGeometry, c = w.clientGeometry;
                    o.windows.push({ id: w.internalId.toString(), caption: w.caption, cls: w.resourceClass, name: w.resourceName, pid: w.pid,
                        x: g.x, y: g.y, w: g.width, h: g.height, cx: c.x, cy: c.y, cw: c.width, ch: c.height,
                        min: w.minimized, normal: w.normalWindow, dialog: w.dialog, desktop: w.desktopWindow, dock: w.dock,
                        cur: w.onAllDesktops || w.desktops.indexOf(workspace.currentDesktop) >= 0, full: w.fullScreen, above: w.keepAbove,
                        skip: w.skipTaskbar, output: w.output ? w.output.name : "" });
                }
                for (var j = 0; j < workspace.screens.length; j++) {
                    var s = workspace.screens[j];
                    o.outputs.push({ name: s.name, x: s.geometry.x, y: s.geometry.y, w: s.geometry.width, h: s.geometry.height, dpr: s.devicePixelRatio });
                }
                report(JSON.stringify(o));
                """;

            string json = await RunScriptAsync(script).ConfigureAwait(false);
            KWinState state = ParseState(json);
            lastState = state;
            return state;
        }

        public static void Invalidate() => lastState = null;

        public static LinuxWindow GetWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            windowsByHandle.TryGetValue(handle, out LinuxWindow window);
            return window;
        }

        /// <summary>Returns the latest known state of the window, refreshing from KWin if the cached data is stale.</summary>
        public static LinuxWindow GetFreshWindow(IntPtr handle, int maxAgeMilliseconds = 500)
        {
            LinuxWindow cached = GetWindow(handle);
            if (cached == null) return null;

            try
            {
                KWinState state = Query(maxAgeMilliseconds);
                return state.Windows.FirstOrDefault(x => x.Handle == handle);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return cached;
            }
        }

        public static IntPtr GetForegroundWindow()
        {
            try
            {
                return Query().ActiveWindow?.Handle ?? IntPtr.Zero;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return IntPtr.Zero;
            }
        }

        public static Point GetCursorPosition()
        {
            try
            {
                return Query(30).CursorPosition;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return Point.Empty;
            }
        }

        public static Task ActivateWindowAsync(IntPtr handle)
        {
            LinuxWindow window = GetWindow(handle);
            if (window == null) return Task.CompletedTask;

            string script = $$"""
                var id = {{JsonSerializer.Serialize(window.Id)}};
                var list = workspace.windowList();
                for (var i = 0; i < list.length; i++) {
                    if (list[i].internalId.toString() === id) {
                        if (list[i].minimized) list[i].minimized = false;
                        workspace.activeWindow = list[i];
                        break;
                    }
                }
                report("ok");
                """;
            Invalidate();
            return RunScriptAsync(script);
        }

        /// <summary>Sets a writable KWin window property, e.g. keepAbove, opacity, minimized, noBorder.</summary>
        public static void SetWindowProperty(IntPtr handle, string property, object value)
        {
            LinuxWindow window = GetWindow(handle);
            if (window == null) return;

            string script = $$"""
                var id = {{JsonSerializer.Serialize(window.Id)}};
                var list = workspace.windowList();
                for (var i = 0; i < list.length; i++) {
                    if (list[i].internalId.toString() === id) { list[i][{{JsonSerializer.Serialize(property)}}] = {{JsonSerializer.Serialize(value)}}; break; }
                }
                report("ok");
                """;
            Invalidate();

            try
            {
                LinuxDBus.RunSync(() => RunScriptAsync(script));
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static async Task<string> RunScriptAsync(string body)
        {
            await EnsureHandlerAsync().ConfigureAwait(false);

            string uniqueName = await LinuxDBus.GetUniqueNameAsync().ConfigureAwait(false);
            string token = Guid.NewGuid().ToString("N");
            string pluginName = "sharex-" + token;
            string script = $$"""
                function report(value) {
                    callDBus({{JsonSerializer.Serialize(uniqueName)}}, "{{BridgePath}}", "{{BridgeInterface}}", "Report", "{{token}}", value);
                }
                try {
                {{body}}
                } catch (e) {
                    callDBus({{JsonSerializer.Serialize(uniqueName)}}, "{{BridgePath}}", "{{BridgeInterface}}", "Fail", "{{token}}", String(e));
                }
                """;

            string dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), "sharex");
            Directory.CreateDirectory(dir);
            string scriptPath = Path.Combine(dir, pluginName + ".js");
            await File.WriteAllTextAsync(scriptPath, script).ConfigureAwait(false);

            TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[token] = completion;

            try
            {
                int id = await LinuxDBus.CallAsync("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss",
                    (ref MessageWriter w) => { w.WriteString(scriptPath); w.WriteString(pluginName); },
                    (m, s) => m.GetBodyReader().ReadInt32()).ConfigureAwait(false);

                if (id < 0)
                {
                    throw new InvalidOperationException("KWin refused to load the ShareX helper script.");
                }

                await LinuxDBus.CallAsync("org.kde.KWin", "/Scripting/Script" + id, "org.kde.kwin.Script", "run").ConfigureAwait(false);

                Task finished = await Task.WhenAny(completion.Task, Task.Delay(3000)).ConfigureAwait(false);
                if (finished != completion.Task)
                {
                    throw new TimeoutException("KWin helper script did not respond.");
                }

                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                pending.TryRemove(token, out _);
                try
                {
                    await LinuxDBus.CallAsync("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s",
                        (ref MessageWriter w) => w.WriteString(pluginName), (m, s) => m.GetBodyReader().ReadBool()).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
                try { File.Delete(scriptPath); } catch { }
            }
        }

        private static async Task EnsureHandlerAsync()
        {
            if (Interlocked.Exchange(ref handlerRegistered, 1) == 0)
            {
                await LinuxDBus.AddMethodHandlerAsync(new BridgeHandler()).ConfigureAwait(false);
            }
        }

        private static IntPtr GetOrCreateHandle(string id) =>
            handlesById.GetOrAdd(id, _ => new IntPtr(Interlocked.Increment(ref nextHandle)));

        private static int Round(JsonElement e, string name) => (int)Math.Round(e.GetProperty(name).GetDouble());

        private static KWinState ParseState(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string activeId = root.GetProperty("active").GetString();

            List<LinuxWindow> windows = new List<LinuxWindow>();
            foreach (JsonElement w in root.GetProperty("windows").EnumerateArray())
            {
                string id = w.GetProperty("id").GetString();
                IntPtr handle = GetOrCreateHandle(id);
                LinuxWindow window = new LinuxWindow
                {
                    Id = id,
                    Handle = handle,
                    Caption = w.GetProperty("caption").GetString(),
                    ResourceClass = w.GetProperty("cls").GetString(),
                    ResourceName = w.GetProperty("name").GetString(),
                    Pid = w.GetProperty("pid").GetInt32(),
                    Bounds = new Rectangle(Round(w, "x"), Round(w, "y"), Round(w, "w"), Round(w, "h")),
                    ClientBounds = new Rectangle(Round(w, "cx"), Round(w, "cy"), Round(w, "cw"), Round(w, "ch")),
                    Minimized = w.GetProperty("min").GetBoolean(),
                    NormalWindow = w.GetProperty("normal").GetBoolean(),
                    Dialog = w.GetProperty("dialog").GetBoolean(),
                    DesktopWindow = w.GetProperty("desktop").GetBoolean(),
                    Dock = w.GetProperty("dock").GetBoolean(),
                    OnCurrentDesktop = w.GetProperty("cur").GetBoolean(),
                    FullScreen = w.GetProperty("full").GetBoolean(),
                    KeepAbove = w.GetProperty("above").GetBoolean(),
                    SkipTaskbar = w.GetProperty("skip").GetBoolean(),
                    Output = w.GetProperty("output").GetString(),
                    Active = id == activeId
                };
                windowsByHandle[handle] = window;
                windows.Add(window);
            }

            List<KWinOutput> outputs = root.GetProperty("outputs").EnumerateArray().Select(o => new KWinOutput
            {
                Name = o.GetProperty("name").GetString(),
                Bounds = new Rectangle(Round(o, "x"), Round(o, "y"), Round(o, "w"), Round(o, "h")),
                DevicePixelRatio = o.GetProperty("dpr").GetDouble()
            }).ToList();

            JsonElement cursor = root.GetProperty("cursor");

            return new KWinState
            {
                CursorPosition = new Point(Round(cursor, "x"), Round(cursor, "y")),
                ActiveWindow = windows.FirstOrDefault(x => x.Active),
                Windows = windows,
                Outputs = outputs,
                Timestamp = Stopwatch.GetTimestamp()
            };
        }

        private sealed class BridgeHandler : IPathMethodHandler
        {
            public string Path => BridgePath;

            public bool HandlesChildPaths => false;

            public ValueTask HandleMethodAsync(MethodContext context)
            {
                Message request = context.Request;
                if (request.InterfaceAsString == BridgeInterface && (request.MemberAsString == "Report" || request.MemberAsString == "Fail"))
                {
                    Reader reader = request.GetBodyReader();
                    string token = reader.ReadString();
                    string value = reader.ReadString();

                    if (pending.TryGetValue(token, out TaskCompletionSource<string> completion))
                    {
                        if (request.MemberAsString == "Report") completion.TrySetResult(value);
                        else completion.TrySetException(new InvalidOperationException("KWin script error: " + value));
                    }

                    if (!context.NoReplyExpected)
                    {
                        context.Reply(context.CreateReplyWriter(null).CreateMessage());
                    }
                }
                else
                {
                    context.ReplyUnknownMethodError();
                }

                return default;
            }
        }
    }
}
