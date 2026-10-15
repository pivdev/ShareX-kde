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

using System.Collections.Generic;

namespace ShareX.HelpersLib
{
    /// <summary>Converts ShareX hotkeys (Windows virtual-key codes) to Qt key codes as used by KGlobalAccel.</summary>
    public static class QtKeyMapping
    {
        public const int ShiftModifier = 0x02000000;
        public const int ControlModifier = 0x04000000;
        public const int AltModifier = 0x08000000;
        public const int MetaModifier = 0x10000000;
        public const int KeypadModifier = 0x20000000;

        private const int Key_F1 = 0x01000030;

        private static readonly Dictionary<InputKey, int> keyMap = CreateKeyMap();

        private static Dictionary<InputKey, int> CreateKeyMap()
        {
            Dictionary<InputKey, int> map = new Dictionary<InputKey, int>
            {
                // Editing and navigation
                [InputKey.Escape] = 0x01000000,
                [InputKey.Tab] = 0x01000001,
                [InputKey.Back] = 0x01000003, // Key_Backspace
                [InputKey.Return] = 0x01000004,
                [InputKey.Insert] = 0x01000006,
                [InputKey.Delete] = 0x01000007,
                [InputKey.Pause] = 0x01000008,
                [InputKey.PrintScreen] = 0x01000009, // Key_Print
                [InputKey.Print] = 0x01000009, // Hotkeys saved by the X11 key name "Print" before it was mapped to PrintScreen
                [InputKey.Clear] = 0x0100000b,
                [InputKey.Home] = 0x01000010,
                [InputKey.End] = 0x01000011,
                [InputKey.Left] = 0x01000012,
                [InputKey.Up] = 0x01000013,
                [InputKey.Right] = 0x01000014,
                [InputKey.Down] = 0x01000015,
                [InputKey.PageUp] = 0x01000016,
                [InputKey.PageDown] = 0x01000017,
                [InputKey.CapsLock] = 0x01000024,
                [InputKey.NumLock] = 0x01000025,
                [InputKey.Scroll] = 0x01000026, // Key_ScrollLock
                [InputKey.Apps] = 0x01000055, // Key_Menu
                [InputKey.Help] = 0x01000058,
                [InputKey.Space] = 0x20,
                [InputKey.Select] = 0x01010000,
                [InputKey.Cancel] = 0x01020001,
                [InputKey.Execute] = 0x01020003,
                [InputKey.Sleep] = 0x01020004,
                [InputKey.Play] = 0x01020005,
                [InputKey.Zoom] = 0x01020006,

                // Numeric keypad operators: Qt uses the base key plus KeypadModifier.
                [InputKey.Multiply] = '*' | KeypadModifier,
                [InputKey.Add] = '+' | KeypadModifier,
                [InputKey.Separator] = ',' | KeypadModifier,
                [InputKey.Subtract] = '-' | KeypadModifier,
                [InputKey.Decimal] = '.' | KeypadModifier,
                [InputKey.Divide] = '/' | KeypadModifier,

                // OEM keys, mapped to the characters they produce on a US layout.
                [InputKey.OemSemicolon] = ';',
                [InputKey.Oemplus] = '=',
                [InputKey.Oemcomma] = ',',
                [InputKey.OemMinus] = '-',
                [InputKey.OemPeriod] = '.',
                [InputKey.OemQuestion] = '/',
                [InputKey.Oemtilde] = '`', // Key_QuoteLeft
                [InputKey.OemOpenBrackets] = '[',
                [InputKey.OemPipe] = '\\',
                [InputKey.OemCloseBrackets] = ']',
                [InputKey.OemQuotes] = '\'', // Key_Apostrophe
                [InputKey.OemBackslash] = '<', // Extra ISO key next to left Shift.

                // Browser, media and launch keys
                [InputKey.BrowserBack] = 0x01000061,
                [InputKey.BrowserForward] = 0x01000062,
                [InputKey.BrowserStop] = 0x01000063,
                [InputKey.BrowserRefresh] = 0x01000064,
                [InputKey.VolumeDown] = 0x01000070,
                [InputKey.VolumeMute] = 0x01000071,
                [InputKey.VolumeUp] = 0x01000072,
                [InputKey.MediaStop] = 0x01000081,
                [InputKey.MediaPreviousTrack] = 0x01000082,
                [InputKey.MediaNextTrack] = 0x01000083,
                [InputKey.MediaPlayPause] = 0x01000086, // Key_MediaTogglePlayPause
                [InputKey.BrowserHome] = 0x01000090, // Key_HomePage
                [InputKey.BrowserFavorites] = 0x01000091,
                [InputKey.BrowserSearch] = 0x01000092,
                [InputKey.LaunchMail] = 0x010000a0,
                [InputKey.SelectMedia] = 0x010000a1, // Key_LaunchMedia
                [InputKey.LaunchApplication1] = 0x010000a2, // Key_Launch0
                [InputKey.LaunchApplication2] = 0x010000a3 // Key_Launch1
            };

            // Letters and top-row digits share their ASCII values with Qt::Key.
            for (InputKey key = InputKey.A; key <= InputKey.Z; key++) map[key] = (int)key;
            for (InputKey key = InputKey.D0; key <= InputKey.D9; key++) map[key] = (int)key;
            for (int i = 0; i <= 9; i++) map[InputKey.NumPad0 + i] = ('0' + i) | KeypadModifier;
            for (int i = 0; i < 24; i++) map[InputKey.F1 + i] = Key_F1 + i;

            return map;
        }

        /// <summary>Returns the Qt key code for a virtual-key code (modifier bits are ignored), or 0 if it has no Qt equivalent.</summary>
        public static int ToQtKey(InputKey keyCode)
        {
            return keyMap.TryGetValue(keyCode & InputKey.KeyCode, out int qtKey) ? qtKey : 0;
        }

        /// <summary>Returns the Qt key combination (key | modifiers) for a hotkey, or 0 if it can't be represented.</summary>
        public static int ToQtShortcut(HotkeyInfo hotkeyInfo)
        {
            if (hotkeyInfo == null || !hotkeyInfo.IsValidHotkey) return 0;

            int qtKey = ToQtKey(hotkeyInfo.KeyCode);
            if (qtKey == 0) return 0;

            if (hotkeyInfo.Shift) qtKey |= ShiftModifier;
            if (hotkeyInfo.Control) qtKey |= ControlModifier;
            if (hotkeyInfo.Alt) qtKey |= AltModifier;
            if (hotkeyInfo.Win) qtKey |= MetaModifier;

            return qtKey;
        }
    }
}

#endif
