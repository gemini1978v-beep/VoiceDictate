using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace VoiceDictate
{
    /// <summary>
    /// Окно «Распознать файл»: выбрать или перетащить аудио/видео → «Распознать» → рядом с файлом
    /// появляется .txt (и, если включено, субтитры .srt). Интерфейс — ui\files.html.
    /// Страница → программа: "команда|значение". Программа → страница: JSON {type:"state", ...}.
    /// </summary>
    internal sealed class FileForm : Form
    {
        private const int WebWidth = 420;
        private const int WebHeight = 620;

        private readonly MainForm main;
        private readonly Microsoft.Web.WebView2.WinForms.WebView2 web;
        private readonly Panel cover;
        private readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
        private bool ready;
        private string lastJson;

        // задание
        private string filePath;
        private string stage = "idle";      // idle / prep / run / done / error
        private double progress = -1;
        private DateTime started;
        private CancellationTokenSource job;
        private FileTranscriber.Result result;
        private string error, errorDetails;

        // ffmpeg
        private bool ffDownloading;
        private double ffFrac = -1, ffMb;
        private CancellationTokenSource ffCts;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public FileForm(MainForm main)
        {
            this.main = main;
            Text = "Распознать файл — Голосовой ввод";
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(LogicalToDeviceUnits(WebWidth), LogicalToDeviceUnits(WebHeight));
            BackColor = WebUi.BackColor(main.IsDarkTheme);
            Icon = main.Icon;
            TopMost = main.TopMost;
            StartPosition = FormStartPosition.Manual;
            Location = PickLocation();

            cover = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
            web = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            web.DefaultBackgroundColor = BackColor;
            Controls.Add(cover);
            Controls.Add(web);
            web.BringToFront();

            clock.Interval = 1000;
            clock.Tick += (s, e) => PushState();

            Load += (s, e) => InitWeb();
            FormClosing += (s, e) =>
            {
                if (job != null) job.Cancel();
                if (ffCts != null) ffCts.Cancel();
                clock.Stop();
            };
        }

        /// <summary>Справа от главного окна, а если не влезает — слева или по центру экрана.</summary>
        private Point PickLocation()
        {
            Rectangle area = Screen.FromControl(main).WorkingArea;
            int gap = LogicalToDeviceUnits(12);
            int x = main.Right + gap;
            if (x + Width > area.Right) x = main.Left - gap - Width;
            if (x < area.Left) x = area.Left + (area.Width - Width) / 2;
            int y = Math.Max(area.Top, Math.Min(main.Top, area.Bottom - Height));
            return new Point(x, y);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;          // CS_DROPSHADOW
                cp.Style |= 0x00020000 | 0x00080000;  // WS_MINIMIZEBOX | WS_SYSMENU
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WebUi.StyleWindow(Handle, main.IsDarkTheme);
        }

        /// <summary>Тему переключили в главном окне.</summary>
        public void ApplyTheme()
        {
            BackColor = WebUi.BackColor(main.IsDarkTheme);
            cover.BackColor = BackColor;
            try
            {
                web.DefaultBackgroundColor = BackColor;
            }
            catch
            {
                // не важно
            }
            if (IsHandleCreated) WebUi.StyleWindow(Handle, main.IsDarkTheme);
            PushState();
        }

        private async void InitWeb()
        {
            try
            {
                CoreWebView2Environment env = await WebUi.GetEnvironmentAsync();
                if (IsDisposed) return;
                await web.EnsureCoreWebView2Async(env);
                if (IsDisposed) return;
                CoreWebView2 core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.WebMessageReceived += OnWebMessage;
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.NavigateToString(WebUi.LoadHtml("files.html", main.IsDarkTheme));
            }
            catch (Exception ex)
            {
                main.LogExternal("Окно файлов не открылось: " + ex.Message);
                BeginInvoke((Action)Close);
            }
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
            if (msg == "drop")
            {
                string path = MainForm.DroppedPath(e);
                if (path != null) BeginInvoke((Action)(() => SetFile(path)));
                return;
            }
            BeginInvoke((Action)(() => Command(msg)));
        }

        private void Command(string msg)
        {
            if (IsDisposed) return;
            int bar = msg.IndexOf('|');
            string cmd = bar < 0 ? msg : msg.Substring(0, bar);
            string arg = bar < 0 ? "" : msg.Substring(bar + 1);
            switch (cmd)
            {
                case "ready":
                    ready = true;
                    lastJson = null;
                    break;
                case "pick":
                    PickFile();
                    break;
                case "clearFile":
                    if (!Running)
                    {
                        filePath = null;
                        stage = "idle";
                    }
                    break;
                case "srt":
                    main.AppSettings.FileSrt = arg == "1";
                    SaveSettings();
                    break;
                case "start":
                    StartJob();
                    break;
                case "cancel":
                    if (job != null) job.Cancel();
                    break;
                case "again":
                    if (!Running)
                    {
                        stage = "idle";
                        result = null;
                        error = null;
                    }
                    break;
                case "openTxt":
                    if (result != null) OpenPath(result.TxtPath);
                    break;
                case "openSrt":
                    if (result != null) OpenPath(result.SrtPath);
                    break;
                case "folder":
                    if (result != null && result.TxtPath != null)
                        StartProcess("explorer.exe", "/select,\"" + result.TxtPath + "\"");
                    break;
                case "copy":
                    if (result != null && result.Text.Length > 0)
                    {
                        try
                        {
                            Clipboard.SetDataObject(result.Text, true, 10, 50);
                        }
                        catch
                        {
                            // буфер занят
                        }
                    }
                    break;
                case "ffmpeg":
                    DownloadFfmpeg();
                    break;
                case "settings":
                    main.ShowMainSettings();
                    break;

                case "drag":
                    Point cur = MousePosition;
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */,
                        (IntPtr)((cur.Y << 16) | (cur.X & 0xFFFF)));
                    break;
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "close":
                    Close();
                    return;
            }
            PushState();
        }

        private bool Running
        {
            get { return stage == "prep" || stage == "run"; }
        }

        private void PickFile()
        {
            if (Running) return;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Выберите аудио или видео";
                dlg.Filter = FileTranscriber.DialogFilter;
                dlg.CheckFileExists = true;
                if (filePath != null) dlg.InitialDirectory = Path.GetDirectoryName(filePath);
                if (dlg.ShowDialog(this) == DialogResult.OK) SetFile(dlg.FileName);
            }
        }

        /// <summary>Выбрать файл (из диалога, перетаскиванием или с главного окна).</summary>
        public void SetFile(string path)
        {
            if (Running || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            filePath = path;
            stage = "idle";
            result = null;
            error = null;
            PushState();
        }

        private async void StartJob()
        {
            if (Running || filePath == null) return;
            WhisperModel model = main.CurrentModel;
            if (model == null || !model.Exists)
            {
                Fail("Модель Whisper не скачана — скачайте её в настройках главного окна.", null);
                return;
            }
            if (!FileTranscriber.CanRead(filePath))
            {
                Fail("Для этого формата нужен ffmpeg — нажмите «Скачать ffmpeg».", null);
                return;
            }

            job = new CancellationTokenSource();
            stage = "prep";
            progress = -1;
            started = DateTime.Now;
            result = null;
            error = null;
            clock.Start();
            PushState();
            main.LogExternal("Файл: распознаю «" + Path.GetFileName(filePath) + "» моделью " + model.File + "…");

            Settings st = main.AppSettings;
            try
            {
                FileTranscriber.Result res = await FileTranscriber.TranscribeAsync(filePath, model.FullPath,
                    st.Language, st.Prompt, st.FileSrt,
                    (stg, frac) => OnUi(() =>
                    {
                        if (!Running) return;
                        stage = stg;
                        progress = frac;
                        PushState();
                    }),
                    job.Token);
                if (res.Error != null)
                {
                    Fail(res.Error, res.Details);
                    main.LogExternal("Файл: ошибка — " + res.Error);
                }
                else
                {
                    result = res;
                    stage = "done";
                    main.LogExternal("Файл: готово за " + res.Seconds.ToString("0") + " с (" + res.Device + ") → " +
                                     res.TxtPath);
                }
            }
            catch (OperationCanceledException)
            {
                stage = "idle";
                main.LogExternal("Файл: распознавание отменено.");
            }
            catch (Exception ex)
            {
                Fail(ex.Message, null);
            }
            finally
            {
                clock.Stop();
                if (job != null) job.Dispose();
                job = null;
                if (!IsDisposed)
                {
                    PushState();
                    if (stage == "done" && ActiveForm != this) FlashDone();
                }
            }
        }

        private void Fail(string msg, string details)
        {
            stage = "error";
            error = msg;
            errorDetails = details;
            PushState();
        }

        private async void DownloadFfmpeg()
        {
            if (ffDownloading || FileTranscriber.FfmpegPath != null) return;
            ffDownloading = true;
            ffFrac = -1;
            ffMb = 0;
            ffCts = new CancellationTokenSource();
            PushState();
            try
            {
                await FileTranscriber.DownloadFfmpegAsync(
                    (frac, mb) => OnUi(() =>
                    {
                        ffFrac = frac;
                        ffMb = mb;
                        PushState();
                    }),
                    m => OnUi(() => main.LogExternal(m)),
                    ffCts.Token);
                main.LogExternal("ffmpeg установлен: " + FileTranscriber.FfmpegDir);
            }
            catch (OperationCanceledException)
            {
                // окно закрыли
            }
            catch (Exception ex)
            {
                main.LogExternal(ex.Message + ". Можно вручную: скачайте ffmpeg для Windows и положите ffmpeg.exe в " +
                                 FileTranscriber.FfmpegDir);
                if (!IsDisposed) Fail("Не удалось скачать ffmpeg: " + ex.Message, null);
            }
            finally
            {
                ffDownloading = false;
                if (!IsDisposed) PushState();
            }
        }

        // ================================================================ программа → страница

        private void PushState()
        {
            if (!ready || IsDisposed || web.CoreWebView2 == null) return;
            WhisperModel model = main.CurrentModel;
            bool hasFf = FileTranscriber.FfmpegPath != null;

            var j = new MainForm.Js()
                .S("type", "state")
                .S("theme", main.IsDarkTheme ? "dark" : "light")
                .S("stage", stage)
                .N("progress", progress)
                .N("elapsed", Running || stage == "done" ? Math.Floor((DateTime.Now - started).TotalSeconds) : 0)
                .B("srt", main.AppSettings.FileSrt)
                .S("model", model != null ? model.Title.Split('—')[0].Trim().TrimEnd(',') : "")
                .B("modelOk", model != null && model.Exists)
                .S("lang", main.AppSettings.Language)
                .B("ffmpeg", hasFf)
                .B("ffDownloading", ffDownloading)
                .N("ffFrac", ffFrac)
                .N("ffMb", ffMb)
                .N("ffSize", FileTranscriber.FfmpegSizeMb)
                .S("error", error)
                .S("details", errorDetails);

            if (filePath != null)
            {
                long size = 0;
                try
                {
                    size = new FileInfo(filePath).Length;
                }
                catch
                {
                    // файл убрали
                }
                j.Raw("file", new MainForm.Js()
                    .S("name", Path.GetFileName(filePath))
                    .S("ext", Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant())
                    .N("mb", Math.Round(size / 1048576.0, 1))
                    .B("video", FileTranscriber.IsVideo(filePath))
                    .B("needFf", !FileTranscriber.IsDirect(filePath) && !hasFf)
                    .ToString());
            }
            else
            {
                j.Raw("file", "null");
            }

            if (result != null)
            {
                string text = result.Text.Length > 300000 ? result.Text.Substring(0, 300000) + "…" : result.Text;
                j.Raw("result", new MainForm.Js()
                    .S("text", text)
                    .S("txtName", result.TxtPath != null ? Path.GetFileName(result.TxtPath) : "")
                    .S("txtDir", result.TxtPath != null ? Path.GetDirectoryName(result.TxtPath) : "")
                    .S("srtName", result.SrtPath != null ? Path.GetFileName(result.SrtPath) : "")
                    .N("seconds", Math.Round(result.Seconds))
                    .N("audioSeconds", Math.Round(result.AudioSeconds))
                    .S("device", result.Device)
                    .N("words", CountWords(result.Text))
                    .ToString());
            }
            else
            {
                j.Raw("result", "null");
            }

            string json = j.ToString();
            if (json == lastJson) return;
            lastJson = json;
            try
            {
                web.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // страница перезагружается
            }
        }

        private static int CountWords(string s)
        {
            return s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        // ================================================================ прочее

        private void OnUi(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (InvalidOperationException)
            {
                // окно закрылось
            }
        }

        private void SaveSettings()
        {
            try
            {
                main.AppSettings.Save();
            }
            catch
            {
                // не страшно
            }
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                main.LogExternal("Не удалось открыть файл: " + ex.Message);
            }
        }

        private void StartProcess(string exe, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                main.LogExternal("Не удалось открыть папку: " + ex.Message);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        /// <summary>Готово, а окно не в фокусе — мигнуть кнопкой на панели задач.</summary>
        private void FlashDone()
        {
            try
            {
                var fi = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                    hwnd = Handle,
                    dwFlags = 0x3 | 0xC,   // FLASHW_ALL | FLASHW_TIMERNOFG
                    uCount = 3,
                    dwTimeout = 0,
                };
                FlashWindowEx(ref fi);
            }
            catch
            {
                // не важно
            }
        }
    }
}
