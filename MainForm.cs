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
                fgTimer.Start();
                RegisterOrLog(slotDictate, "записи");
                UpdateStatus();
                Log(slotDictate.Saved + " — начать запись, ещё раз " + slotDictate.Saved +
                    " — распознать (Whisper) и отправить в активное окно.");
                CheckSetup();
                LoadOpenRouterModels();
                if (settings.Cloud) RefreshBalance(false);
                if (settings.MicSource == "browser" && StartBrowserMic())
                    Log("Запись через браузер: держите открытой вкладку " + BrowserMic.Url +
                        " (если её нет — откроется сама при первом нажатии " + slotDictate.Saved + ").");
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
                Log("Не найден whisper: " + WhisperEngine.CliPath +
                    ". Папка whisper из архива должна лежать рядом с VoiceDictate.exe.");
            var m = SelectedModel();
            if (!settings.Cloud && m != null && !m.Exists)
                Log("Модель «" + m.File + "» ещё не скачана. Нажмите «Скачать» (~" + m.SizeMb +
                    " МБ, один раз), потом «Проверить».");
            if (WhisperEngine.AsciiPath(WhisperEngine.BaseDir) == null)
                Log("Внимание: в пути к программе есть русские буквы. Если whisper не сможет открыть файлы, " +
                    "переложите папку программы, например, в C:\\VoiceDictate.");
        }

        // ================================================================ интерфейс

        private void BuildUi()
        {
            Text = "Голосовой ввод (Whisper)";
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

            root.Controls.Add(MakeHotkeyRow(slotDictate, "Записать / отправить:"));
            root.Controls.Add(MakeHint("1-е нажатие — запись с микрофона (зелёный), 2-е — распознать и отправить (красный). " +
                                       "Сменить клавишу: щёлкните в поле, нажмите сочетание и «Сохранить»."));

            // Модель
            var rowModel = MakeFlow();
            rowModel.Controls.Add(MakeRowLabel("Модель Whisper:"));
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
                Log("Модель: " + m.File + (m.Exists ? "" : " (не скачана — нажмите «Скачать»)"));
            };
            rowModel.Controls.Add(cmbModel);
            btnDownload.Text = "Скачать";
            btnDownload.AutoSize = true;
            btnDownload.Click += (s, e) => DownloadModel();
            rowModel.Controls.Add(btnDownload);
            btnTest.Text = "Проверить";
            btnTest.AutoSize = true;
            btnTest.Click += (s, e) => RunTest();
            rowModel.Controls.Add(btnTest);
            root.Controls.Add(rowModel);

            // Микрофон
            var rowMic = MakeFlow();
            rowMic.Controls.Add(MakeRowLabel("Микрофон:"));
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
            chkGpu.Text = "Видеокарта NVIDIA (CUDA)";
            chkGpu.AutoSize = true;
            chkGpu.Margin = new Padding(3, 6, 3, 3);
            chkGpu.Checked = settings.UseGpu;
            chkGpu.CheckedChanged += (s, e) =>
            {
                settings.UseGpu = chkGpu.Checked;
                WhisperEngine.UseGpu = chkGpu.Checked;
                SaveSettings();
                Log(chkGpu.Checked
                    ? (WhisperEngine.CudaInstalled ? "Распознавание на видеокарте." : "Видеокарта включена, но ускорение не скачано — нажмите кнопку справа.")
                    : "Распознавание на процессоре.");
            };
            rowGpu.Controls.Add(chkGpu);
            btnCuda.AutoSize = true;
            btnCuda.Click += (s, e) => DownloadCuda();
            rowGpu.Controls.Add(btnCuda);
            root.Controls.Add(rowGpu);

            chkAutoSend.Text = "Сразу отправлять распознанный текст в активное окно";
            chkAutoSend.AutoSize = true;
            chkAutoSend.Checked = settings.AutoSend;
            chkAutoSend.CheckedChanged += (s, e) => { settings.AutoSend = chkAutoSend.Checked; SaveSettings(); };
            root.Controls.Add(chkAutoSend);

            // Enter + задержка
            var rowEnter = MakeFlow();
            chkEnter.Text = "Нажимать Enter после вставки, задержка (сек):";
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

            chkType.Text = "Печатать посимвольно (если Ctrl+V не работает)";
            chkType.AutoSize = true;
            chkType.Checked = settings.TypeText;
            chkType.CheckedChanged += (s, e) => { settings.TypeText = chkType.Checked; SaveSettings(); };
            root.Controls.Add(chkType);

            chkTopMost.Text = "Окно поверх остальных";
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

            root.Controls.Add(MakeLabel("Распознанный текст (можно поправить руками):"));

            txtText.Multiline = true;
            txtText.ScrollBars = ScrollBars.Vertical;
            txtText.Dock = DockStyle.Fill;
            txtText.Font = new Font("Segoe UI", 11f);
            root.Controls.Add(txtText);

            var rowButtons = MakeFlow();
            btnDictate.AutoSize = true;
            btnDictate.Click += (s, e) => Toggle();
            btnSend.AutoSize = true;
            btnSend.Text = "Отправить";
            btnSend.Click += (s, e) => SendNow();
            btnClear.AutoSize = true;
            btnClear.Text = "Очистить";
            btnClear.Click += (s, e) => txtText.Clear();
            rowButtons.Controls.Add(btnDictate);
            rowButtons.Controls.Add(btnSend);
            rowButtons.Controls.Add(btnClear);
            root.Controls.Add(rowButtons);

            root.Controls.Add(MakeLabel("Журнал:"));

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
                cmbMic.Items.Add("По умолчанию (как в Windows)");
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

            var btn = new Button { Text = "Сохранить", AutoSize = true };
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
            lblLed.Text = on ? "идёт запись" : "ожидание";
            lblLed.ForeColor = on ? Color.FromArgb(30, 150, 50) : ColorRed;
            if (!on) barLevel.Value = 0;
            UpdateStatus();
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
            btnDictate.Text = recording ? "Стоп и распознать (" + k + ")" : "Записать (" + k + ")";
            btnDictate.Enabled = !busy;
            btnSend.Enabled = !busy && !recording;
            btnClear.Enabled = !busy;
            btnTest.Enabled = !busy && !recording && !downloading;
            btnDownload.Enabled = !downloading;
            btnCuda.Enabled = !downloading && !busy && !recording;
            btnCuda.Text = WhisperEngine.CudaInstalled
                ? "Ускорение NVIDIA установлено ✓"
                : "Скачать ускорение NVIDIA (~" + WhisperEngine.CudaSizeMb + " МБ)";
            cmbModel.Enabled = !busy && !recording && !downloading;
            cmbMic.Enabled = !recording;
            if (busy || downloading) return;
            if (recording)
                SetStatus("Идёт запись " + FormatTime(RecSeconds) + ". " + k + " — распознать и отправить",
                    ColorGreen);
            else
                SetStatus("Готов. " + k + " — начать запись", ColorGray);
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
                error = "не удалось разобрать сочетание";
                return false;
            }
            Unregister(slot);
            if (!Native.RegisterHotKey(Handle, slot.Id, mods | HotkeyUtil.MOD_NOREPEAT, (uint)key))
            {
                int code = Marshal.GetLastWin32Error();
                error = code == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? "сочетание уже занято другой программой"
                    : "ошибка Windows " + code;
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
                Log("Клавиша " + what + " «" + slot.Saved + "» не зарегистрирована: " + err + ". Задайте другую.");
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
                err = "не удалось разобрать сочетание";
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
                Log("Клавиша сохранена: " + wanted);
                UpdateStatus();
            }
            else
            {
                Log("Клавиша «" + wanted + "» не подходит: " + err);
                MessageBox.Show(this, "Клавиша «" + wanted + "» не подходит: " + err,
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
            base.WndProc(ref m);
        }

        // ================================================================ переключатель (F3)

        private void Toggle()
        {
            if (busy)
            {
                Log("Подождите — ещё идёт распознавание.");
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
                Log("Запись через веб работает только в новом окне программы, а оно не загружено — пишу напрямую.");
            if (viaBrowser)
            {
                if (!StartBrowserMic())
                {
                    SetStatus("Микрофон недоступен", ColorRed);
                    return;
                }
                if (!browserMic.Connected)
                {
                    OpenBrowserMicPage();
                    Log("Открыл в браузере страницу микрофона " + BrowserMic.Url + ". Нажмите там «Подключить " +
                        "микрофон», разрешите доступ, оставьте вкладку открытой и снова нажмите " + slotDictate.Saved + ".");
                    SetStatus("Подключите микрофон в браузере", ColorRed);
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
                    SetStatus("Включаю микрофон…", ColorAmber);
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
                Log("Не удалось начать запись" + (viaBrowser ? " через браузер" : viaWeb ? " через веб" : "") + ": " +
                    ex.Message);
                if (!viaBrowser)
                    Log("Если микрофон блокирует антивирус (например, Касперский), выберите в настройках " +
                        "«Запись звука: через браузер».");
                SetStatus("Микрофон недоступен", ColorRed);
                return;
            }
            finally
            {
                micStarting = false;
            }
            webRecording = viaWeb;
            recTimer.Start();
            SetRecording(true);
            string mic = viaWeb && !string.IsNullOrEmpty(webRecorder.DeviceName) ? webRecorder.DeviceName : cmbMic.Text;
            Log("Запись началась" + (viaBrowser ? " через браузер" : viaWeb ? " через веб" : "") + " (" + mic + "). Говорите, потом " +
                slotDictate.Saved + ".");
        }

        /// <summary>Настройка «Запись звука»: direct / web / browser.</summary>
        private void SetMicSource(string src)
        {
            src = src == "web" || src == "browser" ? src : "direct";
            settings.MicSource = src;
            SaveSettings();
            if (src == "browser")
            {
                Log("Запись звука: через браузер. Сейчас откроется страница " + BrowserMic.Url +
                    " — нажмите там «Подключить микрофон» и оставьте вкладку открытой.");
                if (StartBrowserMic()) OpenBrowserMicPage();
            }
            else
            {
                Log(src == "web"
                    ? "Запись звука: через веб (микрофон открывает движок окна программы)."
                    : "Запись звука: напрямую.");
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
                Log("Не удалось запустить запись через браузер: " + ex.Message);
                return false;
            }
            browserMic = mic;
            return true;
        }

        private void OpenBrowserMicPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(BrowserMic.Url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("Не удалось открыть браузер: " + ex.Message + ". Откройте вручную: " + BrowserMic.Url);
            }
        }

        private void OnBrowserMicMessage(string msg)
        {
            if (IsDisposed) return;
            if (msg.StartsWith("hello", StringComparison.Ordinal))
            {
                Log("Браузер подключил микрофон. Вкладку не закрывайте — " + slotDictate.Saved + " начинает запись.");
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
            if (error != null) Log("Микрофон (через веб): " + error);
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
                    Log("Ошибка записи: " + ex.Message);
                }
                level = recorder.LastLevel;
            }
            barLevel.Value = Math.Max(0, Math.Min(100, (int)(Math.Sqrt(level) * 100)));
            WebLevel(level);
            UpdateStatus();
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
                Log("Ошибка при остановке записи: " + ex.Message);
                return;
            }
            webRecording = false;
            SetRecording(false);   // красный

            try
            {
                double sec = samples.Length / (double)Recorder.SampleRate;
                if (sec < 0.3)
                {
                    Log("Запись слишком короткая — нечего распознавать.");
                    return;
                }
                if (maxLevel <= 0f && viaWeb)
                {
                    Log("Микрофон прислал полную тишину и через веб. Проверьте, тот ли микрофон выбран, и что " +
                        "в браузере на этом компьютере микрофон работает. Если нет — доступ, скорее всего, " +
                        "блокирует антивирус для всех программ, нужно обратиться к администратору.");
                    return;
                }
                if (maxLevel <= 0f)
                {
                    Log("Микрофон прислал полную тишину. В Windows 10/11 проверьте: Параметры → Конфиденциальность → " +
                        "Микрофон → «Разрешить приложениям доступ к микрофону» и «Разрешить классическим " +
                        "приложениям…». И выберите правильный микрофон в списке. Если звук режет антивирус " +
                        "(например, Касперский), включите в настройках «Запись звука: через веб».");
                    return;
                }
                if (maxLevel < 0.004f)
                {
                    Log("Очень тихо — микрофон почти ничего не услышал (" + FormatTime(sec) + "). " +
                        "Проверьте, тот ли микрофон выбран.");
                    return;
                }

                string wav = Path.Combine(WhisperEngine.WorkDir, "rec.wav");
                Recorder.SaveWav(wav, samples);

                SetStatus("Распознаю " + FormatTime(sec) + " записи…", ColorAmber);
                Log("Распознаю " + sec.ToString("0.0") + " с записи " + EngineDescription() + "…");

                WhisperEngine.Result res = await TranscribeAsync(wav);

                if (res.Error != null)
                {
                    Log("Ошибка распознавания: " + res.Error);
                    if (!string.IsNullOrEmpty(res.Details)) Log("whisper: " + res.Details);
                    return;
                }
                if (res.Text.Length == 0)
                {
                    Log("Whisper ничего не распознал (" + res.Seconds.ToString("0.0") + " с).");
                    return;
                }

                Log("Распознано за " + res.Seconds.ToString("0.0") + " с (" + res.Device + "): " + res.Text);
                AppendText(res.Text);

                if (settings.AutoSend) await SendText();
                else Log("Текст в поле. Нажмите «Отправить», когда будете готовы.");
            }
            catch (Exception ex)
            {
                Log("Ошибка: " + ex.Message);
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
                Log("Не удалось отправить: " + ex.Message);
            }
            finally
            {
                busy = false;
                UpdateStatus();
            }
        }

        private async Task SendText()
        {
            string text = txtText.Text.Trim();
            if (text.Length == 0)
            {
                Log("Отправлять нечего — поле пустое.");
                return;
            }

            SetStatus("Отправляю…", ColorAmber);
            await WaitKeysReleased(slotDictate.Key);

            IntPtr target = Native.GetForegroundWindow();
            if (!IsExternal(target))
            {
                // Активно наше окно — отправляем в последнее окно, где вы были до этого
                target = lastExternal;
                if (target == IntPtr.Zero || !Native.IsWindow(target))
                {
                    Log("Не знаю, куда отправлять: щёлкните мышкой нужное окно и нажмите «Отправить». " +
                        "Текст остался в поле.");
                    return;
                }
                Native.ForceForeground(target);
                await Task.Delay(250);
            }

            SetClipboard(text);
            Log("Отправляю в: " + Native.GetTitle(target));

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
                Native.SendChord(Native.VK_CONTROL, Native.VK_V);
            }

            if (settings.PressEnter)
            {
                int ms = (int)Math.Round(settings.EnterDelay * 1000);
                await Task.Delay(Math.Max(ms, 0));
                Native.SendChord(Native.VK_RETURN);
            }

            txtText.Clear();
            Log(settings.PressEnter
                ? "Отправлено, Enter через " + settings.EnterDelay.ToString("0.##") + " с. Поле очищено."
                : "Вставлено. Поле очищено.");
        }

        // ================================================================ модель: скачать и проверить

        private async void DownloadModel()
        {
            var model = SelectedModel();
            if (model == null || downloading) return;
            if (model.Exists)
            {
                Log("Модель " + model.File + " уже скачана. Нажмите «Проверить».");
                return;
            }
            if (!Array.Exists(WhisperEngine.Known, k => k.File == model.File))
            {
                Log("Эту модель программа скачать не может — положите файл в папку models сами.");
                return;
            }

            downloading = true;
            UpdateStatus();
            SetStatus("Скачиваю модель…", ColorAmber);
            try
            {
                await WhisperEngine.DownloadAsync(model,
                    (frac, mb) => OnUi(() =>
                    {
                        string pct = frac >= 0 ? ((int)(frac * 100)) + "%, " : "";
                        SetStatus("Скачиваю модель: " + pct + mb.ToString("0") + " из ~" + model.SizeMb + " МБ",
                            ColorAmber);
                        WebProgress("model", frac, mb);
                    }),
                    msg => OnUi(() => Log(msg)),
                    closing.Token);
                Log("Модель скачана: " + model.FullPath + ". Нажмите «Проверить».");
            }
            catch (OperationCanceledException)
            {
                // окно закрывают
            }
            catch (Exception ex)
            {
                Log("Ошибка: " + ex.Message);
                Log("Можно скачать вручную: " + WhisperEngine.ModelUrlHf + model.File +
                    " и положить файл в папку " + WhisperEngine.ModelsDir);
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
                Log("Ускорение NVIDIA уже установлено (папка whisper-cuda). Нажмите «Проверить».");
                return;
            }
            downloading = true;
            UpdateStatus();
            SetStatus("Скачиваю ускорение NVIDIA…", ColorAmber);
            try
            {
                await WhisperEngine.DownloadCudaAsync(
                    (frac, mb) => OnUi(() =>
                    {
                        string pct = frac >= 0 ? ((int)(frac * 100)) + "%, " : "";
                        SetStatus("Скачиваю ускорение NVIDIA: " + pct + mb.ToString("0") + " МБ", ColorAmber);
                        WebProgress("cuda", frac, mb);
                    }),
                    msg => OnUi(() => Log(msg)),
                    closing.Token);
                Log("Ускорение NVIDIA установлено в " + WhisperEngine.CudaDir + ". Нажмите «Проверить» — " +
                    "в журнале должно быть «видеокарта NVIDIA …».");
                if (!chkGpu.Checked) chkGpu.Checked = true;
            }
            catch (OperationCanceledException)
            {
                // окно закрывают
            }
            catch (Exception ex)
            {
                Log("Не удалось установить ускорение: " + ex.Message);
                Log("Можно вручную: скачайте " + WhisperEngine.CudaUrl + ", распакуйте файлы из папки Release " +
                    "в " + WhisperEngine.CudaDir + " и скопируйте туда же DLL из папки whisper.");
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
                Log("Сначала скачайте модель (кнопка «Скачать»).");
                return;
            }
            if (settings.Cloud && string.IsNullOrEmpty(settings.OpenRouterKey))
            {
                Log("Сначала вставьте ключ OpenRouter в настройках.");
                return;
            }
            if (!File.Exists(WhisperEngine.TestWavPath))
            {
                Log("Нет тестового файла " + WhisperEngine.TestWavPath);
                return;
            }

            busy = true;
            UpdateStatus();
            SetStatus(settings.Cloud ? "Проверка OpenRouter…" : "Проверка Whisper…", ColorAmber);
            if (!settings.Cloud && WhisperEngine.UseGpu && WhisperEngine.CudaInstalled)
                Log("Первый запуск на новой видеокарте (RTX 50xx) может занять несколько минут: драйвер готовит код. " +
                    "Дальше будет быстро.");
            Log("Проверка: распознаю тестовую запись (синтезированный голос) " + EngineDescription() + "…");
            try
            {
                WhisperEngine.Result res = await TranscribeAsync(WhisperEngine.TestWavPath);
                if (res.Error != null)
                {
                    Log("Проверка не прошла: " + res.Error);
                    if (!string.IsNullOrEmpty(res.Details)) Log("whisper: " + res.Details);
                }
                else
                {
                    Log("Проверка прошла за " + res.Seconds.ToString("0.0") + " с (" + res.Device + "). Распознано: «" +
                        res.Text + "»");
                    Log("Должно быть примерно: «Привет! Это проверка распознавания речи. " +
                        "Если вы видите этот текст, всё работает.»");
                    Log("Теперь проверьте микрофон: нажмите " + slotDictate.Saved +
                        ", скажите фразу (полоска громкости должна прыгать) и нажмите " + slotDictate.Saved +
                        " ещё раз.");
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка: " + ex.Message);
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

        private void SetClipboard(string text)
        {
            try
            {
                Clipboard.SetDataObject(text, true, 10, 50);
            }
            catch (Exception ex)
            {
                Log("Не удалось положить текст в буфер: " + ex.Message);
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
                Log("Не удалось сохранить настройки: " + ex.Message);
            }
        }
    }
}
