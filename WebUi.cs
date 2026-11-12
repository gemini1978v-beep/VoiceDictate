using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace VoiceDictate
{
    /// <summary>Общее для окон на HTML: один движок WebView2 на всю программу, загрузка страниц, цвета окна.</summary>
    internal static class WebUi
    {
        private static Task<CoreWebView2Environment> envTask;

        public static Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (envTask == null)
            {
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VoiceDictate", "WebView2");
                // запись «через веб» включается по F3 без щелчка по странице — звук должен работать без жеста пользователя
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                envTask = CoreWebView2Environment.CreateAsync(null, dataDir, options);
            }
            return envTask;
        }

        /// <summary>
        /// Страница из ui\ рядом с exe (можно править без пересборки), иначе — встроенная копия.
        /// Тему ставим сразу (data-theme="obsidian" / "graphite"), чтобы при загрузке не мигало белым.
        /// </summary>
        public static string LoadHtml(string file, string theme)
        {
            string html;
            string ext = Path.Combine(WhisperEngine.BaseDir, "ui", file);
            if (File.Exists(ext))
            {
                html = File.ReadAllText(ext, Encoding.UTF8);
            }
            else
            {
                using (Stream st = typeof(WebUi).Assembly.GetManifestResourceStream("VoiceDictate.ui." + file))
                {
                    if (st == null) throw new InvalidOperationException(L.T("в программе нет файла ui\\", "the program has no file ui\\") + file);
                    using (var r = new StreamReader(st, Encoding.UTF8)) html = r.ReadToEnd();
                }
            }
            if (theme == "obsidian" || theme == "graphite" || theme == "bordo")
                html = html.Replace("<html lang=\"ru\">", "<html lang=\"ru\" data-theme=\"" + theme + "\">");
            // английский интерфейс — сразу, чтобы страница не мелькала русским текстом
            if (L.En) html = html.Replace("<html lang=\"ru\"", "<html lang=\"en\" data-lang=\"en\"");
            return html;
        }

        /// <summary>
        /// Адрес главной страницы. Страница отдаётся программой по https-адресу, а не через NavigateToString:
        /// микрофон (getUserMedia) браузер даёт только «безопасным» страницам.
        /// </summary>
        public const string AppOrigin = "https://voicedictate.local/";

        /// <summary>Отдавать страницу file из ui\ по адресу AppOrigin + file.</summary>
        public static void ServePage(CoreWebView2 core, CoreWebView2Environment env, string file, Func<string> theme)
        {
            core.AddWebResourceRequestedFilter(AppOrigin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, e) =>
            {
                Uri uri;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) ||
                    !string.Equals(uri.AbsolutePath.TrimStart('/'), file, StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(LoadHtml(file, theme()));
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
        }

        /// <summary>Цвет окна до загрузки страницы — как фон темы, чтобы не мигало.</summary>
        public static Color BackColor(string theme)
        {
            if (theme == "obsidian") return Color.FromArgb(8, 13, 27);
            if (theme == "graphite") return Color.FromArgb(43, 46, 52);
            if (theme == "bordo") return Color.FromArgb(42, 13, 23);
            return Color.White;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Windows 11: скруглённые углы и тёмная/светлая рамка окна. На Windows 10 просто ничего не делает.</summary>
        public static void StyleWindow(IntPtr hwnd, bool dark)
        {
            try
            {
                int round = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                int d = dark ? 1 : 0;   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 20, ref d, sizeof(int));
            }
            catch
            {
                // старая Windows
            }
        }
    }
}
