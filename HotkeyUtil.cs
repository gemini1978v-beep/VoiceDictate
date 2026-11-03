using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace VoiceDictate
{
    /// <summary>Разбор и запись горячих клавиш вида "F1", "Ctrl+Alt+R".</summary>
    internal static class HotkeyUtil
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public static bool TryParse(string text, out uint mods, out Keys key)
        {
            mods = 0;
            key = Keys.None;
            if (string.IsNullOrWhiteSpace(text)) return false;

            foreach (string partRaw in text.Split('+'))
            {
                string part = partRaw.Trim();
                if (part.Length == 0) return false;

                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        mods |= MOD_CONTROL;
                        continue;
                    case "alt":
                        mods |= MOD_ALT;
                        continue;
                    case "shift":
                        mods |= MOD_SHIFT;
                        continue;
                    case "win":
                    case "windows":
                        mods |= MOD_WIN;
                        continue;
                }

                if (key != Keys.None) return false; // две обычные клавиши — так нельзя

                string name = part;
                if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;

                Keys k;
                if (!Enum.TryParse(name, true, out k)) return false;
                if (k == Keys.None || IsModifierKey(k)) return false;
                key = k;
            }
            return key != Keys.None;
        }

        public static bool IsModifierKey(Keys k)
        {
            switch (k)
            {
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu:
                case Keys.LWin:
                case Keys.RWin:
                    return true;
                default:
                    return false;
            }
        }

        public static string Format(uint mods, Keys key)
        {
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & MOD_WIN) != 0) parts.Add("Win");

            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            parts.Add(name);
            return string.Join("+", parts);
        }
    }
}
