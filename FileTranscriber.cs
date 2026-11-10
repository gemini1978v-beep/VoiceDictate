using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Распознавание готовых аудио- и видеофайлов.
    /// WAV, MP3, FLAC, OGG whisper читает сам. Всё остальное (M4A, AAC, OPUS, WMA, любое видео)
    /// сначала перегоняет в WAV программа ffmpeg — её можно скачать кнопкой (папка ffmpeg рядом с exe)
    /// или она уже может быть установлена в системе (PATH).
    /// </summary>
    internal static class FileTranscriber
    {
        public static readonly string[] DirectExts = { ".wav", ".mp3", ".flac", ".ogg" };

        public static readonly string[] VideoExts =
        {
            ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv", ".3gp", ".mpeg", ".mpg",
            ".ts", ".mts", ".m2ts", ".vob", ".ogv",
        };

        public static string DialogFilter
        {
            get { return L.T("Аудио и видео", "Audio and video") + "|" + MediaMask + "|" + L.T("Все файлы", "All files") + "|*.*"; }
        }

        private const string MediaMask =
            "*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.oga;*.opus;*.wma;*.amr;*.aiff;*.aif;*.ac3;*.mka;" +
            "*.mp4;*.m4v;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.flv;*.3gp;*.mpeg;*.mpg;*.ts;*.mts;*.m2ts;*.vob;*.ogv";

        /// <summary>Откуда качать ffmpeg (берётся только ffmpeg.exe из папки bin архива).</summary>
        public static readonly string[] FfmpegUrls =
        {
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        };
        public const int FfmpegSizeMb = 100;

        private static readonly object runLock = new object();
        private static readonly List<Process> running = new List<Process>();

        public static string FfmpegDir
        {
            get { return Path.Combine(WhisperEngine.BaseDir, "ffmpeg"); }
        }

        /// <summary>Путь к ffmpeg.exe: своя папка ffmpeg, иначе установленный в системе. null — нет.</summary>
        public static string FfmpegPath
        {
            get
            {
                string own = Path.Combine(FfmpegDir, "ffmpeg.exe");
                if (File.Exists(own)) return own;
                try
                {
                    string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    foreach (string dir in path.Split(';'))
                    {
                        if (dir.Trim().Length == 0) continue;
                        string f = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                        if (File.Exists(f)) return f;
                    }
                }
                catch
                {
                    // кривой PATH
                }
                return null;
            }
        }

        public static bool IsDirect(string file)
        {
            return DirectExts.Contains(Path.GetExtension(file).ToLowerInvariant());
        }

        public static bool IsVideo(string file)
        {
            return VideoExts.Contains(Path.GetExtension(file).ToLowerInvariant());
        }

        /// <summary>Можно ли распознать этот файл прямо сейчас (с ffmpeg — любой).</summary>
        public static bool CanRead(string file)
        {
            return IsDirect(file) || FfmpegPath != null;
        }

        /// <summary>Остановить ffmpeg/whisper, если работают (при выходе из программы).</summary>
        public static void KillRunning()
        {
            lock (runLock)
            {
                foreach (Process p in running)
                {
                    try
                    {
                        if (!p.HasExited) p.Kill(true);
                    }
                    catch
                    {
                        // уже завершился
                    }
                }
            }
        }

        // ================================================================ распознавание

        public sealed class Result
        {
            public string Text = "";
            public string TxtPath;
            public string SrtPath;
            public double Seconds;        // сколько шла работа
            public double AudioSeconds;   // длина записи (если известна)
            public string Device = L.T("процессор", "CPU");
            public string Error;          // null — всё хорошо
            public string Details;
        }

        /// <summary>
        /// Распознать файл. progress(стадия, доля): "prep" — подготовка звука, "run" — распознавание;
        /// доля 0..1 или -1, если неизвестна.
        /// </summary>
        public static Task<Result> TranscribeAsync(string input, string modelPath, string language, string prompt,
            bool srt, Action<string, double> progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    return Run(input, modelPath, language, prompt, srt, progress, ct);
                }
                finally
                {
                    // копия звука может весить сотни МБ — не держим её до следующего файла
                    try
                    {
                        string work = Path.Combine(WhisperEngine.WorkDir, "file");
                        if (Directory.Exists(work))
                            foreach (string f in Directory.GetFiles(work)) TryDelete(f);
                    }
                    catch
                    {
                        // не страшно
                    }
                }
            });
        }

        private static Result Run(string input, string modelPath, string language, string prompt, bool srt,
            Action<string, double> progress, CancellationToken ct)
        {
            var res = new Result();
            var sw = Stopwatch.StartNew();

            if (!File.Exists(input))
            {
                res.Error = L.T("файл не найден: ", "file not found: ") + input;
                return res;
            }
            string cli = WhisperEngine.CliPath;
            if (!File.Exists(cli))
            {
                res.Error = L.T("не найден ", "not found: ") + cli + L.T(". Папка whisper должна лежать рядом с программой.", ". The whisper folder must be next to the program.");
                return res;
            }
            string model = WhisperEngine.AsciiPath(modelPath);
            if (!File.Exists(modelPath) || model == null)
            {
                res.Error = model == null
                    ? L.T("в пути к программе есть русские буквы — переложите её, например, в C:\\VoiceDictate", "the program path contains non-Latin letters — move it, for example, to C:\\VoiceDictate")
                    : L.T("модель не скачана: ", "model is not downloaded: ") + Path.GetFileName(modelPath);
                return res;
            }

            string work = Path.Combine(WhisperEngine.WorkDir, "file");
            Directory.CreateDirectory(work);
            foreach (string old in Directory.GetFiles(work)) TryDelete(old);

            // ---- 1. звук: WAV 16 кГц моно (через ffmpeg) или копия файла с латинским именем
            string audio;
            string ff = FfmpegPath;
            if (ff != null)
            {
                progress("prep", -1);
                audio = Path.Combine(work, "audio.wav");
                string err = RunFfmpeg(ff, input, audio, progress, ct, out res.AudioSeconds);
                if (err != null)
                {
                    res.Error = err;
                    return res;
                }
            }
            else if (IsDirect(input))
            {
                progress("prep", -1);
                audio = Path.Combine(work, "audio" + Path.GetExtension(input).ToLowerInvariant());
                File.Copy(input, audio, true);
            }
            else
            {
                res.Error = L.T("для этого формата нужен ffmpeg — нажмите «Скачать ffmpeg»", "this format needs ffmpeg — click “Download ffmpeg”");
                return res;
            }
            ct.ThrowIfCancellationRequested();

            // ---- 2. whisper
            progress("run", 0);
            string outBase = Path.Combine(work, "result");
            string rsp = Path.Combine(work, "args.txt");
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            var args = new List<string>
            {
                "-m", model,
                "-f", audio,
                "-l", string.IsNullOrWhiteSpace(language) ? "ru" : language.Trim(),
                "-t", threads.ToString(CultureInfo.InvariantCulture),
                "-pp",            // печатать прогресс в процентах
                "-otxt",
                "-of", outBase,
            };
            if (srt) args.Add("-osrt");
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                args.Add("--prompt");
                args.Add(prompt.Replace("\r", " ").Replace("\n", " ").Trim());
            }
            File.WriteAllText(rsp, string.Join("\n", args) + "\n", new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = "\"@" + rsp + "\"",
                WorkingDirectory = Path.GetDirectoryName(cli),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["CUDA_CACHE_MAXSIZE"] = "4294967296";

            var log = new StringBuilder();
            var progressRe = new Regex(@"progress\s*=\s*(\d+)\s*%", RegexOptions.Compiled);
            int exitCode = RunProcess(psi, null, line =>
            {
                lock (log) log.AppendLine(line);
                Match m = progressRe.Match(line);
                if (m.Success) progress("run", Math.Min(100, int.Parse(m.Groups[1].Value)) / 100.0);
            }, ct);

            res.Seconds = sw.Elapsed.TotalSeconds;
            string logText;
            lock (log) logText = log.ToString();
            Match dev = Regex.Match(logText, @"Device \d+:\s*([^,\r\n]+)");
            if (dev.Success) res.Device = L.T("видеокарта ", "GPU ") + dev.Groups[1].Value.Trim();
            if (exitCode != 0)
            {
                bool badAudio = logText.IndexOf("failed to read", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                logText.IndexOf("failed to open", StringComparison.OrdinalIgnoreCase) >= 0;
                res.Error = badAudio
                    ? L.T("whisper не смог прочитать файл. Скачайте ffmpeg (кнопка в окне) — он понимает любые форматы", "whisper could not read the file. Download ffmpeg (button in the window) — it understands any format")
                    : WhisperEngine.DescribeExitCode(exitCode);
                res.Details = WhisperEngine.Tail(logText, 6);
                return res;
            }

            string raw = File.Exists(outBase + ".txt") ? File.ReadAllText(outBase + ".txt", Encoding.UTF8) : "";
            res.Text = FormatText(raw);
            if (res.Text.Length == 0)
            {
                res.Error = L.T("Whisper не нашёл речи в этом файле", "Whisper found no speech in this file");
                return res;
            }

            // ---- 3. сохраняем рядом с исходным файлом (или в «Документы\VoiceDictate», если туда нельзя)
            string baseName = Path.GetFileNameWithoutExtension(input);
            string[] dirs =
            {
                Path.GetDirectoryName(Path.GetFullPath(input)),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoiceDictate"),
            };
            Exception lastErr = null;
            foreach (string dir in dirs)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string b = UniqueBase(dir, baseName, srt);
                    res.TxtPath = b + ".txt";
                    File.WriteAllText(res.TxtPath, res.Text + Environment.NewLine, new UTF8Encoding(true));
                    if (srt && File.Exists(outBase + ".srt"))
                    {
                        res.SrtPath = b + ".srt";
                        File.Copy(outBase + ".srt", res.SrtPath, false);
                    }
                    lastErr = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                    res.TxtPath = null;
                    res.SrtPath = null;
                }
            }
            if (lastErr != null) res.Error = L.T("не удалось сохранить текст: ", "could not save the text: ") + lastErr.Message;
            return res;
        }

        /// <summary>Имя без расширения, чтобы ни .txt, ни .srt с таким именем ещё не было.</summary>
        private static string UniqueBase(string dir, string name, bool srt)
        {
            for (int i = 1; i < 1000; i++)
            {
                string b = Path.Combine(dir, i == 1 ? name : name + " (" + i + ")");
                if (!File.Exists(b + ".txt") && (!srt || !File.Exists(b + ".srt"))) return b;
            }
            return Path.Combine(dir, name + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        /// <summary>Сегменты whisper → чистый текст абзацами (без «Продолжение следует» и т. п.).</summary>
        public static string FormatText(string raw)
        {
            var paragraphs = new List<string>();
            var cur = new StringBuilder();
            foreach (string line in raw.Replace("\r", "").Split('\n'))
            {
                string t = WhisperEngine.Clean(line);
                if (t.Length == 0) continue;
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(t);
                char last = t[t.Length - 1];
                if (cur.Length > 600 && (last == '.' || last == '!' || last == '?' || last == '…'))
                {
                    paragraphs.Add(cur.ToString());
                    cur.Clear();
                }
            }
            if (cur.Length > 0) paragraphs.Add(cur.ToString());
            return string.Join(Environment.NewLine + Environment.NewLine, paragraphs).Trim();
        }

        // ================================================================ ffmpeg

        private static string RunFfmpeg(string ff, string input, string wav, Action<string, double> progress,
            CancellationToken ct, out double duration)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ff,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in new[] { "-hide_banner", "-nostdin", "-y", "-i", input, "-vn", "-ac", "1", "-ar", "16000",
                         "-c:a", "pcm_s16le", wav })
                psi.ArgumentList.Add(a);

            double total = 0;
            var log = new StringBuilder();
            var durRe = new Regex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            var timeRe = new Regex(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            int code = RunProcess(psi, null, line =>
            {
                lock (log) log.AppendLine(line);
                Match d = durRe.Match(line);
                if (d.Success && total <= 0) total = ToSeconds(d);
                Match t = timeRe.Match(line);
                if (t.Success && total > 0) progress("prep", Math.Min(1, ToSeconds(t) / total));
            }, ct);
            duration = total;

            string text;
            lock (log) text = log.ToString();
            if (code == 0 && File.Exists(wav) && new FileInfo(wav).Length > 1000) return null;
            if (text.IndexOf("does not contain any stream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("matches no streams", StringComparison.OrdinalIgnoreCase) >= 0)
                return L.T("в этом файле нет звуковой дорожки", "this file has no audio track");
            return L.T("ffmpeg не смог прочитать файл: ", "ffmpeg could not read the file: ") + WhisperEngine.Tail(text, 2);
        }

        private static double ToSeconds(Match m)
        {
            return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                   double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>Запустить процесс, отдавать строки вывода, остановить по отмене. Возвращает код выхода.</summary>
        private static int RunProcess(ProcessStartInfo psi, Action<string> onOut, Action<string> onErr, CancellationToken ct)
        {
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null && onOut != null) onOut(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && onErr != null) onErr(e.Data); };
                p.Start();
                lock (runLock) running.Add(p);
                try
                {
                    try
                    {
                        p.PriorityClass = ProcessPriorityClass.BelowNormal;   // чтобы компьютер не тормозил
                    }
                    catch
                    {
                        // не дали
                    }
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    while (!p.WaitForExit(200))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            try
                            {
                                p.Kill(true);
                            }
                            catch
                            {
                                // уже завершился
                            }
                            p.WaitForExit(3000);
                            ct.ThrowIfCancellationRequested();
                        }
                    }
                    p.WaitForExit();   // дочитать вывод
                    return p.ExitCode;
                }
                finally
                {
                    lock (runLock) running.Remove(p);
                }
            }
        }

        /// <summary>Скачать ffmpeg.exe в папку ffmpeg рядом с программой.</summary>
        public static async Task DownloadFfmpegAsync(Action<double, double> progress, Action<string> log, CancellationToken ct)
        {
            Directory.CreateDirectory(FfmpegDir);
            string zip = Path.Combine(FfmpegDir, "ffmpeg.zip.part");
            string exe = Path.Combine(FfmpegDir, "ffmpeg.exe");
            Exception last = null;
            using (var http = new HttpClient())
            {
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceDictate/1.0");
                foreach (string url in FfmpegUrls)
                {
                    try
                    {
                        log(L.T("Скачиваю ffmpeg: ", "Downloading ffmpeg: ") + url);
                        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            resp.EnsureSuccessStatusCode();
                            long total = resp.Content.Headers.ContentLength ?? -1;
                            using (var src = await resp.Content.ReadAsStreamAsync())
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

                        log(L.T("Распаковываю ffmpeg…", "Unpacking ffmpeg…"));
                        await Task.Run(() =>
                        {
                            using (ZipArchive za = ZipFile.OpenRead(zip))
                            {
                                ZipArchiveEntry entry = za.Entries.FirstOrDefault(e =>
                                    e.FullName.Replace('\\', '/').EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                                if (entry == null) throw new InvalidDataException(L.T("в архиве нет bin/ffmpeg.exe", "the archive has no bin/ffmpeg.exe"));
                                string tmp = exe + ".tmp";
                                entry.ExtractToFile(tmp, true);
                                if (File.Exists(exe)) File.Delete(exe);
                                File.Move(tmp, exe);
                            }
                        }, ct);
                        TryDelete(zip);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(zip);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        log(L.T("Не получилось: ", "Failed: ") + ex.Message);
                        TryDelete(zip);
                    }
                }
            }
            throw new IOException(L.T("не удалось скачать ffmpeg: ", "could not download ffmpeg: ") + (last != null ? last.Message : L.T("нет ответа", "no response")));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // пусть лежит
            }
        }
    }
}
