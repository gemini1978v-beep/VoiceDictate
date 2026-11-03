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

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
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

                using (ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    Application.SetHighDpiMode(HighDpiMode.SystemAware);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                }
                GC.KeepAlive(mutex);
            }
        }
    }
}
