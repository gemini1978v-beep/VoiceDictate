using System.Globalization;

namespace VoiceDictate
{
    /// <summary>
    /// Язык интерфейса: русский или английский (ui_lang в settings.ini, переключатель RU/EN в окне).
    /// Строки переводятся на месте: L.T("по-русски", "in English").
    /// </summary>
    internal static class L
    {
        public static bool En;

        /// <summary>Версия программы «1.1» (задаётся в VoiceDictate.csproj).</summary>
        public static readonly string Version = GetVersion();

        private static string GetVersion()
        {
            System.Version v = typeof(L).Assembly.GetName().Version;
            return v == null ? "" : v.Major + "." + v.Minor;
        }

        /// <summary>Языки речи и перевода, которые понимает Whisper: код, своё название, название по-английски.</summary>
        public static readonly string[][] SpeechLangs =
        {
            new[] { "ru", "Русский", "Russian" }, new[] { "en", "English", "English" },
            new[] { "uk", "Українська", "Ukrainian" }, new[] { "be", "Беларуская", "Belarusian" },
            new[] { "kk", "Қазақша", "Kazakh" }, new[] { "de", "Deutsch", "German" },
            new[] { "fr", "Français", "French" }, new[] { "es", "Español", "Spanish" },
            new[] { "it", "Italiano", "Italian" }, new[] { "pt", "Português", "Portuguese" },
            new[] { "pl", "Polski", "Polish" }, new[] { "cs", "Čeština", "Czech" },
            new[] { "nl", "Nederlands", "Dutch" }, new[] { "sv", "Svenska", "Swedish" },
            new[] { "fi", "Suomi", "Finnish" }, new[] { "tr", "Türkçe", "Turkish" },
            new[] { "el", "Ελληνικά", "Greek" }, new[] { "bg", "Български", "Bulgarian" },
            new[] { "sr", "Српски", "Serbian" }, new[] { "ro", "Română", "Romanian" },
            new[] { "hu", "Magyar", "Hungarian" }, new[] { "lt", "Lietuvių", "Lithuanian" },
            new[] { "lv", "Latviešu", "Latvian" }, new[] { "et", "Eesti", "Estonian" },
            new[] { "ka", "ქართული", "Georgian" }, new[] { "hy", "Հայերեն", "Armenian" },
            new[] { "az", "Azərbaycan", "Azerbaijani" }, new[] { "uz", "Oʻzbek", "Uzbek" },
            new[] { "he", "עברית", "Hebrew" }, new[] { "ar", "العربية", "Arabic" },
            new[] { "hi", "हिन्दी", "Hindi" }, new[] { "zh", "中文", "Chinese" },
            new[] { "ja", "日本語", "Japanese" }, new[] { "ko", "한국어", "Korean" },
            new[] { "vi", "Tiếng Việt", "Vietnamese" }, new[] { "id", "Bahasa Indonesia", "Indonesian" },
        };

        /// <summary>Название языка: своё («Deutsch») или по-английски («German»).</summary>
        public static string LangName(string code, bool english)
        {
            foreach (string[] l in SpeechLangs)
                if (l[0] == code) return english ? l[2] : l[1];
            return code;
        }

        public static string Code
        {
            get { return En ? "en" : "ru"; }
        }

        public static string T(string ru, string en)
        {
            return En ? en : ru;
        }

        /// <summary>Язык по умолчанию — по языку Windows: русский для ru/uk/be/kk, иначе английский.</summary>
        public static string SystemDefault()
        {
            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return two == "ru" || two == "uk" || two == "be" || two == "kk" ? "ru" : "en";
        }
    }
}
