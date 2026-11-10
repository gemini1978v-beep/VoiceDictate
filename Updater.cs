using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Обновление программы из релизов GitHub: узнать последнюю версию, скачать архив,
    /// заменить свои файлы (settings.ini, модели, whisper-cuda, ffmpeg не трогаем) и перезапуститься.
    /// Файлы заменяет сама новая версия, запущенная из папки update, когда старая уже закрыта:
    /// запущенный exe трогать нельзя — программа собрана в один файл и подгружает свои части из него же.
    /// </summary>
    internal static class Updater
    {
        private const string LatestUrl = "https://api.github.com/repos/gemini1978v-beep/VoiceDictate/releases/latest";
        private const string AssetName = "VoiceDictate-win-x64.zip";

        /// <summary>Аргумент: программу только что обновили.</summary>
        public const string AfterUpdateArg = "--after-update";

        /// <summary>Аргумент нового exe, запущенного из папки update: разложить себя на место старой версии.</summary>
        public const string InstallArg = "--install-update";

        public sealed class Release
        {
            public string Version;   // «1.4»
            public string PageUrl;   // страница релиза с описанием
            public string ZipUrl;
            public long Size;
        }

        private static string ExePath
        {
            get { return Environment.ProcessPath ?? Path.Combine(WhisperEngine.BaseDir, "VoiceDictate.exe"); }
        }

        private static string UpdateDir
        {
            get { return Path.Combine(WhisperEngine.BaseDir, "update"); }
        }

        private static HttpClient NewHttp(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceDictate/" + L.Version);
            return http;
        }

        /// <summary>
        /// Есть ли версия новее этой. null — нет (или GitHub не ответил).
        /// Адрес можно подменить переменной VOICEDICTATE_UPDATE_URL (для проверки обновления без GitHub).
        /// </summary>
        public static async Task<Release> CheckAsync()
        {
            string url = Environment.GetEnvironmentVariable("VOICEDICTATE_UPDATE_URL");
            if (string.IsNullOrEmpty(url)) url = LatestUrl;
            using (HttpClient http = NewHttp(TimeSpan.FromSeconds(20)))
            {
                string json = await http.GetStringAsync(url);
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    string tag = root.GetProperty("tag_name").GetString() ?? "";
                    if (!IsNewer(tag, L.Version)) return null;
                    foreach (JsonElement a in root.GetProperty("assets").EnumerateArray())
                    {
                        if (a.GetProperty("name").GetString() != AssetName) continue;
                        return new Release
                        {
                            Version = tag.TrimStart('v', 'V'),
                            PageUrl = root.TryGetProperty("html_url", out JsonElement page) ? page.GetString() : null,
                            ZipUrl = a.GetProperty("browser_download_url").GetString(),
                            Size = a.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : -1,
                        };
                    }
                    return null;   // релиз без архива — нечего ставить
                }
            }
        }

        private static bool IsNewer(string tag, string current)
        {
            Version a, b;
            return Version.TryParse(tag.TrimStart('v', 'V'), out a) && Version.TryParse(current, out b) && a > b;
        }

        /// <summary>
        /// Скачать архив и распаковать в папку update. Возвращает папку новой версии;
        /// дальше StartInstaller, и эту копию программы надо закрыть.
        /// </summary>
        public static async Task<string> DownloadAsync(Release r, Action<double, double> progress, Action<string> log,
            CancellationToken ct)
        {
            string dir = UpdateDir;
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
            catch (UnauthorizedAccessException)
            {
                throw new IOException(L.T("нет прав на запись в папку программы — скачайте новую версию вручную", "no write access to the program folder — download the new version manually"));
            }

            string zip = Path.Combine(dir, AssetName);
            log(L.T("Скачиваю ", "Downloading ") + r.ZipUrl);
            using (HttpClient http = NewHttp(Timeout.InfiniteTimeSpan))
            using (var resp = await http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? r.Size;
                using (var src = await resp.Content.ReadAsStreamAsync(ct))
                using (var dst = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    var tick = Stopwatch.StartNew();
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct);
                        done += n;
                        if (tick.ElapsedMilliseconds > 250)
                        {
                            tick.Restart();
                            progress(total > 0 ? (double)done / total : -1, done / 1048576.0);
                        }
                    }
                    progress(1, done / 1048576.0);
                }
            }

            log(L.T("Распаковываю…", "Unpacking…"));
            return await Task.Run(() =>
            {
                string unpack = Path.Combine(dir, "new");
                ZipFile.ExtractToDirectory(zip, unpack);
                string root = Path.Combine(unpack, "VoiceDictate");   // в архиве всё лежит в папке VoiceDictate
                if (!Directory.Exists(root)) root = unpack;
                if (!File.Exists(Path.Combine(root, "VoiceDictate.exe")))
                    throw new InvalidDataException(L.T("в архиве нет VoiceDictate.exe", "the archive has no VoiceDictate.exe"));
                return root;
            });
        }

        /// <summary>
        /// Запустить новую версию прямо из распакованной папки: она дождётся, пока эта копия закроется,
        /// разложит свои файлы поверх старых и запустит программу (RunInstaller).
        /// </summary>
        public static void StartInstaller(string newRoot)
        {
            var psi = new ProcessStartInfo(Path.Combine(newRoot, "VoiceDictate.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = newRoot,
            };
            psi.ArgumentList.Add(InstallArg);
            psi.ArgumentList.Add(ExePath);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi);
        }

        /// <summary>
        /// Режим установщика (VoiceDictate.exe --install-update путь_к_старому_exe pid):
        /// ждём закрытия старой копии, копируем файлы своей папки на место старых (кроме settings.ini) и запускаем программу.
        /// </summary>
        public static void RunInstaller(string[] args)
        {
            int i = Array.IndexOf(args, InstallArg);
            if (i < 0 || i + 2 >= args.Length) return;
            string targetExe = args[i + 1];
            string targetDir = Path.GetDirectoryName(targetExe);
            int pid;
            if (int.TryParse(args[i + 2], out pid))
            {
                try
                {
                    using (Process old = Process.GetProcessById(pid)) old.WaitForExit(30000);
                }
                catch (ArgumentException)
                {
                    // уже закрылась
                }
            }

            string myRoot = AppContext.BaseDirectory;
            string myExe = Environment.ProcessPath;
            Exception last = null;
            for (int attempt = 0; attempt < 20; attempt++)   // файлы старой копии могут освободиться не сразу
            {
                try
                {
                    foreach (string src in Directory.GetFiles(myRoot, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(myRoot, src);
                        string dst = string.Equals(src, myExe, StringComparison.OrdinalIgnoreCase)
                            ? targetExe
                            : Path.Combine(targetDir, rel);
                        // свои настройки (и ключ OpenRouter) оставляем
                        if (string.Equals(rel, "settings.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dst)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(src, dst, true);
                    }
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(500);
                }
            }

            if (last != null)
            {
                System.Windows.Forms.MessageBox.Show(
                    L.T("Не удалось установить новую версию: ", "Could not install the new version: ") + last.Message +
                    L.T("\n\nСкачайте её вручную со страницы релизов на GitHub.", "\n\nDownload it manually from the GitHub releases page."),
                    L.T("Голосовой ввод", "Voice Input"));
            }
            var psi = new ProcessStartInfo(targetExe) { UseShellExecute = false, WorkingDirectory = targetDir };
            if (last == null) psi.ArgumentList.Add(AfterUpdateArg);
            Process.Start(psi);
        }

        /// <summary>При запуске: убрать скачанный архив и распакованную новую версию.</summary>
        public static void CleanupAfterUpdate()
        {
            try
            {
                if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true);
            }
            catch
            {
                // установщик ещё не закрылся — удалим в следующий раз
            }
        }
    }
}
