using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VoiceDictate
{
    internal static class Program
    {
        // Одна копия программы: второй запуск не открывает новое окно, а показывает уже запущенное
        private const string MutexName = "Local\\VoiceDictate.Whisper.SingleInstance";
        private const string ShowEventName = "Local\\VoiceDictate.Whisper.ShowWindow";

        /// <summary>Сигнал от повторного запуска: «покажи окно». Его ждёт MainForm.</summary>
        internal static EventWaitHandle ShowEvent;

        /// <summary>Программу только что обновили (запущена старой версией после замены файлов).</summary>
        internal static bool AfterUpdate;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [STAThread]
        private static void Main(string[] args)
        {
            if (Array.IndexOf(args, Updater.InstallArg) >= 0)
            {
                Updater.RunInstaller(args);   // новая версия раскладывает себя на место старой и запускает её
                return;
            }
            AfterUpdate = Array.IndexOf(args, Updater.AfterUpdateArg) >= 0;
            using (var mutex = new Mutex(false, MutexName))
            {
                bool owned;
                try
                {
                    // после обновления старая копия ещё закрывается — ждём её
                    owned = mutex.WaitOne(AfterUpdate ? 20000 : 0);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;   // прошлая копия завершилась аварийно
                }
                if (!owned)
                {
                    // Программа уже работает — просим её показаться и выходим
                    try
                    {
                        AllowSetForegroundWindow(-1); // ASFW_ANY: разрешаем ей вывести окно на передний план
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set();
                    }
                    catch
                    {
                        // первая копия ещё запускается — просто выходим
                    }
                    return;
                }

                try
                {
                    using (ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                    {
                        Application.SetHighDpiMode(HighDpiMode.SystemAware);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new MainForm());
                    }
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }
}
