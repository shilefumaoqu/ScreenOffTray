using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScreenOffTray
{
    internal sealed class HotkeyBinding
    {
        private static readonly HotkeyBinding defaultBinding = new HotkeyBinding(3, 0x79);
        internal static HotkeyBinding Default { get { return defaultBinding; } }
        internal uint Modifiers { get; private set; }
        internal int Key { get; private set; }
        internal string DisplayText { get { return Serialize(); } }

        private HotkeyBinding(uint modifiers, int key) { Modifiers = modifiers; Key = key; }

        internal string Serialize()
        {
            string value = "";
            if ((Modifiers & 2) != 0) value += "Ctrl+";
            if ((Modifiers & 1) != 0) value += "Alt+";
            if ((Modifiers & 4) != 0) value += "Shift+";
            if ((Modifiers & 8) != 0) value += "Win+";
            return value + (Key >= 0x70 && Key <= 0x87
                ? "F" + (Key - 0x6F).ToString(CultureInfo.InvariantCulture) : ((char)Key).ToString());
        }

        internal bool SameAs(HotkeyBinding other)
        {
            return other != null && other.Modifiers == Modifiers && other.Key == Key;
        }

        internal bool Matches(uint modifiers, int key, bool injected)
        {
            return !injected && modifiers == Modifiers && key == Key;
        }

        internal static bool TryCreate(uint modifiers, int key, out HotkeyBinding binding, out string error)
        {
            binding = null; error = null;
            if ((modifiers & ~15U) != 0) {
                error = "修饰键无效 / Invalid modifier flags."; return false;
            }
            bool functionKey = key >= 0x70 && key <= 0x87;
            bool ordinaryKey = (key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9');
            if (!functionKey && !ordinaryKey) {
                error = "请选择 F1–F24、A–Z 或 0–9 / Choose F1-F24, A-Z or 0-9."; return false;
            }
            if (key == 0x7B) {
                error = "F12 由系统保留 / F12 is reserved by the system."; return false;
            }
            if (ordinaryKey && (modifiers & 11U) == 0) {
                error = "字母和数字至少需要 Ctrl、Alt 或 Win / Letters and digits require Ctrl, Alt or Win."; return false;
            }
            if ((modifiers & 8) != 0 && key == 'L') {
                error = "Win+L 由系统保留 / Win+L is reserved by the system."; return false;
            }
            binding = new HotkeyBinding(modifiers, key); return true;
        }

        internal static bool TryParse(string value, out HotkeyBinding binding, out string error)
        {
            binding = null;
            error = "快捷键格式无效 / Invalid hotkey format.";
            if (String.IsNullOrWhiteSpace(value)) return false;
            string[] parts = value.Split('+');
            uint modifiers = 0;
            for (int i = 0; i < parts.Length - 1; i++) {
                string part = parts[i].Trim(); uint flag;
                if (String.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase)) flag = 2;
                else if (String.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)) flag = 1;
                else if (String.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)) flag = 4;
                else if (String.Equals(part, "Win", StringComparison.OrdinalIgnoreCase)) flag = 8;
                else return false;
                if ((modifiers & flag) != 0) return false;
                modifiers |= flag;
            }
            string token = parts[parts.Length - 1].Trim().ToUpperInvariant();
            int key;
            if (token.Length == 1 && ((token[0] >= 'A' && token[0] <= 'Z') || (token[0] >= '0' && token[0] <= '9')))
                key = token[0];
            else {
                int number;
                if (token.Length < 2 || token[0] != 'F' ||
                    !Int32.TryParse(token.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                    number < 1 || number > 24 || token != "F" + number.ToString(CultureInfo.InvariantCulture)) return false;
                key = 0x6F + number;
            }
            return TryCreate(modifiers, key, out binding, out error);
        }
    }

    // Hook-only physical modifier history. Injected modifiers cannot supply a
    // missing physical modifier; the async mask also rejects extra injected holds.
    internal sealed class HotkeyInputState
    {
        private readonly HashSet<int> heldModifiers = new HashSet<int>();
        internal void Observe(int key, bool up, bool injected)
        {
            if (injected || Modifier(key) == 0) return;
            if (up) heldModifiers.Remove(key); else heldModifiers.Add(key);
        }
        internal uint PhysicalModifiers { get {
            uint result = 0;
            foreach (int key in heldModifiers) result |= Modifier(key);
            return result;
        } }
        internal bool Matches(HotkeyBinding binding, int key, bool up, bool injected, uint actualModifiers)
        {
            return !up && binding != null && binding.Matches(actualModifiers, key, injected) &&
                PhysicalModifiers == binding.Modifiers;
        }
        private static uint Modifier(int key)
        {
            switch (key) {
                case 0x10: case 0xA0: case 0xA1: return 4;
                case 0x11: case 0xA2: case 0xA3: return 2;
                case 0x12: case 0xA4: case 0xA5: return 1;
                case 0x5B: case 0x5C: return 8;
                default: return 0;
            }
        }
    }
}
