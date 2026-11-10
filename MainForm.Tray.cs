using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace VoiceDictate
{
    /// <summary>
    /// Значок в трее (у часов). Свернули окно — оно прячется в трей, программа продолжает работать:
    /// F3 записывает и отправляет как обычно. Цвет значка: синий — ожидание, зелёный — запись,
    /// оранжевый — распознавание. Щелчок по значку — открыть/спрятать окно, правая кнопка — меню.
    /// </summary>
    internal sealed partial class MainForm
    {
        private NotifyIcon tray;
        private Icon iconApp, iconTrayIdle, iconTrayRec, iconTrayBusy;
        private ToolStripMenuItem trayShow;
        private int trayIconState = -1;
        private RegisteredWaitHandle showWait;

        private static Icon LoadIconResource(string name, Size size)
        {
            try
            {
                using (Stream st = typeof(MainForm).Assembly.GetManifestResourceStream("VoiceDictate.icons." + name))
                {
                    if (st == null) return null;
                    return size.IsEmpty ? new Icon(st) : new Icon(st, size);
                }
            }
            catch
            {
                return null;
            }
        }

        private void InitTray()
        {
            iconApp = LoadIconResource("app.ico", Size.Empty);
            if (iconApp != null) Icon = iconApp;   // значок окна и панели задач

            Size small = SystemInformation.SmallIconSize;
            iconTrayIdle = LoadIconResource("app.ico", small);
            iconTrayRec = LoadIconResource("tray-rec.ico", small);
            iconTrayBusy = LoadIconResource("tray-busy.ico", small);

            var menu = new ContextMenuStrip();
            trayShow = new ToolStripMenuItem(L.T("Открыть окно", "Open window"), null, (s, e) => ShowFromTray());
            trayShow.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(trayShow);
            menu.Items.Add(new ToolStripSeparator());
            var trayExit = new ToolStripMenuItem(L.T("Выход", "Exit"), null, (s, e) => Close());
            menu.Items.Add(trayExit);
            menu.Opening += (s, e) =>
            {
                trayShow.Text = IsShownNormally() ? L.T("Спрятать в трей", "Hide to tray") : L.T("Открыть окно", "Open window");
                trayExit.Text = L.T("Выход", "Exit");
            };

            tray = new NotifyIcon();
            tray.Icon = iconTrayIdle ?? iconApp ?? SystemIcons.Application;
            tray.Text = L.T("Голосовой ввод ", "Voice Input ") + L.Version;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (IsShownNormally()) HideToTray();
                else ShowFromTray();
            };
            tray.Visible = true;

            // Свернули кнопкой на панели задач (или Win+D/Win+M) — прячем в трей
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized && Visible) HideToTray();
            };
            FormClosing += (s, e) =>
            {
                // закрываем окно файлов и останавливаем его ffmpeg/whisper, чтобы не остались висеть
                if (fileForm != null && !fileForm.IsDisposed) fileForm.Close();
                FileTranscriber.KillRunning();
            };
            FormClosed += (s, e) =>
            {
                if (showWait != null) showWait.Unregister(null);
                tray.Visible = false;
                tray.Dispose();
                SherpaEngine.Unload();
                WhisperEngine.CleanWorkDir();   // записи голоса и временные файлы не оставляем
            };

            // Повторный запуск программы (второй exe) просит показать окно этой копии
            if (Program.ShowEvent != null)
            {
                showWait = ThreadPool.RegisterWaitForSingleObject(Program.ShowEvent,
                    (state, timedOut) => OnUi(() =>
                    {
                        ShowFromTray();
                        Native.ForceForeground(Handle);
                    }),
                    null, Timeout.Infinite, false);
            }
            UpdateTray();
        }

        private bool IsShownNormally()
        {
            return Visible && WindowState != FormWindowState.Minimized;
        }

        private void HideToTray()
        {
            if (tray == null)
            {
                WindowState = FormWindowState.Minimized;
                return;
            }
            Hide();   // окно и его обработчик горячей клавиши живут дальше, просто не видны
            if (!settings.TrayHintShown)
            {
                settings.TrayHintShown = true;
                SaveSettings();
                try
                {
                    tray.ShowBalloonTip(4000, L.T("Голосовой ввод работает в трее", "Voice Input is running in the tray"),
                        slotDictate.Saved + L.T(" — запись и отправка, как обычно. Щёлкните по значку, чтобы открыть окно.", " — record and send as usual. Click the icon to open the window."),
                        ToolTipIcon.None);
                }
                catch
                {
                    // уведомления отключены
                }
            }
        }

        private void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        /// <summary>Цвет значка и подсказка — по состоянию записи.</summary>
        private void UpdateTray()
        {
            if (tray == null) return;
            int st = recording ? 1 : busy ? 2 : 0;
            if (st != trayIconState)
            {
                trayIconState = st;
                Icon ic = st == 1 ? iconTrayRec : st == 2 ? iconTrayBusy : iconTrayIdle;
                if (ic != null) tray.Icon = ic;
            }
            string text = recording ? L.T("Идёт запись — ", "Recording — ") + slotDictate.Saved + L.T(", чтобы отправить", " to send")
                : busy ? L.T("Голосовой ввод — распознаю…", "Voice Input — recognizing…")
                : L.T("Голосовой ввод ", "Voice Input ") + L.Version + " — " + slotDictate.Saved + L.T(" — запись", " — record");
            if (text.Length > 63) text = text.Substring(0, 63);
            if (tray.Text != text) tray.Text = text;
        }
    }
}
