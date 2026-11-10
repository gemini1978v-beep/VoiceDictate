using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace VoiceDictate
{
    /// <summary>
    /// Красивый интерфейс: страница ui\index.html (HTML/CSS/JS) внутри окна через WebView2 (движок Edge).
    /// Логика программы не меняется: страница только нажимает «невидимые» старые кнопки и галочки,
    /// а программа присылает ей состояние. Если WebView2 нет — показывается старое окно.
    ///
    /// Страница → программа: строка "команда|значение" (window.chrome.webview.postMessage).
    /// Программа → страница: JSON {type: "state" | "log" | "logs" | "level" | "progress", ...}.
    /// </summary>
    internal sealed partial class MainForm
    {
        private const int WebWidth = 400;    // размер окна в «логических» пикселях (при 100% масштабе)
        private const int WebHeight = 600;
        private const string RuntimeUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        private bool webMode;                 // окно показывает HTML-интерфейс
        private bool webReady;                // страница загрузилась и прислала "ready"
        private Microsoft.Web.WebView2.WinForms.WebView2 web;
        private Panel webCover;               // сплошной фон между старым окном и страницей
        private TableLayoutPanel classicRoot; // старый интерфейс (в режиме web — спрятан под страницей)
        private string lastStateJson;
        private bool textFromWeb;
        private readonly List<string[]> logHistory = new List<string[]>();

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;

        // ================================================================ окно

        /// <summary>Переделать окно под HTML: без рамки, размер как у небольшого виджета.</summary>
        private void SetupWebWindow()
        {
            webMode = true;
            FormBorderStyle = FormBorderStyle.None;
            MinimumSize = Size.Empty;
            ClientSize = new Size(LogicalToDeviceUnits(WebWidth), LogicalToDeviceUnits(WebHeight));
            BackColor = ThemeBackColor();

            // Старые элементы остаются (через них работает вся логика), но недоступны для мыши и фокуса
            // и вынесены далеко за край окна — их никогда не видно, даже пока грузится страница
            classicRoot.Enabled = false;
            classicRoot.Dock = DockStyle.None;
            classicRoot.Size = new Size(660, 740);
            classicRoot.Location = new Point(-30000, -30000);

            web = new Microsoft.Web.WebView2.WinForms.WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = ThemeBackColor();
            Controls.Add(web);

            // Пока WebView2 запускается, он ничего не рисует и сквозь него видно старое окно.
            // Кладём под страницу сплошную подложку цвета темы: при запуске — чистый фон, потом страница.
            webCover = new Panel();
            webCover.Dock = DockStyle.Fill;
            webCover.BackColor = ThemeBackColor();
            Controls.Add(webCover);
            webCover.BringToFront();
            web.BringToFront();

            txtText.TextChanged += (s, e) =>
            {
                if (!textFromWeb) WebPushState();
            };
            Load += (s, e) => InitWeb();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (webMode)
                {
                    cp.ClassStyle |= 0x00020000;          // CS_DROPSHADOW — тень у окна без рамки
                    cp.Style |= 0x00020000 | 0x00080000;  // WS_MINIMIZEBOX | WS_SYSMENU — сворачивание с панели задач
                }
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!webMode) return;
            try
            {
                int round = 2; // Windows 11: DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch
            {
                // Windows 10 — углы останутся прямыми
            }
            ApplyWindowTheme();
        }

        private bool DarkTheme
        {
            get { return settings.Theme == "dark"; }
        }

        /// <summary>Цвет окна до загрузки страницы — чтобы не мигало белым в тёмной теме.</summary>
        private Color ThemeBackColor()
        {
            return DarkTheme ? Color.FromArgb(21, 23, 28) : Color.White;
        }

        /// <summary>Окно под тему: фон и (Windows 11) тёмная рамка.</summary>
        private void ApplyWindowTheme()
        {
            if (!webMode) return;
            BackColor = ThemeBackColor();
            if (webCover != null) webCover.BackColor = ThemeBackColor();
            try
            {
                if (web != null) web.DefaultBackgroundColor = ThemeBackColor();
            }
            catch
            {
                // не важно
            }
            if (!IsHandleCreated) return;
            try
            {
                int dark = DarkTheme ? 1 : 0; // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            }
            catch
            {
                // старая Windows
            }
        }

        private async void InitWeb()
        {
            try
            {
                CoreWebView2Environment env = await WebUi.GetEnvironmentAsync();
                if (IsDisposed || web == null) return;
                await web.EnsureCoreWebView2Async(env);
                if (IsDisposed || web == null) return;

                CoreWebView2 core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F3/F5/Ctrl+F и т. п. не трогают страницу
                core.WebMessageReceived += OnWebMessage;
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    OpenUrl(e.Uri);
                };
                core.ProcessFailed += (s, e) =>
                {
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    {
                        BeginInvoke((Action)(() => FallbackToClassic(L.T("процесс WebView2 закрылся", "the WebView2 process exited"))));
                        return;
                    }
                    try
                    {
                        webReady = false;
                        web.CoreWebView2.Reload();
                    }
                    catch
                    {
                        // ничего
                    }
                };
                core.PermissionRequested += (s, e) =>
                {
                    // запись «через веб»: страница программы сама открывает микрофон, без вопросов
                    if (e.PermissionKind == CoreWebView2PermissionKind.Microphone &&
                        e.Uri.StartsWith(WebUi.AppOrigin, StringComparison.OrdinalIgnoreCase))
                        e.State = CoreWebView2PermissionState.Allow;
                };
                WebUi.ServePage(core, env, "index.html", () => DarkTheme);
                core.Navigate(WebUi.AppOrigin + "index.html");
            }
            catch (Exception ex)
            {
                // переключаемся уже после события Load, чтобы спокойно пересоздать окно
                string why = ex.Message;
                if (IsHandleCreated) BeginInvoke((Action)(() => FallbackToClassic(why)));
            }
        }

        /// <summary>WebView2 не запустился — показываем старое окно, всё работает как раньше.</summary>
        private void FallbackToClassic(string why)
        {
            if (!webMode || IsDisposed) return;
            webMode = false;
            webReady = false;
            bool wasRegistered = slotDictate.Registered;
            Unregister(slotDictate);
            if (web != null)
            {
                Controls.Remove(web);
                try
                {
                    web.Dispose();
                }
                catch
                {
                    // не важно
                }
                web = null;
            }
            if (webCover != null)
            {
                Controls.Remove(webCover);
                webCover.Dispose();
                webCover = null;
            }
            classicRoot.Enabled = true;
            classicRoot.Location = Point.Empty;
            classicRoot.Dock = DockStyle.Fill;
            BackColor = SystemColors.Control;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(660, 740);
            MinimumSize = new Size(600, 620);
            RecreateHandle();
            CenterToScreen();
            if (wasRegistered) RegisterOrLog(slotDictate, L.T("записи", "for recording"));
            Log(L.T("Новый интерфейс не запустился (", "The new interface did not start (") + why + L.T("), показано обычное окно. ", "), showing the classic window. ") +
                L.T("Нужен Microsoft Edge WebView2 Runtime: ", "Microsoft Edge WebView2 Runtime is required: ") + RuntimeUrl);
        }

        // ================================================================ программа → страница

        private void WebPost(string json)
        {
            if (!webReady || web == null || IsDisposed) return;
            try
            {
                if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // страница перезагружается
            }
        }

        private void WebLog(string time, string msg)
        {
            logHistory.Add(new[] { time, msg });
            if (logHistory.Count > 400) logHistory.RemoveRange(0, logHistory.Count - 400);
            WebPost(new Js().S("type", "log").S("t", time).S("m", msg).ToString());
        }

        private void WebLevel(float level)
        {
            WebPost(new Js().S("type", "level").N("v", Math.Sqrt(Math.Max(0f, level))).ToString());
        }

        private void WebProgress(string what, double frac, double mb)
        {
            WebPost(new Js().S("type", "progress").S("what", what).N("frac", frac).N("mb", mb).ToString());
        }

        /// <summary>Отправить странице всё состояние (только если что-то поменялось).</summary>
        private void WebPushState()
        {
            if (!webReady) return;

            var models = new StringBuilder("[");
            for (int i = 0; i < cmbModel.Items.Count; i++)
            {
                var m = cmbModel.Items[i] as WhisperModel;
                if (m == null) continue;
                if (models.Length > 1) models.Append(',');
                models.Append(new Js().S("t", m.Title).B("ok", m.Exists).N("mb", m.SizeMb).B("giga", m.IsGigaAm).ToString());
            }
            models.Append(']');

            var mics = new List<string>();
            foreach (object o in cmbMic.Items) mics.Add(Convert.ToString(o));

            Color c = lblStatus.ForeColor;
            string kind = c == ColorGreen ? "rec" : c == ColorRed ? "err" : c == ColorAmber ? "busy" : "idle";
            WhisperModel sel = SelectedModel();

            Js js = new Js()
                .S("type", "state")
                .S("hotkey", slotDictate.Saved)
                .S("hotkeyDraft", slotDictate.Box.Text)
                .B("recording", recording)
                .B("busy", busy)
                .B("downloading", downloading)
                .N("recSeconds", recording ? Math.Floor(RecSeconds) : 0)
                .S("status", lblStatus.Text)
                .S("statusKind", kind)
                .Raw("models", models.ToString())
                .N("model", cmbModel.SelectedIndex)
                .B("modelExists", sel != null && sel.Exists)
                .Raw("mics", Js.Arr(mics))
                .S("micSource", settings.MicSource)
                .S("lang", L.Code)
                .S("version", L.Version)
                .S("speechLang", settings.Language)
                .B("translate", settings.Translate)
                .S("translateTo", settings.TranslateTo)
                .Raw("langs", LangsJson())
                .N("mic", cmbMic.SelectedIndex)
                .B("gpu", chkGpu.Checked)
                .B("cuda", WhisperEngine.CudaInstalled)
                .N("cudaMb", WhisperEngine.CudaSizeMb)
                .B("autoSend", chkAutoSend.Checked)
                .B("enter", chkEnter.Checked)
                .N("delay", (double)numDelay.Value)
                .B("typeText", chkType.Checked)
                .B("topmost", chkTopMost.Checked)
                .S("theme", DarkTheme ? "dark" : "light")
                .S("text", txtText.Text)
                .B("canDictate", !busy)
                .B("canSend", !busy && !recording)
                .B("canTest", !busy && !recording && !downloading)
                .B("canDownload", !downloading)
                .B("canCuda", !downloading && !busy && !recording)
                .B("canModel", !busy && !recording && !downloading)
                .B("canMic", !recording);
            AddCloudState(js);   // MainForm.Cloud.cs
            string json = js.ToString();

            if (json == lastStateJson) return;
            lastStateJson = json;
            WebPost(json);
        }

        // ================================================================ страница → программа

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg;
            try
            {
                msg = e.TryGetWebMessageAsString();
            }
            catch
            {
                return;
            }
            if (string.IsNullOrEmpty(msg)) return;
            if (msg.StartsWith("pcm|", StringComparison.Ordinal) || msg.StartsWith("micStarted", StringComparison.Ordinal) ||
                msg.StartsWith("micError", StringComparison.Ordinal) || msg.StartsWith("micStopped", StringComparison.Ordinal))
            {
                OnWebMicMessage(msg);   // звук идёт часто — без лишних перерисовок
                return;
            }
            if (msg == "drop")
            {
                // файл перетащили на окно: путь приходит вместе с сообщением
                string path = DroppedPath(e);
                if (path != null) BeginInvoke((Action)(() => OpenFileWindow(path)));
                return;
            }
            // Выполняем после выхода из обработчика WebView2 (перетаскивание окна и окна сообщений
            // запускают свой цикл сообщений — внутри обработчика так делать нельзя)
            BeginInvoke((Action)(() => WebCommand(msg)));
        }

        private void WebCommand(string msg)
        {
            if (IsDisposed || !webMode) return;
            int bar = msg.IndexOf('|');
            string cmd = bar < 0 ? msg : msg.Substring(0, bar);
            string arg = bar < 0 ? "" : msg.Substring(bar + 1);
            int n;

            switch (cmd)
            {
                case "ready":
                    webReady = true;
                    lastStateJson = null;
                    var all = new StringBuilder("[");
                    foreach (string[] l in logHistory)
                    {
                        if (all.Length > 1) all.Append(',');
                        all.Append(new Js().S("t", l[0]).S("m", l[1]).ToString());
                    }
                    all.Append(']');
                    WebPost(new Js().S("type", "logs").Raw("items", all.ToString()).ToString());
                    break;

                case "toggle":
                    Toggle();
                    break;
                case "send":
                    SendNow();
                    break;
                case "clear":
                    if (!busy) txtText.Clear();
                    break;
                case "text":
                    textFromWeb = true;
                    try
                    {
                        txtText.Text = arg;
                    }
                    finally
                    {
                        textFromWeb = false;
                    }
                    break;

                case "model":
                    if (int.TryParse(arg, out n) && !busy && !recording && !downloading &&
                        n >= 0 && n < cmbModel.Items.Count)
                        cmbModel.SelectedIndex = n;
                    break;
                case "mic":
                    if (int.TryParse(arg, out n) && !recording && n >= 0 && n < cmbMic.Items.Count)
                        cmbMic.SelectedIndex = n;
                    break;
                case "micSource":
                    if (!recording && !micStarting) SetMicSource(arg);
                    break;
                case "lang":
                    SetUiLang(arg);
                    break;
                case "speechLang":
                    if (!busy && !recording && arg.Length > 0 && arg.Length <= 5)
                    {
                        settings.Language = arg;
                        SaveSettings();
                        Log(L.T("Язык речи: ", "Speech language: ") + arg + ".");
                    }
                    break;
                case "translate":
                    if (!busy) SetTranslate(arg == "1");
                    break;
                case "translateTo":
                    if (!busy)
                    {
                        if (arg == "off") SetTranslate(false);
                        else SetTranslateTo(arg);
                    }
                    break;
                case "engine":
                    if (!busy && !recording) SetEngine(arg == "openrouter");
                    break;
                case "orKey":
                    SetOpenRouterKey(arg);
                    break;
                case "orModel":
                    if (int.TryParse(arg, out n) && !busy) SetOpenRouterModel(n);
                    break;
                case "orBalance":
                    RefreshBalance(true);
                    break;
                case "download":
                    DownloadModel();
                    break;
                case "test":
                    RunTest();
                    break;
                case "cuda":
                    DownloadCuda();
                    break;

                case "gpu":
                    chkGpu.Checked = arg == "1";
                    break;
                case "autoSend":
                    chkAutoSend.Checked = arg == "1";
                    break;
                case "enter":
                    chkEnter.Checked = arg == "1";
                    break;
                case "delay":
                    double d;
                    if (double.TryParse(arg.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        numDelay.Value = (decimal)Math.Round(Math.Max(0, Math.Min(10, d)), 2);
                    break;
                case "type":
                    chkType.Checked = arg == "1";
                    break;
                case "topmost":
                    chkTopMost.Checked = arg == "toggle" ? !chkTopMost.Checked : arg == "1";
                    break;

                case "hotkeyCapture":
                    // пока поле клавиши в фокусе, клавиша отключена, чтобы её можно было «поймать»
                    if (arg == "1")
                    {
                        Unregister(slotDictate);
                    }
                    else if (!slotDictate.Registered)
                    {
                        string err;
                        RegisterSlot(slotDictate, slotDictate.Saved, out err);
                    }
                    break;
                case "hotkeyKey":
                    string[] p = arg.Split(',');
                    int vk, mods;
                    if (p.Length == 2 && int.TryParse(p[0], out vk) && int.TryParse(p[1], out mods))
                    {
                        Keys k = (Keys)vk;
                        if (k != Keys.None && !HotkeyUtil.IsModifierKey(k))
                            slotDictate.Box.Text = HotkeyUtil.Format((uint)mods, k);
                    }
                    break;
                case "hotkeySave":
                    if (slotDictate.Box.Text.Trim() != slotDictate.Saved) SaveHotkey(slotDictate);
                    break;
                case "hotkeyRevert":
                    slotDictate.Box.Text = slotDictate.Saved;
                    break;

                case "theme":
                    settings.Theme = arg == "dark" ? "dark" : "light";
                    SaveSettings();
                    ApplyWindowTheme();
                    if (fileForm != null && !fileForm.IsDisposed) fileForm.ApplyTheme();
                    break;
                case "files":
                    OpenFileWindow(null);
                    break;

                case "reset":
                    ResetSettings();
                    break;
                case "copyLog":
                    var sb = new StringBuilder();
                    foreach (string[] l in logHistory) sb.Append(l[0]).Append("  ").Append(l[1]).AppendLine();
                    if (sb.Length > 0) SetClipboard(sb.ToString());
                    break;
                case "folder":
                    try
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + WhisperEngine.BaseDir.TrimEnd('\\') + "\"")
                        {
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        Log(L.T("Не удалось открыть папку: ", "Could not open the folder: ") + ex.Message);
                    }
                    break;
                case "open":
                    OpenUrl(arg);
                    break;

                case "drag":
                    // тянем окно за любое пустое место страницы, как за заголовок
                    Point cur = MousePosition;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION,
                        (IntPtr)((cur.Y << 16) | (cur.X & 0xFFFF)));
                    break;
                case "minimize":
                    HideToTray();
                    break;
                case "close":
                    Close();
                    return;
            }
            WebPushState();
        }

        // ================================================================ окно «Распознать файл»

        private FileForm fileForm;

        /// <summary>Модель для файлов: файлы распознаёт только Whisper, поэтому при GigaAM берём скачанную модель Whisper.</summary>
        internal WhisperModel CurrentModel
        {
            get
            {
                WhisperModel sel = SelectedModel();
                if (sel == null || !sel.IsGigaAm) return sel;
                WhisperModel any = null;
                foreach (object o in cmbModel.Items)
                {
                    var m = o as WhisperModel;
                    if (m == null || m.IsGigaAm) continue;
                    if (m.Exists) return m;
                    if (any == null) any = m;
                }
                return any;
            }
        }

        internal Settings AppSettings
        {
            get { return settings; }
        }

        internal bool IsDarkTheme
        {
            get { return DarkTheme; }
        }

        internal void LogExternal(string msg)
        {
            Log(msg);
        }

        /// <summary>Из окна файлов: показать главное окно сразу на экране настроек (там выбирается модель).</summary>
        internal void ShowMainSettings()
        {
            ShowFromTray();
            WebPost(new Js().S("type", "open").S("what", "settings").ToString());
        }

        /// <summary>Путь к файлу, перетащенному на страницу (postMessageWithAdditionalObjects).</summary>
        internal static string DroppedPath(CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                if (e.AdditionalObjects == null) return null;
                foreach (object o in e.AdditionalObjects)
                {
                    var f = o as CoreWebView2File;
                    if (f != null && File.Exists(f.Path)) return f.Path;
                }
            }
            catch
            {
                // старый WebView2 без этой возможности
            }
            return null;
        }

        /// <summary>Открыть (или показать) окно распознавания файлов; path — сразу выбрать этот файл.</summary>
        internal void OpenFileWindow(string path)
        {
            if (fileForm == null || fileForm.IsDisposed)
            {
                fileForm = new FileForm(this);
                fileForm.Show();
            }
            else
            {
                fileForm.Show();
                if (fileForm.WindowState == FormWindowState.Minimized) fileForm.WindowState = FormWindowState.Normal;
                fileForm.Activate();
            }
            if (path != null) fileForm.SetFile(path);
        }

        /// <summary>Кнопка «Сбросить» в настройках: всё как после установки.</summary>
        private void ResetSettings()
        {
            if (busy || recording || downloading)
            {
                Log(L.T("Сначала дождитесь окончания записи, распознавания или скачивания.", "Wait for recording, recognition or download to finish first."));
                return;
            }
            var d = new Settings();
            chkGpu.Checked = d.UseGpu;
            chkAutoSend.Checked = d.AutoSend;
            chkEnter.Checked = d.PressEnter;
            numDelay.Value = (decimal)d.EnterDelay;
            chkType.Checked = d.TypeText;
            chkTopMost.Checked = d.TopMost;
            settings.MicSource = d.MicSource;
            settings.Cloud = d.Cloud;   // ключ OpenRouter не стираем
            SaveSettings();
            if (cmbMic.Items.Count > 0) cmbMic.SelectedIndex = 0;
            for (int i = 0; i < cmbModel.Items.Count; i++)
            {
                var m = cmbModel.Items[i] as WhisperModel;
                if (m != null && string.Equals(m.File, d.Model, StringComparison.OrdinalIgnoreCase))
                {
                    cmbModel.SelectedIndex = i;
                    break;
                }
            }
            slotDictate.Box.Text = d.HotkeyDictate;
            if (slotDictate.Saved != d.HotkeyDictate || !slotDictate.Registered) SaveHotkey(slotDictate);
            Log(L.T("Настройки сброшены на стандартные.", "Settings reset to defaults."));
        }

        private void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) ||
                !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log(L.T("Не удалось открыть ссылку: ", "Could not open the link: ") + ex.Message);
            }
        }

        // ================================================================ маленький JSON без библиотек

        internal sealed class Js
        {
            private readonly StringBuilder sb = new StringBuilder("{");
            private bool first = true;

            private void Key(string k)
            {
                if (!first) sb.Append(',');
                first = false;
                Quote(sb, k);
                sb.Append(':');
            }

            public Js S(string k, string v)
            {
                Key(k);
                if (v == null) sb.Append("null");
                else Quote(sb, v);
                return this;
            }

            public Js B(string k, bool v)
            {
                Key(k);
                sb.Append(v ? "true" : "false");
                return this;
            }

            public Js N(string k, double v)
            {
                Key(k);
                if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
                sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture));
                return this;
            }

            public Js Raw(string k, string json)
            {
                Key(k);
                sb.Append(json);
                return this;
            }

            public override string ToString()
            {
                return sb.ToString() + "}";
            }

            public static string Arr(IEnumerable<string> items)
            {
                var a = new StringBuilder("[");
                foreach (string s in items)
                {
                    if (a.Length > 1) a.Append(',');
                    Quote(a, s ?? "");
                }
                return a.Append(']').ToString();
            }

            private static void Quote(StringBuilder b, string s)
            {
                b.Append('"');
                foreach (char ch in s)
                {
                    switch (ch)
                    {
                        case '"': b.Append("\\\""); break;
                        case '\\': b.Append("\\\\"); break;
                        case '\n': b.Append("\\n"); break;
                        case '\r': b.Append("\\r"); break;
                        case '\t': b.Append("\\t"); break;
                        default:
                            if (ch < 0x20 || ch == (char)0x2028 || ch == (char)0x2029)
                                b.Append("\\u").Append(((int)ch).ToString("x4"));
                            else
                                b.Append(ch);
                            break;
                    }
                }
                b.Append('"');
            }
        }
    }
}
