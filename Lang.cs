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
