using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VoiceDictate
{
    /// <summary>
    /// Новая версия: проверяем GitHub в фоне при запуске и раз в 12 часов (Updater.cs). Нет интернета — молчим.
    /// Если есть — плашка «Вышла версия X» на главном экране и строка в «О программе»; «Обновить» скачивает
    /// её и перезапускает программу. Кнопка «Проверить и обновить» в настройках делает то же сразу.
    /// </summary>
    internal sealed partial class MainForm
    {
        private Updater.Release updRelease;   // найденная новая версия (null — нет)
        private bool updating;
        private string updAnnounced = "";     // про какую версию уже написали в журнал
        private string updState = "";         // "" — не проверяли, checking, latest, available, offline
        private readonly Timer updTimer = new Timer();

        /// <summary>Из события Shown.</summary>
        private void InitUpdates()
        {
            Updater.CleanupAfterUpdate();
            if (Program.AfterUpdate) Log(L.T("Программа обновлена до версии ", "The program was updated to version ") + L.Version + ".");
            if (settings.CheckUpdates)
            {
                var first = CheckUpdate(false);   // в фоне: окно уже работает
                updTimer.Interval = 12 * 60 * 60 * 1000;
                updTimer.Tick += (s, e) => { var next = CheckUpdate(false); };
                updTimer.Start();
            }
            var later = new Timer { Interval = 5000 };
            later.Tick += (s, e) =>
            {
                later.Stop();
                later.Dispose();
                Updater.CleanupAfterUpdate();   // установщик из папки update к этому времени уже закрылся
            };
            later.Start();
        }

        /// <summary>Спросить GitHub. true — есть версия новее. manual — по кнопке: пишем результат в журнал.</summary>
        private async Task<bool> CheckUpdate(bool manual)
        {
            if (updating || updState == "checking") return updRelease != null;
            updState = "checking";
            WebPushState();
            Updater.Release r;
            try
            {
                r = await Updater.CheckAsync();
            }
            catch (Exception ex)
            {
                // нет интернета или GitHub не ответил — ничего не делаем, проверим в следующий раз
                updState = "offline";
                if (manual) Log(L.T("Не удалось проверить обновления: ", "Could not check for updates: ") + ex.Message);
                if (!IsDisposed) WebPushState();
                return false;
            }
            if (IsDisposed) return false;
            if (r == null)
            {
                updState = "latest";
                updRelease = null;
                if (manual) Log(L.T("Установлена последняя версия ", "You have the latest version ") + L.Version + ".");
                WebPushState();
                return false;
            }
            updRelease = r;
            updState = "available";
            if (updAnnounced != r.Version)
            {
                updAnnounced = r.Version;
                Log(L.T("Вышла новая версия ", "A new version is available: ") + r.Version + L.T(". Нажмите «Обновить» на главном экране.", ". Click “Update” on the main screen."));
                if (!Visible && tray != null)
                {
                    try
                    {
                        tray.ShowBalloonTip(5000, L.T("Голосовой ввод: вышла версия ", "Voice Input: version ") + r.Version + L.T("", " is out"),
                            L.T("Откройте окно и нажмите «Обновить».", "Open the window and click “Update”."), ToolTipIcon.None);
                    }
                    catch
                    {
                        // уведомления отключены
                    }
                }
            }
            WebPushState();
            return true;
        }

        /// <summary>Кнопка «Проверить и обновить»: проверить сейчас и, если есть новая версия, сразу поставить.</summary>
        private async void CheckAndInstall()
        {
            if (updating) return;
            if (updRelease != null || await CheckUpdate(true)) InstallUpdate();
        }

        private async void InstallUpdate()
        {
            Updater.Release r = updRelease;
            if (r == null || updating) return;
            if (recording || busy || downloading)
            {
                Log(L.T("Сначала дождитесь окончания записи, распознавания или скачивания.", "Wait for recording, recognition or download to finish first."));
                return;
            }
            updating = true;
            downloading = true;   // кнопки скачивания и выбора модели заняты
            UpdateStatus();
            SetStatus(L.T("Скачиваю версию ", "Downloading version ") + r.Version + "…", ColorAmber);
            try
            {
                string newRoot = await Updater.DownloadAsync(r,
                    (frac, mb) => OnUi(() => WebProgress("update", frac, mb)),
                    msg => OnUi(() => Log(msg)),
                    closing.Token);
                Log(L.T("Версия ", "Version ") + r.Version + L.T(" скачана, перезапускаю программу…", " downloaded, restarting the program…"));
                Updater.StartInstaller(newRoot);   // она дождётся, пока эта копия закроется
                Close();
                return;
            }
            catch (OperationCanceledException)
            {
                // окно закрывают
            }
            catch (Exception ex)
            {
                Log(L.T("Обновить не удалось: ", "Update failed: ") + ex.Message);
                if (r.PageUrl != null)
                    Log(L.T("Можно скачать вручную: ", "You can download it manually: ") + r.PageUrl);
            }
            updating = false;
            downloading = false;
            if (!IsDisposed) UpdateStatus();
        }

        /// <summary>Часть состояния для страницы: новая версия.</summary>
        private void AddUpdateState(Js js)
        {
            js.S("updVer", updRelease != null ? updRelease.Version : "")
              .S("updUrl", updRelease != null ? updRelease.PageUrl ?? "" : "")
              .B("updating", updating)
              .S("updState", updState);
        }
    }
}
