using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VoiceDictate
{
    /// <summary>
    /// Схема работы (одна клавиша, по умолчанию F3):
    ///   1-е нажатие — запись с микрофона. Индикатор ЗЕЛЁНЫЙ. Окно, в котором вы работаете, не меняется.
    ///   2-е нажатие — индикатор КРАСНЫЙ, запись распознаёт Whisper (локально, без интернета),
    ///                 текст вставляется в активное окно (или туда, где вы были перед записью).
    /// </summary>
    internal sealed partial class MainForm : Form
    {
        private const int ID_DICTATE = 0x5A12;
        private const int ID_CANCEL = 0x5A13;     // Esc во время записи — отменить её
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        private static readonly Color ColorGreen = Color.FromArgb(40, 150, 40);
        private static readonly Color ColorRed = Color.FromArgb(220, 40, 40);
        private static readonly Color ColorAmber = Color.FromArgb(200, 120, 0);
        private static readonly Color ColorGray = Color.FromArgb(70, 70, 70);

        /// <summary>Одна горячая клавиша: поле для её ввода и состояние регистрации.</summary>
        private sealed class HotkeySlot
        {
            public int Id;
            public string Saved;                       // сохранённое сочетание
            public readonly TextBox Box = new TextBox();
            public bool Registered;
            public Keys Key = Keys.None;
        }

        /// <summary>Круглая лампочка-индикатор.</summary>
        private sealed class Led : Control
        {
            private bool on;

            public Led()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Size = new Size(20, 20);
            }

            public bool On
            {
                get { return on; }
                set
                {
                    if (on == value) return;
                    on = value;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Math.Min(Width, Height) - 3;
                var r = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
                Color fill = on ? Color.FromArgb(40, 200, 60) : Color.FromArgb(220, 40, 40);
                Color edge = on ? Color.FromArgb(20, 120, 30) : Color.FromArgb(140, 20, 20);
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
                using (var p = new Pen(edge, 1.5f)) g.DrawEllipse(p, r);
                var hl = new Rectangle(r.X + d / 4, r.Y + d / 6, d / 3, d / 4);
                using (var b = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) g.FillEllipse(b, hl);
            }
        }

        private readonly Settings settings;
        private readonly System.Windows.Forms.Timer fgTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer recTimer = new System.Windows.Forms.Timer();
        private readonly HotkeySlot slotDictate = new HotkeySlot();
        private readonly Recorder recorder = new Recorder();
        private readonly WebRecorder webRecorder = new WebRecorder();   // запись «через веб» (WebRecorder.cs)
        private BrowserMic browserMic;               // запись через вкладку браузера (BrowserMic.cs)
        private bool webRecording;                   // текущая запись идёт через веб
        private bool micStarting;                    // веб-микрофон включается
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private IntPtr lastExternal = IntPtr.Zero;   // последнее активное окно другой программы
        private bool recording;                      // идёт запись (индикатор зелёный)
        private bool busy;                           // распознавание или отправка
        private bool downloading;
        private bool loadingLists;

        // --- элементы окна ---
        private readonly Led led = new Led();
        private readonly Label lblLed = new Label();
        private readonly ComboBox cmbModel = new ComboBox();
        private readonly Button btnDownload = new Button();
        private readonly Button btnTest = new Button();
        private readonly ComboBox cmbMic = new ComboBox();
        private readonly ProgressBar barLevel = new ProgressBar();
        private readonly CheckBox chkGpu = new CheckBox();
        private readonly Button btnCuda = new Button();
        private readonly CheckBox chkAutoSend = new CheckBox();
        private readonly CheckBox chkEnter = new CheckBox();
        private readonly NumericUpDown numDelay = new NumericUpDown();
        private readonly CheckBox chkType = new CheckBox();
        private readonly CheckBox chkTopMost = new CheckBox();
        private readonly Label lblStatus = new Label();
        private readonly TextBox txtText = new TextBox();
        private readonly Button btnDictate = new Button();
        private readonly Button btnSend = new Button();
        private readonly Button btnClear = new Button();
        private readonly TextBox txtLog = new TextBox();

        public MainForm()
        {
            settings = Settings.Load();
            L.En = settings.UiLang == "en";
            WhisperEngine.UseGpu = settings.UseGpu;
            slotDictate.Id = ID_DICTATE;
            slotDictate.Saved = settings.HotkeyDictate;

            BuildUi();
            if (!settings.ClassicUi) SetupWebWindow();   // HTML-интерфейс (MainForm.Web.cs)
            InitTray();                                  // значок у часов (MainForm.Tray.cs)

            fgTimer.Interval = 200;
            fgTimer.Tick += (s, e) =>
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (IsExternal(fg)) lastExternal = fg;
            };

            recTimer.Interval = 50;
            recTimer.Tick += (s, e) => OnRecTick();

            Shown += (s, e) =>
            {
                WhisperEngine.CleanWorkDir();   // если в прошлый раз программу закрыли аварийно
                fgTimer.Start();
                RegisterOrLog(slotDictate, L.T("записи", "for recording"));
                UpdateStatus();
                Log(L.T("Голосовой ввод ", "Voice Input ") + L.Version + ". " +
                    slotDictate.Saved + L.T(" — начать запись, ещё раз ", " — start recording, press ") + slotDictate.Saved +
                    L.T(" — распознать (Whisper) и отправить в активное окно.", " again — recognize and send to the active window."));
                CheckSetup();
                LoadOpenRouterModels();
                if (settings.Cloud) RefreshBalance(false);
                if (settings.MicSource == "browser" && StartBrowserMic())
                    Log(L.T("Запись через браузер: держите открытой вкладку ", "Recording via browser: keep the tab open: ") + BrowserMic.Url +
                        L.T(" (если её нет — откроется сама при первом нажатии ", " (if it is not open, it opens on the first press of ") + slotDictate.Saved + ").");
            };
            FormClosing += (s, e) =>
            {
                closing.Cancel();
                fgTimer.Stop();
                recTimer.Stop();
                if (webRecording && webRecorder.Target != null)
                    webRecorder.Target(new Js().S("type", "mic").S("cmd", "stop").ToString());
                if (browserMic != null) browserMic.Dispose();
                try
                {
                    recorder.Dispose();
                }
                catch
                {
                    // закрываемся
                }
                Unregister(slotDictate);
            };
        }

        private static bool IsExternal(IntPtr hWnd)
        {
            return hWnd != IntPtr.Zero && !Native.IsOwnWindow(hWnd);
        }

        /// <summary>При запуске: есть ли whisper и модель.</summary>
        private void CheckSetup()
        {
            if (!File.Exists(WhisperEngine.CliPath))
                Log(L.T("Не найден whisper: ", "whisper not found: ") + WhisperEngine.CliPath +
                    L.T(". Папка whisper из архива должна лежать рядом с VoiceDictate.exe.", ". The whisper folder from the archive must be next to VoiceDictate.exe."));
            var m = SelectedModel();
            if (!settings.Cloud && m != null && !m.Exists)
                Log(L.T("Модель «", "Model “") + m.File + L.T("» ещё не скачана. Нажмите «Скачать» (~", "” is not downloaded yet. Click “Download” (~") + m.SizeMb +
                    L.T(" МБ, один раз), потом «Проверить».", " MB, once), then “Test”."));
            if (WhisperEngine.AsciiPath(WhisperEngine.BaseDir) == null)
                Log(L.T("Внимание: в пути к программе есть русские буквы. Если whisper не сможет открыть файлы, ", "Warning: the program path contains non-Latin letters. If whisper cannot open files, ") +
                    L.T("переложите папку программы, например, в C:\\VoiceDictate.", "move the program folder, for example, to C:\\VoiceDictate."));
        }

        // ================================================================ интерфейс

        private void BuildUi()
        {
            Text = L.T("Голосовой ввод ", "Voice Input ") + L.Version + " (Whisper)";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 740);
            MinimumSize = new Size(600, 620);
            TopMost = settings.TopMost;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(10),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            root.Controls.Add(MakeHotkeyRow(slotDictate, L.T("Записать / отправить:", "Record / send:")));
            root.Controls.Add(MakeHint(L.T("1-е нажатие — запись с микрофона (зелёный), 2-е — распознать и отправить (красный). ", "1st press — record from the microphone (green), 2nd — recognize and send (red). ") +
                                       L.T("Сменить клавишу: щёлкните в поле, нажмите сочетание и «Сохранить».", "To change the key: click the field, press a combination and “Save”.")));

            // Модель
            var rowModel = MakeFlow();
            rowModel.Controls.Add(MakeRowLabel(L.T("Модель:", "Model:")));
            cmbModel.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbModel.Width = 300;
            cmbModel.DropDownWidth = 360;
            cmbModel.Margin = new Padding(3, 4, 3, 3);
            cmbModel.SelectedIndexChanged += (s, e) =>
            {
                if (loadingLists) return;
                var m = SelectedModel();
                if (m == null) return;
                settings.Model = m.File;
                SaveSettings();
                Log(L.T("Модель: ", "Model: ") + m.File + (m.Exists ? "" : L.T(" (не скачана — нажмите «Скачать»)", " (not downloaded — click “Download”)")));
            };
            rowModel.Controls.Add(cmbModel);
            btnDownload.Text = L.T("Скачать", "Download");
            btnDownload.AutoSize = true;
            btnDownload.Click += (s, e) => DownloadModel();
            rowModel.Controls.Add(btnDownload);
            btnTest.Text = L.T("Проверить", "Test");
            btnTest.AutoSize = true;
            btnTest.Click += (s, e) => RunTest();
            rowModel.Controls.Add(btnTest);
            root.Controls.Add(rowModel);

            // Микрофон
            var rowMic = MakeFlow();
            rowMic.Controls.Add(MakeRowLabel(L.T("Микрофон:", "Microphone:")));
            cmbMic.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMic.Width = 300;
            cmbMic.Margin = new Padding(3, 4, 3, 3);
            cmbMic.SelectedIndexChanged += (s, e) =>
            {
                if (loadingLists) return;
                settings.MicDevice = cmbMic.SelectedIndex - 1;
                SaveSettings();
            };
            rowMic.Controls.Add(cmbMic);
            barLevel.Width = 150;
            barLevel.Height = 16;
            barLevel.Maximum = 100;
            barLevel.Margin = new Padding(6, 8, 3, 3);
            rowMic.Controls.Add(barLevel);
            root.Controls.Add(rowMic);

            // Видеокарта NVIDIA
            var rowGpu = MakeFlow();
            chkGpu.Text = L.T("Видеокарта NVIDIA (CUDA)", "NVIDIA GPU (CUDA)");
            chkGpu.AutoSize = true;
            chkGpu.Margin = new Padding(3, 6, 3, 3);
            chkGpu.Checked = settings.UseGpu;
            chkGpu.CheckedChanged += (s, e) =>
            {
                settings.UseGpu = chkGpu.Checked;
                WhisperEngine.UseGpu = chkGpu.Checked;
                SaveSettings();
                Log(chkGpu.Checked
                    ? (WhisperEngine.CudaInstalled ? L.T("Распознавание на видеокарте.", "Recognition on the GPU.") : L.T("Видеокарта включена, но ускорение не скачано — нажмите кнопку справа.", "GPU is on, but acceleration is not downloaded — click the button on the right."))
                    : L.T("Распознавание на процессоре.", "Recognition on the CPU."));
            };
            rowGpu.Controls.Add(chkGpu);
            btnCuda.AutoSize = true;
            btnCuda.Click += (s, e) => DownloadCuda();
            rowGpu.Controls.Add(btnCuda);
            root.Controls.Add(rowGpu);

            chkAutoSend.Text = L.T("Сразу отправлять распознанный текст в активное окно", "Send recognized text to the active window right away");
            chkAutoSend.AutoSize = true;
            chkAutoSend.Checked = settings.AutoSend;
            chkAutoSend.CheckedChanged += (s, e) => { settings.AutoSend = chkAutoSend.Checked; SaveSettings(); };
            root.Controls.Add(chkAutoSend);

            // Enter + задержка
            var rowEnter = MakeFlow();
            chkEnter.Text = L.T("Нажимать Enter после вставки, задержка (сек):", "Press Enter after pasting, delay (s):");
            chkEnter.AutoSize = true;
            chkEnter.Margin = new Padding(3, 5, 3, 3);
            chkEnter.Checked = settings.PressEnter;
            chkEnter.CheckedChanged += (s, e) =>
            {
                settings.PressEnter = chkEnter.Checked;
                numDelay.Enabled = chkEnter.Checked;
                SaveSettings();
            };
            numDelay.DecimalPlaces = 2;
            numDelay.Increment = 0.1m;
            numDelay.Minimum = 0m;
            numDelay.Maximum = 10m;
            numDelay.Width = 70;
            numDelay.Value = (decimal)Math.Max(0, Math.Min(10, settings.EnterDelay));
            numDelay.Enabled = settings.PressEnter;
            numDelay.ValueChanged += (s, e) => { settings.EnterDelay = (double)numDelay.Value; SaveSettings(); };
            rowEnter.Controls.Add(chkEnter);
            rowEnter.Controls.Add(numDelay);
            root.Controls.Add(rowEnter);

            chkType.Text = L.T("Печатать посимвольно (если Ctrl+V не работает)", "Type character by character (if Ctrl+V does not work)");
            chkType.AutoSize = true;
            chkType.Checked = settings.TypeText;
            chkType.CheckedChanged += (s, e) => { settings.TypeText = chkType.Checked; SaveSettings(); };
            root.Controls.Add(chkType);

            chkTopMost.Text = L.T("Окно поверх остальных", "Window always on top");
            chkTopMost.AutoSize = true;
            chkTopMost.Checked = settings.TopMost;
            chkTopMost.CheckedChanged += (s, e) =>
            {
                settings.TopMost = chkTopMost.Checked;
                TopMost = settings.TopMost;
                SaveSettings();
            };
            root.Controls.Add(chkTopMost);

            lblStatus.AutoSize = false;
            lblStatus.Height = 46;
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            root.Controls.Add(lblStatus);

            root.Controls.Add(MakeLabel(L.T("Распознанный текст (можно поправить руками):", "Recognized text (you can edit it):")));

            txtText.Multiline = true;
            txtText.ScrollBars = ScrollBars.Vertical;
            txtText.Dock = DockStyle.Fill;
            txtText.Font = new Font("Segoe UI", 11f);
            root.Controls.Add(txtText);

            var rowButtons = MakeFlow();
            btnDictate.AutoSize = true;
            btnDictate.Click += (s, e) => Toggle();
            btnSend.AutoSize = true;
            btnSend.Text = L.T("Отправить", "Send");
            btnSend.Click += (s, e) => SendNow();
            btnClear.AutoSize = true;
            btnClear.Text = L.T("Очистить", "Clear");
            btnClear.Click += (s, e) => txtText.Clear();
            rowButtons.Controls.Add(btnDictate);
            rowButtons.Controls.Add(btnSend);
            rowButtons.Controls.Add(btnClear);
            root.Controls.Add(rowButtons);

            root.Controls.Add(MakeLabel(L.T("Журнал:", "Log:")));

            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Dock = DockStyle.Fill;
            txtLog.BackColor = Color.FromArgb(30, 30, 30);
            txtLog.ForeColor = Color.FromArgb(212, 212, 212);
            txtLog.Font = new Font("Consolas", 9f);
            root.Controls.Add(txtLog);

            // Высоты строк: всё по содержимому, кроме текста и журнала
            int textRow = root.Controls.GetChildIndex(txtText);
            int logRow = root.Controls.GetChildIndex(txtLog);
            root.RowCount = root.Controls.Count;
            for (int i = 0; i < root.RowCount; i++)
            {
                if (i == textRow) root.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
                else if (i == logRow) root.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
                else root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            for (int i = 0; i < root.Controls.Count; i++)
                root.SetRow(root.Controls[i], i);

            Controls.Add(root);
            classicRoot = root;
            FillModels();
            FillMics();
            SetRecording(false);
        }

        private void FillModels()
        {
            loadingLists = true;
            try
            {
                cmbModel.Items.Clear();
                List<WhisperModel> list = WhisperEngine.ListModels();
                int sel = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    cmbModel.Items.Add(list[i]);
                    if (string.Equals(list[i].File, settings.Model, StringComparison.OrdinalIgnoreCase)) sel = i;
                }
                if (cmbModel.Items.Count > 0) cmbModel.SelectedIndex = sel;
                var m = SelectedModel();
                if (m != null) settings.Model = m.File;
            }
            finally
            {
                loadingLists = false;
            }
        }

        private void FillMics()
        {
            loadingLists = true;
            try
            {
                cmbMic.Items.Clear();
                cmbMic.Items.Add(L.T("По умолчанию (как в Windows)", "Default (as in Windows)"));
                foreach (string name in Recorder.GetDevices()) cmbMic.Items.Add(name);
                int idx = settings.MicDevice + 1;
                cmbMic.SelectedIndex = idx >= 0 && idx < cmbMic.Items.Count ? idx : 0;
            }
            finally
            {
                loadingLists = false;
            }
        }

        private WhisperModel SelectedModel()
        {
            return cmbModel.SelectedItem as WhisperModel;
        }

        private FlowLayoutPanel MakeHotkeyRow(HotkeySlot slot, string title)
        {
            var row = MakeFlow();

            led.Margin = new Padding(3, 5, 6, 3);
            row.Controls.Add(led);

            var label = MakeLabel(title);
            label.AutoSize = false;
            label.Width = 160;
            label.Height = 24;
            row.Controls.Add(label);

            TextBox box = slot.Box;
            box.Width = 120;
            box.ReadOnly = true;
            box.BackColor = SystemColors.Window;
            box.ShortcutsEnabled = false;
            box.Text = slot.Saved;
            box.PreviewKeyDown += (s, e) => e.IsInputKey = true;
            box.KeyDown += (s, e) =>
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (HotkeyUtil.IsModifierKey(e.KeyCode)) return;
                uint mods = 0;
                if (e.Control) mods |= HotkeyUtil.MOD_CONTROL;
                if (e.Alt) mods |= HotkeyUtil.MOD_ALT;
                if (e.Shift) mods |= HotkeyUtil.MOD_SHIFT;
                box.Text = HotkeyUtil.Format(mods, e.KeyCode);
            };
            // Пока поле в фокусе, клавиша отключена, чтобы её можно было «поймать»
            box.Enter += (s, e) => Unregister(slot);
            box.Leave += (s, e) =>
            {
                if (!slot.Registered)
                {
                    string err;
                    RegisterSlot(slot, slot.Saved, out err);
                }
            };
            row.Controls.Add(box);

            var btn = new Button { Text = L.T("Сохранить", "Save"), AutoSize = true };
            btn.Click += (s, e) => SaveHotkey(slot);
            row.Controls.Add(btn);

            lblLed.AutoSize = true;
            lblLed.Margin = new Padding(8, 7, 3, 3);
            lblLed.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            row.Controls.Add(lblLed);
            return row;
        }

        private static FlowLayoutPanel MakeFlow()
        {
            return new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2),
            };
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        }

        private static Label MakeRowLabel(string text)
        {
            return new Label { Text = text, AutoSize = false, Width = 130, Height = 24, Margin = new Padding(3, 7, 3, 3) };
        }

        private static Label MakeHint(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = Color.Gray,
                Margin = new Padding(3, 0, 3, 6),
            };
        }

        private void Log(string msg)
        {
            if (IsDisposed) return;
            string time = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText(time + "  " + msg + Environment.NewLine);
            WebLog(time, msg);
        }

        private void SetStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
            WebPushState();
        }

        private void SetRecording(bool on)
        {
            recording = on;
            led.On = on;
            lblLed.Text = on ? L.T("идёт запись", "recording") : L.T("ожидание", "idle");
            lblLed.ForeColor = on ? Color.FromArgb(30, 150, 50) : ColorRed;
            if (!on) barLevel.Value = 0;
            SetCancelKey(on);
            UpdateStatus();
        }

        /// <summary>Esc ловим только пока идёт запись, чтобы в остальное время он работал в других программах.</summary>
        private void SetCancelKey(bool on)
        {
            if (!IsHandleCreated) return;
            if (on && slotDictate.Key != Keys.Escape)
                Native.RegisterHotKey(Handle, ID_CANCEL, HotkeyUtil.MOD_NOREPEAT, (uint)Keys.Escape);
            else
                Native.UnregisterHotKey(Handle, ID_CANCEL);
        }

        private void UpdateStatus()
        {
            UpdateStatusCore();
            WebPushState();
            UpdateTray();
        }

        private void UpdateStatusCore()
        {
            string k = slotDictate.Saved;
            btnDictate.Text = recording ? L.T("Стоп и распознать (", "Stop and recognize (") + k + ")" : L.T("Записать (", "Record (") + k + ")";
            btnDictate.Enabled = !busy;
            btnSend.Enabled = !busy && !recording;
            btnClear.Enabled = !busy;
            btnTest.Enabled = !busy && !recording && !downloading;
            btnDownload.Enabled = !downloading;
            btnCuda.Enabled = !downloading && !busy && !recording;
            btnCuda.Text = WhisperEngine.CudaInstalled
                ? L.T("Ускорение NVIDIA установлено ✓", "NVIDIA acceleration installed ✓")
                : L.T("Скачать ускорение NVIDIA (~", "Download NVIDIA acceleration (~") + WhisperEngine.CudaSizeMb + L.T(" МБ)", " MB)");
            cmbModel.Enabled = !busy && !recording && !downloading;
            cmbMic.Enabled = !recording;
            if (busy || downloading) return;
            if (recording)
                SetStatus(L.T("Идёт запись ", "Recording ") + FormatTime(RecSeconds) + ". " + k + L.T(" — распознать и отправить, Esc — отмена", " — recognize and send, Esc — cancel"),
                    ColorGreen);
            else
                SetStatus(L.T("Готов. ", "Ready. ") + k + L.T(" — начать запись", " — start recording"), ColorGray);
        }

        private static string FormatTime(double sec)
        {
            int s = (int)sec;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        // ================================================================ горячая клавиша

        private bool RegisterSlot(HotkeySlot slot, string text, out string error)
        {
            error = null;
            uint mods;
            Keys key;
            if (!HotkeyUtil.TryParse(text, out mods, out key))
            {
                error = L.T("не удалось разобрать сочетание", "could not parse the key combination");
                return false;
            }
            Unregister(slot);
            if (!Native.RegisterHotKey(Handle, slot.Id, mods | HotkeyUtil.MOD_NOREPEAT, (uint)key))
            {
                int code = Marshal.GetLastWin32Error();
                error = code == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? L.T("сочетание уже занято другой программой", "the combination is already used by another program")
                    : L.T("ошибка Windows ", "Windows error ") + code;
                return false;
            }
            slot.Registered = true;
            slot.Key = key;
            return true;
        }

        private void Unregister(HotkeySlot slot)
        {
            if (!slot.Registered) return;
            Native.UnregisterHotKey(Handle, slot.Id);
            slot.Registered = false;
        }

        private void RegisterOrLog(HotkeySlot slot, string what)
        {
            string err;
            if (!RegisterSlot(slot, slot.Saved, out err))
                Log(L.T("Клавиша ", "Hotkey ") + what + " «" + slot.Saved + L.T("» не зарегистрирована: ", "” is not registered: ") + err + L.T(". Задайте другую.", ". Choose another one."));
        }

        private void SaveHotkey(HotkeySlot slot)
        {
            string wanted = slot.Box.Text.Trim();

            uint m;
            Keys k;
            string err = null;
            bool ok = HotkeyUtil.TryParse(wanted, out m, out k);
            if (!ok)
            {
                err = L.T("не удалось разобрать сочетание", "could not parse the key combination");
            }
            else
            {
                wanted = HotkeyUtil.Format(m, k);
                ok = RegisterSlot(slot, wanted, out err);
            }

            if (ok)
            {
                slot.Saved = wanted;
                slot.Box.Text = wanted;
                settings.HotkeyDictate = wanted;
                SaveSettings();
                Log(L.T("Клавиша сохранена: ", "Hotkey saved: ") + wanted);
                UpdateStatus();
            }
            else
            {
                Log(L.T("Клавиша «", "Hotkey “") + wanted + L.T("» не подходит: ", "” cannot be used: ") + err);
                MessageBox.Show(this, L.T("Клавиша «", "Hotkey “") + wanted + L.T("» не подходит: ", "” cannot be used: ") + err,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                string err2;
                RegisterSlot(slot, slot.Saved, out err2);
                slot.Box.Text = slot.Saved;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == ID_DICTATE)
            {
                Toggle();
                return;
            }
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == ID_CANCEL)
            {
                CancelRecording();
                return;
            }
            base.WndProc(ref m);
        }

        // ================================================================ переключатель (F3)

        private void Toggle()
        {
            if (busy)
            {
                Log(L.T("Подождите — ещё идёт распознавание.", "Please wait — recognition is still running."));
                return;
            }
            if (micStarting) return;   // веб-микрофон ещё включается
            if (!recording) StartRecording();
            else StopAndRecognize();
        }

        // ---------------------------------------------------------------- 1-е нажатие: запись

        private async void StartRecording()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (IsExternal(fg)) lastExternal = fg;

            if (!CheckEngineReady()) return;

            bool viaBrowser = settings.MicSource == "browser";
            bool viaWeb = viaBrowser || (settings.MicSource == "web" && webMode && webReady);
            if (settings.MicSource == "web" && !viaWeb)
                Log(L.T("Запись через веб работает только в новом окне программы, а оно не загружено — пишу напрямую.", "Web recording works only in the new program window, which is not loaded — recording directly."));
            if (viaBrowser)
            {
                if (!StartBrowserMic())
                {
                    SetStatus(L.T("Микрофон недоступен", "Microphone unavailable"), ColorRed);
                    return;
                }
                if (!browserMic.Connected)
                {
                    OpenBrowserMicPage();
                    Log(L.T("Открыл в браузере страницу микрофона ", "Opened the microphone page in the browser: ") + BrowserMic.Url + L.T(". Нажмите там «Подключить ", ". Click “Connect ") +
                        L.T("микрофон», разрешите доступ, оставьте вкладку открытой и снова нажмите ", "microphone” there, allow access, keep the tab open and press again: ") + slotDictate.Saved + ".");
                    SetStatus(L.T("Подключите микрофон в браузере", "Connect the microphone in the browser"), ColorRed);
                    return;
                }
                webRecorder.Target = browserMic.Send;
            }
            else
            {
                webRecorder.Target = WebPost;
            }
            try
            {
                if (viaWeb)
                {
                    micStarting = true;
                    SetStatus(L.T("Включаю микрофон…", "Turning on the microphone…"), ColorAmber);
                    await webRecorder.Start(cmbMic.SelectedIndex > 0 ? cmbMic.Text : "");
                    if (IsDisposed) return;
                }
                else
                {
                    recorder.Start(settings.MicDevice);
                }
            }
            catch (Exception ex)
            {
                Log(L.T("Не удалось начать запись", "Could not start recording") + (viaBrowser ? L.T(" через браузер", " via browser") : viaWeb ? L.T(" через веб", " via web") : "") + ": " +
                    ex.Message);
                if (!viaBrowser)
                    Log(L.T("Если микрофон блокирует антивирус (например, Касперский), выберите в настройках ", "If an antivirus (e.g. Kaspersky) blocks the microphone, choose in Settings ") +
                        L.T("«Запись звука: через браузер».", "“Audio capture: via browser”."));
                SetStatus(L.T("Микрофон недоступен", "Microphone unavailable"), ColorRed);
                return;
            }
            finally
            {
                micStarting = false;
            }
            webRecording = viaWeb;
            LiveStart();   // MainForm.Live.cs
            recTimer.Start();
            SetRecording(true);
            string mic = viaWeb && !string.IsNullOrEmpty(webRecorder.DeviceName) ? webRecorder.DeviceName : cmbMic.Text;
            Log(L.T("Запись началась", "Recording started") + (viaBrowser ? L.T(" через браузер", " via browser") : viaWeb ? L.T(" через веб", " via web") : "") + " (" + mic + L.T("). Говорите, потом ", "). Speak, then press ") +
                slotDictate.Saved + "." + (live != null ? L.T(" Фразы вставляются после каждой паузы.", " Phrases are inserted after each pause.") : ""));
        }

        /// <summary>Настройка «Запись звука»: direct / web / browser.</summary>
        private void SetMicSource(string src)
        {
            src = src == "web" || src == "browser" ? src : "direct";
            settings.MicSource = src;
            SaveSettings();
            if (src == "browser")
            {
                Log(L.T("Запись звука: через браузер. Сейчас откроется страница ", "Audio capture: via browser. The page will open now: ") + BrowserMic.Url +
                    L.T(" — нажмите там «Подключить микрофон» и оставьте вкладку открытой.", " — click “Connect microphone” there and keep the tab open."));
                if (StartBrowserMic()) OpenBrowserMicPage();
            }
            else
            {
                Log(src == "web"
                    ? L.T("Запись звука: через веб (микрофон открывает движок окна программы).", "Audio capture: via web (the program window engine opens the microphone).")
                    : L.T("Запись звука: напрямую.", "Audio capture: direct."));
            }
        }

        /// <summary>Запустить маленький сервер для вкладки браузера (один раз).</summary>
        private bool StartBrowserMic()
        {
            if (browserMic != null) return true;
            var mic = new BrowserMic(msg =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke((Action)(() => OnBrowserMicMessage(msg)));
                }
                catch (InvalidOperationException)
                {
                    // окно закрывается
                }
            });
            try
            {
                mic.Start();
            }
            catch (Exception ex)
            {
                mic.Dispose();
                Log(L.T("Не удалось запустить запись через браузер: ", "Could not start browser recording: ") + ex.Message);
                return false;
            }
            browserMic = mic;
            return true;
        }

        private void OpenBrowserMicPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(BrowserMic.Url + "?lang=" + L.Code) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log(L.T("Не удалось открыть браузер: ", "Could not open the browser: ") + ex.Message + L.T(". Откройте вручную: ", ". Open it manually: ") + BrowserMic.Url);
            }
        }

        private void OnBrowserMicMessage(string msg)
        {
            if (IsDisposed) return;
            if (msg.StartsWith("hello", StringComparison.Ordinal))
            {
                Log(L.T("Браузер подключил микрофон. Вкладку не закрывайте — ", "The browser connected the microphone. Keep the tab open — ") + slotDictate.Saved + L.T(" начинает запись.", " starts recording."));
                return;
            }
            OnWebMicMessage(msg);
        }

        private double RecSeconds
        {
            get { return webRecording ? webRecorder.Seconds : recorder.Seconds; }
        }

        /// <summary>Звук и ответы от страницы при записи через веб.</summary>
        private void OnWebMicMessage(string msg)
        {
            int bar = msg.IndexOf('|');
            string cmd = bar < 0 ? msg : msg.Substring(0, bar);
            string arg = bar < 0 ? "" : msg.Substring(bar + 1);
            string error = webRecorder.OnMessage(cmd, arg);
            if (error != null) Log(L.T("Микрофон (через веб): ", "Microphone (via web): ") + error);
        }

        private void OnRecTick()
        {
            if (!recording) return;
            float level = webRecorder.LastLevel;
            if (!webRecording)
            {
                try
                {
                    recorder.Poll();
                }
                catch (Exception ex)
                {
                    Log(L.T("Ошибка записи: ", "Recording error: ") + ex.Message);
                }
                level = recorder.LastLevel;
            }
            LiveTick();
            barLevel.Value = Math.Max(0, Math.Min(100, (int)(Math.Sqrt(level) * 100)));
            WebLevel(level);
            UpdateStatus();
        }

        /// <summary>Esc: выключить микрофон и выбросить запись, ничего не распознавая и не вставляя.</summary>
        private async void CancelRecording()
        {
            if (!recording || busy) return;
            busy = true;
            recordGen++;   // фразы, которые ещё распознаются, не вставятся (MainForm.Live.cs)
            recTimer.Stop();
            live = null;
            try
            {
                if (webRecording) await webRecorder.Stop();
                else recorder.Stop();
            }
            catch (Exception ex)
            {
                Log(L.T("Ошибка при остановке записи: ", "Error while stopping recording: ") + ex.Message);
            }
            finally
            {
                webRecording = false;
                busy = false;
                SetRecording(false);
            }
            Log(liveInserted > 0
                ? L.T("Запись отменена (Esc). Уже вставленные фразы остались, остальное выброшено.", "Recording cancelled (Esc). Phrases already inserted stay, the rest is discarded.")
                : L.T("Запись отменена (Esc), ничего не вставлено.", "Recording cancelled (Esc), nothing inserted."));
        }

        // ---------------------------------------------------------------- 2-е нажатие: распознать и отправить

        private async void StopAndRecognize()
        {
            busy = true;
            recTimer.Stop();
            short[] samples;
            float maxLevel;
            bool viaWeb = webRecording;
            try
            {
                if (viaWeb)
                {
                    samples = await webRecorder.Stop();
                    maxLevel = webRecorder.MaxLevel;
                }
                else
                {
                    samples = recorder.Stop();
                    maxLevel = recorder.MaxLevel;
                }
            }
            catch (Exception ex)
            {
                busy = false;
                webRecording = false;
                SetRecording(false);
                Log(L.T("Ошибка при остановке записи: ", "Error while stopping recording: ") + ex.Message);
                return;
            }
            webRecording = false;
            SetRecording(false);   // красный
            PauseDetector det = live;
            live = null;

            try
            {
                if (det != null && await LiveFinish(det, samples)) return;   // MainForm.Live.cs

                double sec = samples.Length / (double)Recorder.SampleRate;
                if (sec < 0.3)
                {
                    Log(L.T("Запись слишком короткая — нечего распознавать.", "The recording is too short — nothing to recognize."));
                    return;
                }
                if (maxLevel <= 0f && viaWeb)
                {
                    Log(L.T("Микрофон прислал полную тишину и через веб. Проверьте, тот ли микрофон выбран, и что ", "The microphone sent complete silence via web too. Check that the right microphone is selected and that ") +
                        L.T("в браузере на этом компьютере микрофон работает. Если нет — доступ, скорее всего, ", "the microphone works in a browser on this computer. If not, access is most likely ") +
                        L.T("блокирует антивирус для всех программ, нужно обратиться к администратору.", "blocked by an antivirus for all programs — contact your administrator."));
                    return;
                }
                if (maxLevel <= 0f)
                {
                    Log(L.T("Микрофон прислал полную тишину. В Windows 10/11 проверьте: Параметры → Конфиденциальность → ", "The microphone sent complete silence. In Windows 10/11 check: Settings → Privacy → ") +
                        L.T("Микрофон → «Разрешить приложениям доступ к микрофону» и «Разрешить классическим ", "Microphone → “Allow apps to access your microphone” and “Allow desktop ") +
                        L.T("приложениям…». И выберите правильный микрофон в списке. Если звук режет антивирус ", "apps…”. And pick the right microphone in the list. If an antivirus cuts the sound ") +
                        L.T("(например, Касперский), включите в настройках «Запись звука: через веб».", "(e.g. Kaspersky), choose “Audio capture: via browser” in Settings."));
                    return;
                }
                if (maxLevel < 0.004f)
                {
                    Log(L.T("Очень тихо — микрофон почти ничего не услышал (", "Very quiet — the microphone heard almost nothing (") + FormatTime(sec) + "). " +
                        L.T("Проверьте, тот ли микрофон выбран.", "Check that the right microphone is selected."));
                    return;
                }

                string wav = Path.Combine(WhisperEngine.WorkDir, "rec.wav");
                Recorder.SaveWav(wav, samples);

                SetStatus(L.T("Распознаю ", "Recognizing ") + FormatTime(sec) + L.T(" записи…", " of audio…"), ColorAmber);
                Log(L.T("Распознаю ", "Recognizing ") + sec.ToString("0.0") + L.T(" с записи ", " s of audio ") + EngineDescription() + "…");

                WhisperEngine.Result res = await TranscribeAsync(wav);

                if (res.Error != null)
                {
                    Log(L.T("Ошибка распознавания: ", "Recognition error: ") + res.Error);
                    if (!string.IsNullOrEmpty(res.Details)) Log("whisper: " + res.Details);
                    return;
                }
                if (res.Text.Length == 0)
                {
                    Log(L.T("Whisper ничего не распознал (", "Whisper recognized nothing (") + res.Seconds.ToString("0.0") + L.T(" с).", " s)."));
                    return;
                }

                Log(L.T("Распознано за ", "Recognized in ") + res.Seconds.ToString("0.0") + L.T(" с (", " s (") + res.Device + "): " + res.Text);
                AppendText(res.Text);

                if (settings.AutoSend) await SendText();
                else Log(L.T("Текст в поле. Нажмите «Отправить», когда будете готовы.", "The text is in the field. Click “Send” when ready."));
            }
            catch (Exception ex)
            {
                Log(L.T("Ошибка: ", "Error: ") + ex.Message);
            }
            finally
            {
                busy = false;
                UpdateStatus();
            }
        }

        private void AppendText(string text)
        {
            string cur = txtText.Text;
            if (cur.Length > 0 && !char.IsWhiteSpace(cur[cur.Length - 1])) txtText.AppendText(" ");
            txtText.AppendText(text);
        }

        // ---------------------------------------------------------------- отправка текста

        private async void SendNow()
        {
            if (busy || recording) return;
            busy = true;
            UpdateStatus();
            try
            {
                await SendText();
            }
            catch (Exception ex)
            {
                Log(L.T("Не удалось отправить: ", "Could not send: ") + ex.Message);
            }
            finally
            {
                busy = false;
                UpdateStatus();
            }
        }

        /// <summary>
        /// Вставить текст из поля в активное окно. final = false — очередная фраза посреди записи
        /// (MainForm.Live.cs): без Enter и без лишних строк в журнале; lead — пробел перед ней.
        /// </summary>
        private async Task SendText(bool final = true, string lead = "")
        {
            string text = txtText.Text.Trim();
            if (text.Length == 0)
            {
                Log(L.T("Отправлять нечего — поле пустое.", "Nothing to send — the field is empty."));
                return;
            }
            text = lead + text;

            if (final) SetStatus(L.T("Отправляю…", "Sending…"), ColorAmber);
            await WaitKeysReleased(slotDictate.Key);

            IntPtr target = Native.GetForegroundWindow();
            if (!IsExternal(target))
            {
                // Активно наше окно — отправляем в последнее окно, где вы были до этого
                target = lastExternal;
                if (target == IntPtr.Zero || !Native.IsWindow(target))
                {
                    Log(L.T("Не знаю, куда отправлять: щёлкните мышкой нужное окно и нажмите «Отправить». ", "Not sure where to send: click the target window and press “Send”. ") +
                        L.T("Текст остался в поле.", "The text stays in the field."));
                    return;
                }
                Native.ForceForeground(target);
                await Task.Delay(250);
            }

            await SetClipboard(text);
            if (final || lead.Length == 0) Log(L.T("Отправляю в: ", "Sending to: ") + Native.GetTitle(target));

            if (settings.TypeText)
            {
                int i = 0;
                foreach (char c in text)
                {
                    Native.TypeChar(c);
                    if (++i % 8 == 0) await Task.Delay(15);
                }
            }
            else
            {
                await Task.Delay(30);
                Native.SendChord(Native.VK_CONTROL, Native.VK_V);
                // окно читает буфер не сразу: не меняем его (следующей фразой), пока вставка не прошла
                await Task.Delay(150);
            }

            if (settings.PressEnter && final) await PressEnterAfterDelay();

            txtText.Clear();
            if (!final) return;
            Log(settings.PressEnter
                ? L.T("Отправлено, Enter через ", "Sent, Enter after ") + settings.EnterDelay.ToString("0.##") + L.T(" с. Поле очищено.", " s. Field cleared.")
                : L.T("Вставлено. Поле очищено.", "Pasted. Field cleared."));
        }

        private async Task PressEnterAfterDelay()
        {
            int ms = (int)Math.Round(settings.EnterDelay * 1000);
            await Task.Delay(Math.Max(ms, 0));
            Native.SendChord(Native.VK_RETURN);
        }

        // ================================================================ модель: скачать и проверить

        private async void DownloadModel()
        {
            var model = SelectedModel();
            if (model == null || downloading) return;
            if (model.Exists)
            {
                Log(L.T("Модель ", "Model ") + model.File + L.T(" уже скачана. Нажмите «Проверить».", " is already downloaded. Click “Test”."));
                return;
            }
            if (!Array.Exists(WhisperEngine.Known, k => k.File == model.File))
            {
                Log(L.T("Эту модель программа скачать не может — положите файл в папку models сами.", "The program cannot download this model — put the file into the models folder yourself."));
                return;
            }

            downloading = true;
            UpdateStatus();
            SetStatus(L.T("Скачиваю модель…", "Downloading the model…"), ColorAmber);
            try
            {
                await WhisperEngine.DownloadAsync(model,
                    (frac, mb) => OnUi(() =>
                    {
                        string pct = frac >= 0 ? ((int)(frac * 100)) + "%, " : "";
                        SetStatus(L.T("Скачиваю модель: ", "Downloading the model: ") + pct + mb.ToString("0") + L.T(" из ~", " of ~") + model.SizeMb + L.T(" МБ", " MB"),
                            ColorAmber);
                        WebProgress("model", frac, mb);
                    }),
                    msg => OnUi(() => Log(msg)),
                    closing.Token);
                Log(L.T("Модель скачана: ", "Model downloaded: ") + model.FullPath + L.T(". Нажмите «Проверить».", ". Click “Test”."));
            }
            catch (OperationCanceledException)
            {
                // окно закрывают
            }
            catch (Exception ex)
            {
                Log(L.T("Ошибка: ", "Error: ") + ex.Message);
                if (model.IsGigaAm)
                    Log(L.T("Можно скачать вручную файлы ", "You can download the files manually: ") + string.Join(", ", model.Parts) + L.T(" отсюда: ", " from ") +
                        SherpaEngine.GigaAmUrlHf.Replace("/resolve/main/", "/tree/main") + L.T(" и положить их в папку ", " and put them into ") + model.FullPath);
                else
                    Log(L.T("Можно скачать вручную: ", "You can download it manually: ") + WhisperEngine.ModelUrlHf + model.File +
                        L.T(" и положить файл в папку ", " and put the file into ") + WhisperEngine.ModelsDir);
            }
            finally
            {
                downloading = false;
                if (!IsDisposed)
                {
                    FillModels();
                    UpdateStatus();
                }
            }
        }

        private async void DownloadCuda()
        {
            if (downloading || busy || recording) return;
            if (WhisperEngine.CudaInstalled)
            {
                Log(L.T("Ускорение NVIDIA уже установлено (папка whisper-cuda). Нажмите «Проверить».", "NVIDIA acceleration is already installed (whisper-cuda folder). Click “Test”."));
                return;
            }
            downloading = true;
            UpdateStatus();
            SetStatus(L.T("Скачиваю ускорение NVIDIA…", "Downloading NVIDIA acceleration…"), ColorAmber);
            try
            {
                await WhisperEngine.DownloadCudaAsync(
                    (frac, mb) => OnUi(() =>
                    {
                        string pct = frac >= 0 ? ((int)(frac * 100)) + "%, " : "";
                        SetStatus(L.T("Скачиваю ускорение NVIDIA: ", "Downloading NVIDIA acceleration: ") + pct + mb.ToString("0") + L.T(" МБ", " MB"), ColorAmber);
                        WebProgress("cuda", frac, mb);
                    }),
                    msg => OnUi(() => Log(msg)),
                    closing.Token);
                Log(L.T("Ускорение NVIDIA установлено в ", "NVIDIA acceleration installed to ") + WhisperEngine.CudaDir + L.T(". Нажмите «Проверить» — ", ". Click “Test” — ") +
                    L.T("в журнале должно быть «видеокарта NVIDIA …».", "the log should say “GPU NVIDIA …”."));
                if (!chkGpu.Checked) chkGpu.Checked = true;
            }
            catch (OperationCanceledException)
            {
                // окно закрывают
            }
            catch (Exception ex)
            {
                Log(L.T("Не удалось установить ускорение: ", "Could not install acceleration: ") + ex.Message);
                Log(L.T("Можно вручную: скачайте ", "Manually: download ") + WhisperEngine.CudaUrl + L.T(", распакуйте файлы из папки Release ", ", unpack the files from the Release folder ") +
                    L.T("в ", "into ") + WhisperEngine.CudaDir + L.T(" и скопируйте туда же DLL из папки whisper.", " and copy the DLLs from the whisper folder there too."));
            }
            finally
            {
                downloading = false;
                if (!IsDisposed) UpdateStatus();
            }
        }

        private async void RunTest()
        {
            if (busy || recording) return;
            var model = SelectedModel();
            if (!settings.Cloud && (model == null || !model.Exists))
            {
                Log(L.T("Сначала скачайте модель (кнопка «Скачать»).", "Download a model first (the “Download” button)."));
                return;
            }
            if (settings.Cloud && string.IsNullOrEmpty(settings.OpenRouterKey))
            {
                Log(L.T("Сначала вставьте ключ OpenRouter в настройках.", "Paste your OpenRouter key in Settings first."));
                return;
            }
            if (!File.Exists(WhisperEngine.TestWavPath))
            {
                Log(L.T("Нет тестового файла ", "Test file not found: ") + WhisperEngine.TestWavPath);
                return;
            }

            busy = true;
            UpdateStatus();
            bool giga = !settings.Cloud && model.IsGigaAm;
            SetStatus(settings.Cloud ? L.T("Проверка OpenRouter…", "Testing OpenRouter…")
                : giga ? L.T("Проверка GigaAM…", "Testing GigaAM…") : L.T("Проверка Whisper…", "Testing Whisper…"), ColorAmber);
            if (!settings.Cloud && !giga && WhisperEngine.UseGpu && WhisperEngine.CudaInstalled)
                Log(L.T("Первый запуск на новой видеокарте (RTX 50xx) может занять несколько минут: драйвер готовит код. ", "The first run on a new GPU (RTX 50xx) may take a few minutes: the driver is preparing code. ") +
                    L.T("Дальше будет быстро.", "After that it will be fast."));
            Log(L.T("Проверка: распознаю тестовую запись (синтезированный голос) ", "Test: recognizing the test recording (synthesized voice) ") + EngineDescription() + "…");
            try
            {
                WhisperEngine.Result res = await TranscribeAsync(WhisperEngine.TestWavPath);
                if (res.Error != null)
                {
                    Log(L.T("Проверка не прошла: ", "Test failed: ") + res.Error);
                    if (!string.IsNullOrEmpty(res.Details)) Log("whisper: " + res.Details);
                }
                else
                {
                    Log(L.T("Проверка прошла за ", "Test passed in ") + res.Seconds.ToString("0.0") + L.T(" с (", " s (") + res.Device + L.T("). Распознано: «", "). Recognized: “") +
                        res.Text + "»");
                    if (giga)
                        Log(L.T("Должно быть: «Если вы видите этот текст, всё работает.» Начало тестовой записи («Привет! Это проверка…») ", "It should be: “Если вы видите этот текст, всё работает.” The start of the test recording (“Привет! Это проверка…”) ") +
                            L.T("GigaAM обычно пропускает — это особенность синтезированного голоса, живую речь модель распознаёт целиком.", "is usually skipped by GigaAM — a quirk of the synthesized voice; live speech is recognized in full."));
                    else
                        Log(L.T("Должно быть примерно: «Привет! Это проверка распознавания речи. ", "It should be roughly (in Russian): “Привет! Это проверка распознавания речи. ") +
                            L.T("Если вы видите этот текст, всё работает.»", "Если вы видите этот текст, всё работает.”"));
                    Log(L.T("Теперь проверьте микрофон: нажмите ", "Now test the microphone: press ") + slotDictate.Saved +
                        L.T(", скажите фразу (полоска громкости должна прыгать) и нажмите ", ", say a phrase (the volume bar should move) and press ") + slotDictate.Saved +
                        L.T(" ещё раз.", " again."));
                }
            }
            catch (Exception ex)
            {
                Log(L.T("Ошибка: ", "Error: ") + ex.Message);
            }
            finally
            {
                busy = false;
                UpdateStatus();
            }
        }

        // ================================================================ прочее

        /// <summary>Выполнить в потоке окна (если окно ещё живо).</summary>
        private void OnUi(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(a);
                }
                catch (InvalidOperationException)
                {
                    // окно закрылось
                }
            }
            else a();
        }

        /// <summary>Ждём, пока отпустят Ctrl/Alt/Shift/Win и саму горячую клавишу.</summary>
        private static async Task WaitKeysReleased(Keys hotkey)
        {
            for (int i = 0; i < 100; i++)
            {
                bool held = Native.IsKeyDown(Native.VK_CONTROL) || Native.IsKeyDown(Native.VK_MENU) ||
                            Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(Native.VK_LWIN) ||
                            Native.IsKeyDown(Native.VK_RWIN) ||
                            (hotkey != Keys.None && Native.IsKeyDown((int)hotkey));
                if (!held) return;
                await Task.Delay(20);
            }
        }

        /// <summary>Положить текст в буфер (только Unicode, см. Native.SetClipboardText). Буфер бывает занят — пробуем несколько раз.</summary>
        private async Task SetClipboard(string text)
        {
            for (int i = 0; i < 20; i++)
            {
                if (Native.SetClipboardText(Handle, text)) return;
                await Task.Delay(25);
            }
            try
            {
                Clipboard.SetDataObject(text, true, 10, 50);
            }
            catch (Exception ex)
            {
                Log(L.T("Не удалось положить текст в буфер: ", "Could not put the text on the clipboard: ") + ex.Message);
            }
        }

        private void SaveSettings()
        {
            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                Log(L.T("Не удалось сохранить настройки: ", "Could not save settings: ") + ex.Message);
            }
        }
    }
}
