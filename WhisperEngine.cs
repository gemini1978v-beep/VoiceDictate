using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Модель, которую программа умеет скачать: файл Whisper (.bin) или папка GigaAM (несколько файлов ONNX).
    /// </summary>
    internal sealed class WhisperModel
    {
        public string File;      // имя файла (или папки GigaAM) в папке models
        public int SizeMb;
        public readonly string[] Parts;   // файлы папки GigaAM; null — модель Whisper
        private readonly string titleRu, titleEn;

        public WhisperModel(string file, string titleRu, string titleEn, int sizeMb, string[] parts = null)
        {
            File = file;
            this.titleRu = titleRu;
            this.titleEn = titleEn;
            SizeMb = sizeMb;
            Parts = parts;
        }

        /// <summary>GigaAM (sherpa-onnx, SherpaEngine.cs), а не Whisper.</summary>
        public bool IsGigaAm
        {
            get { return Parts != null; }
        }

        /// <summary>Как показать в списке (на языке интерфейса).</summary>
        public string Title
        {
            get { return L.En ? titleEn : titleRu; }
        }

        public string FullPath
        {
            get { return Path.Combine(WhisperEngine.ModelsDir, File); }
        }

        public bool Exists
        {
            get
            {
                try
                {
                    if (IsGigaAm)
                        return Parts.All(p => System.IO.File.Exists(Path.Combine(FullPath, p))) &&
                               new FileInfo(Path.Combine(FullPath, Parts[0])).Length > 1024 * 1024;
                    var fi = new FileInfo(FullPath);
                    return fi.Exists && fi.Length > 1024 * 1024;
                }
                catch
                {
                    return false;
                }
            }
        }

        public override string ToString()
        {
            return Title + (Exists ? "  ✓" : L.T("  (не скачана)", "  (not downloaded)"));
        }
    }

    /// <summary>Распознавание через whisper.cpp (whisper-cli.exe в папке whisper рядом с программой).</summary>
    internal static class WhisperEngine
    {
        public const string ModelUrlHf = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
        public const string ModelUrlMirror = "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/";

        /// <summary>Модели, которые можно скачать из программы. Первая — рекомендуемая.</summary>
        public static readonly WhisperModel[] Known =
        {
            new WhisperModel(SherpaEngine.GigaAmDir, "GigaAM v3, 220 МБ — рекомендую для русского",
                "GigaAM v3, 220 MB — recommended for Russian", 220, SherpaEngine.GigaAmFiles),
            new WhisperModel("ggml-small-q5_1.bin", "small, 190 МБ — для других языков и перевода", "small, 190 MB — for other languages and translation", 190),
            new WhisperModel("ggml-large-v3-turbo-q5_0.bin", "large-v3-turbo, 550 МБ — точнее", "large-v3-turbo, 550 MB — more accurate", 550),
            new WhisperModel("ggml-medium-q5_0.bin", "medium, 515 МБ", "medium, 515 MB", 515),
            new WhisperModel("ggml-base-q5_1.bin", "base, 60 МБ — быстрая, но хуже", "base, 60 MB — fast but less accurate", 60),
        };

        // Фразы, которые Whisper «придумывает» на тишине (научился на субтитрах)
        private static readonly string[] Hallucinations =
        {
            "продолжение следует", "субтитры сделал", "субтитры создавал", "субтитры подготовил",
            "редактор субтитров", "корректор а.", "спасибо за просмотр", "подписывайтесь на канал",
            "ставьте лайки", "dimatorzok",
        };

        public static string BaseDir
        {
            get { return AppContext.BaseDirectory; }
        }

        public static string WhisperDir
        {
            get { return Path.Combine(BaseDir, "whisper"); }
        }

        public static string ModelsDir
        {
            get { return Path.Combine(BaseDir, "models"); }
        }

        // ---------------- видеокарта NVIDIA (CUDA)

        /// <summary>whisper.cpp 1.9.2 с CUDA 12.4: RTX 30xx — свой код, RTX 50xx — через PTX (драйвер готовит код при первом запуске).</summary>
        public const string CudaUrl =
            "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-cublas-12.4.0-bin-x64.zip";
        public const int CudaSizeMb = 640;

        /// <summary>Включено в настройках «использовать видеокарту».</summary>
        public static bool UseGpu = true;

        public static string CudaDir
        {
            get { return Path.Combine(BaseDir, "whisper-cuda"); }
        }

        public static bool CudaInstalled
        {
            get
            {
                return File.Exists(Path.Combine(CudaDir, "whisper-cli.exe")) &&
                       File.Exists(Path.Combine(CudaDir, "ggml-cuda.dll"));
            }
        }

        public static string CliPath
        {
            get
            {
                if (UseGpu && CudaInstalled) return Path.Combine(CudaDir, "whisper-cli.exe");
                string p = Path.Combine(WhisperDir, "whisper-cli.exe");
                if (File.Exists(p)) return p;
                string old = Path.Combine(WhisperDir, "main.exe");   // старые сборки whisper.cpp
                return File.Exists(old) ? old : p;
            }
        }

        public static string TestWavPath
        {
            get { return Path.Combine(WhisperDir, "test-ru.wav"); }
        }

        /// <summary>
        /// Папка для временных файлов. whisper-cli на Windows получает пути в кодировке ANSI,
        /// поэтому путь должен быть латиницей.
        /// </summary>
        public static string WorkDir
        {
            get
            {
                string[] candidates =
                {
                    Path.Combine(BaseDir, "temp"),
                    Path.Combine(Path.GetTempPath(), "VoiceDictate"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoiceDictate"),
                };
                foreach (string c in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(c);
                        string safe = AsciiPath(c);
                        if (safe != null) return safe;
                    }
                    catch
                    {
                        // пробуем следующую
                    }
                }
                throw new IOException(L.T("не нашлось папки для временных файлов с путём латиницей", "no temp folder with a Latin-only path was found"));
            }
        }

        /// <summary>
        /// При закрытии программы: удалить записи голоса и прочие временные файлы (rec.wav, phrase.wav,
        /// результаты whisper, копии файлов для распознавания).
        /// </summary>
        public static void CleanWorkDir()
        {
            string dir;
            try
            {
                dir = WorkDir;
            }
            catch
            {
                return;
            }
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) TryDelete(f);
                foreach (string d in Directory.GetDirectories(dir)) TryDeleteDir(d);
            }
            catch
            {
                // нет доступа — не страшно
            }
        }

        /// <summary>Список моделей: известные + любые другие .bin в папке models.</summary>
        public static List<WhisperModel> ListModels()
        {
            var list = new List<WhisperModel>(Known);
            try
            {
                if (Directory.Exists(ModelsDir))
                {
                    foreach (string f in Directory.GetFiles(ModelsDir, "*.bin"))
                    {
                        string name = Path.GetFileName(f);
                        if (list.Any(m => string.Equals(m.File, name, StringComparison.OrdinalIgnoreCase))) continue;
                        long mb = new FileInfo(f).Length / (1024 * 1024);
                        list.Add(new WhisperModel(name, name + " (" + mb + " МБ)", name + " (" + mb + " MB)", (int)mb));
                    }
                }
            }
            catch
            {
                // папки нет или нет доступа
            }
            return list;
        }

        // ================================================================ распознавание

        public sealed class Result
        {
            public string Text = "";
            public double Seconds;
            public string Error;      // null — всё хорошо
            public string Details;    // последние строки журнала whisper (при ошибке)
            public string Device = L.T("процессор", "CPU");   // на чём шло распознавание
            public double? Cost;      // сколько стоило (только OpenRouter), долларов
        }

        /// <summary>Распознать WAV-файл. Выполняется в фоне, окно не подвисает.</summary>
        /// <summary>translate — сразу перевести на английский (умеет только Whisper и только на английский).</summary>
        public static Task<Result> TranscribeAsync(string wavPath, string modelPath, string language, string prompt,
            bool translate = false)
        {
            return Task.Run(() => Transcribe(wavPath, modelPath, language, prompt, translate));
        }

        private static Result Transcribe(string wavPath, string modelPath, string language, string prompt, bool translate)
        {
            var res = new Result();
            var sw = Stopwatch.StartNew();

            string cli = CliPath;
            if (!File.Exists(cli))
            {
                res.Error = L.T("не найден ", "not found: ") + cli + L.T(". Папка whisper должна лежать рядом с программой.", ". The whisper folder must be next to the program.");
                return res;
            }
            if (!File.Exists(modelPath))
            {
                res.Error = L.T("модель не скачана: ", "model is not downloaded: ") + Path.GetFileName(modelPath);
                return res;
            }

            string model = AsciiPath(modelPath);
            string wav = AsciiPath(wavPath);
            if (model == null || wav == null)
            {
                res.Error = L.T("в пути к программе есть русские буквы, а whisper такие пути не открывает. ", "the program path contains non-Latin letters, and whisper cannot open such paths. ") +
                            L.T("Переложите папку программы, например, в C:\\VoiceDictate", "Move the program folder, for example, to C:\\VoiceDictate");
                return res;
            }

            string work = WorkDir;
            string outBase = Path.Combine(work, "result");
            string outTxt = outBase + ".txt";
            string rsp = Path.Combine(work, "args.txt");
            try
            {
                if (File.Exists(outTxt)) File.Delete(outTxt);
            }
            catch
            {
                // не страшно
            }

            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            var args = new List<string>
            {
                "-m", model,
                "-f", wav,
                "-l", string.IsNullOrWhiteSpace(language) ? "ru" : language.Trim(),
                "-t", threads.ToString(),
                "-nt",
                "-otxt",
                "-of", outBase,
            };
            if (translate) args.Add("--translate");
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                args.Add("--prompt");
                args.Add(prompt.Replace("\r", " ").Replace("\n", " ").Trim());
            }

            // Аргументы через файл «@args.txt» в UTF-8: так русская подсказка не портится кодировкой
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
            // Кэш скомпилированного драйвером кода видеокарты: для RTX 50xx он большой, по умолчанию не влезает
            psi.EnvironmentVariables["CUDA_CACHE_MAXSIZE"] = "4294967296";

            var log = new StringBuilder();
            var stdout = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                try
                {
                    p.Start();
                }
                catch (Exception ex)
                {
                    res.Error = L.T("не удалось запустить whisper: ", "could not start whisper: ") + ex.Message;
                    return res;
                }
                try
                {
                    p.PriorityClass = ProcessPriorityClass.AboveNormal;
                }
                catch
                {
                    // не дали — работаем с обычным приоритетом
                }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(10 * 60 * 1000))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch
                    {
                        // уже завершился
                    }
                    res.Error = L.T("whisper не ответил за 10 минут", "whisper did not respond within 10 minutes");
                    return res;
                }
                p.WaitForExit();   // дочитать вывод

                res.Seconds = sw.Elapsed.TotalSeconds;
                Match dev = Regex.Match(log.ToString(), @"Device \d+:\s*([^,\r\n]+)");
                if (dev.Success) res.Device = L.T("видеокарта ", "GPU ") + dev.Groups[1].Value.Trim();
                int code = p.ExitCode;
                if (code != 0)
                {
                    res.Error = DescribeExitCode(code);
                    res.Details = Tail(log.ToString(), 6);
                    return res;
                }
            }

            string text = null;
            try
            {
                if (File.Exists(outTxt)) text = File.ReadAllText(outTxt, Encoding.UTF8);
            }
            catch
            {
                // прочитаем из вывода
            }
            if (text == null) text = stdout.ToString();

            res.Text = Clean(text);
            return res;
        }

        internal static string DescribeExitCode(int code)
        {
            uint u = unchecked((uint)code);
            switch (u)
            {
                case 0xC0000135:
                    return L.T("whisper не запустился: не хватает DLL. Поставьте Microsoft Visual C++ Redistributable x64: ", "whisper did not start: missing DLLs. Install Microsoft Visual C++ Redistributable x64: ") +
                           "https://aka.ms/vs/17/release/vc_redist.x64.exe";
                case 0xC000001D:
                    return L.T("whisper не запустился: процессор не поддерживает нужные инструкции", "whisper did not start: the CPU lacks required instructions");
                case 0xC0000005:
                    return L.T("whisper аварийно завершился (код 0xC0000005). Попробуйте другую модель", "whisper crashed (code 0xC0000005). Try another model");
                case 0xC0000017:
                    return L.T("whisper не хватило памяти. Возьмите модель поменьше", "whisper ran out of memory. Pick a smaller model");
                default:
                    return L.T("whisper завершился с ошибкой, код ", "whisper exited with an error, code ") + code;
            }
        }

        internal static string Tail(string text, int lines)
        {
            var all = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }

        /// <summary>
        /// Весь текст — одна «галлюцинация» на тишине («Продолжение следует...» и т. п.).
        /// Для облачных моделей: у них текст одной строкой, поэтому выкидываем только если кроме фразы ничего нет.
        /// </summary>
        public static bool IsOnlyHallucination(string text)
        {
            string letters = Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N} ]", " ");
            letters = Regex.Replace(letters, @"\s+", " ").Trim();
            if (letters.Length == 0) return true;
            return Hallucinations.Any(h => Regex.Replace(h, @"[^\p{L}\p{N} ]", " ").Trim() == letters);
        }

        /// <summary>Склеить строки, убрать пометки вроде [музыка] и фразы-«галлюцинации».</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var parts = new List<string>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = Regex.Replace(raw, @"\[[^\]]*\]|\([^)]*(музык|смех|аплодисмент|шум|music|laugh|applause)[^)]*\)", "",
                    RegexOptions.IgnoreCase).Trim();
                if (line.Length == 0) continue;
                string low = line.ToLowerInvariant();
                if (Hallucinations.Any(h => low.Contains(h))) continue;
                parts.Add(line);
            }
            string joined = string.Join(" ", parts);
            return Regex.Replace(joined, @"\s{2,}", " ").Trim();
        }

        // ================================================================ скачивание модели

        /// <summary>Скачать модель в папку models. progress: доля 0..1 (или -1, если размер неизвестен), МБ.</summary>
        public static async Task DownloadAsync(WhisperModel model, Action<double, double> progress, Action<string> log,
            CancellationToken ct)
        {
            Directory.CreateDirectory(ModelsDir);
            if (model.IsGigaAm)
            {
                await DownloadPartsAsync(model, progress, log, ct);
                return;
            }
            string part = model.FullPath + ".part";
            string[] urls = { ModelUrlHf + model.File, ModelUrlMirror + model.File };
            Exception last = null;

            using (var http = new HttpClient())
            {
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceDictate/1.0");
                foreach (string url in urls)
                {
                    try
                    {
                        log(L.T("Скачиваю ", "Downloading ") + url);
                        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            resp.EnsureSuccessStatusCode();
                            long total = resp.Content.Headers.ContentLength ?? -1;
                            using (var src = await resp.Content.ReadAsStreamAsync())
                            using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
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

                        if (!LooksLikeGgml(part))
                            throw new InvalidDataException(L.T("скачался не файл модели (возможно, страница-заглушка)", "the download is not a model file (maybe a placeholder page)"));
                        if (File.Exists(model.FullPath)) File.Delete(model.FullPath);
                        File.Move(part, model.FullPath);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(part);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        log(L.T("Не получилось: ", "Failed: ") + ex.Message);
                        TryDelete(part);
                    }
                }
            }
            throw new IOException(L.T("не удалось скачать модель: ", "could not download the model: ") + (last != null ? last.Message : L.T("нет ответа", "no response")));
        }

        /// <summary>Модель GigaAM — папка из нескольких файлов: качаем во временную папку, потом переименовываем.</summary>
        private static async Task DownloadPartsAsync(WhisperModel model, Action<double, double> progress, Action<string> log,
            CancellationToken ct)
        {
            string tmpDir = model.FullPath + ".part";
            double totalMb = model.SizeMb;
            Exception last = null;

            using (var http = new HttpClient())
            {
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceDictate/1.0");
                foreach (string baseUrl in new[] { SherpaEngine.GigaAmUrlHf, SherpaEngine.GigaAmUrlMirror })
                {
                    try
                    {
                        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                        Directory.CreateDirectory(tmpDir);
                        log(L.T("Скачиваю ", "Downloading ") + baseUrl);
                        long before = 0;   // байт в уже скачанных файлах
                        foreach (string file in model.Parts)
                        {
                            string path = Path.Combine(tmpDir, file);
                            using (var resp = await http.GetAsync(baseUrl + file, HttpCompletionOption.ResponseHeadersRead, ct))
                            {
                                resp.EnsureSuccessStatusCode();
                                using (var src = await resp.Content.ReadAsStreamAsync())
                                using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
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
                                            double mb = (before + done) / 1048576.0;
                                            progress(Math.Min(0.99, mb / totalMb), mb);
                                        }
                                    }
                                    before += done;
                                }
                            }
                            if (LooksLikeHtml(path))
                                throw new InvalidDataException(L.T("скачался не файл модели (возможно, страница-заглушка)", "the download is not a model file (maybe a placeholder page)"));
                        }
                        progress(1, before / 1048576.0);

                        var probe = new WhisperModel(Path.GetFileName(tmpDir), "", "", 0, model.Parts);
                        if (!probe.Exists)
                            throw new InvalidDataException(L.T("скачались не все файлы модели", "not all model files were downloaded"));
                        SherpaEngine.Unload();   // старая папка могла быть открыта
                        if (Directory.Exists(model.FullPath)) Directory.Delete(model.FullPath, true);
                        Directory.Move(tmpDir, model.FullPath);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        TryDeleteDir(tmpDir);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        log(L.T("Не получилось: ", "Failed: ") + ex.Message);
                        TryDeleteDir(tmpDir);
                    }
                }
            }
            throw new IOException(L.T("не удалось скачать модель: ", "could not download the model: ") + (last != null ? last.Message : L.T("нет ответа", "no response")));
        }

        /// <summary>Вместо файла пришла HTML-страница (заглушка прокси, ошибка сайта).</summary>
        private static bool LooksLikeHtml(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var b = new byte[64];
                    int n = fs.Read(b, 0, b.Length);
                    string head = Encoding.ASCII.GetString(b, 0, n).TrimStart().ToLowerInvariant();
                    return head.StartsWith("<!doctype") || head.StartsWith("<html");
                }
            }
            catch
            {
                return true;
            }
        }

        private static void TryDeleteDir(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
                // пусть лежит
            }
        }

        /// <summary>Скачать и распаковать whisper.cpp с CUDA в папку whisper-cuda.</summary>
        public static async Task DownloadCudaAsync(Action<double, double> progress, Action<string> log, CancellationToken ct)
        {
            string zip = Path.Combine(BaseDir, "whisper-cuda.zip.part");
            string tmpDir = CudaDir + ".tmp";
            try
            {
                log(L.T("Скачиваю ", "Downloading ") + CudaUrl);
                using (var http = new HttpClient())
                {
                    http.Timeout = Timeout.InfiniteTimeSpan;
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceDictate/1.0");
                    using (var resp = await http.GetAsync(CudaUrl, HttpCompletionOption.ResponseHeadersRead, ct))
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
                        }
                    }
                }

                log(L.T("Распаковываю…", "Unpacking…"));
                await Task.Run(() =>
                {
                    if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                    Directory.CreateDirectory(tmpDir);
                    using (ZipArchive za = ZipFile.OpenRead(zip))
                    {
                        foreach (ZipArchiveEntry e in za.Entries)
                        {
                            if (e.Name.Length == 0) continue;   // папка
                            string ext = Path.GetExtension(e.Name).ToLowerInvariant();
                            if (ext != ".dll" && e.Name != "whisper-cli.exe") continue;
                            e.ExtractToFile(Path.Combine(tmpDir, e.Name), true);
                        }
                    }
                    // Visual C++ Runtime берём из обычной папки whisper
                    foreach (string dll in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll" })
                    {
                        string from = Path.Combine(WhisperDir, dll);
                        string to = Path.Combine(tmpDir, dll);
                        if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
                    }
                    if (!File.Exists(Path.Combine(tmpDir, "whisper-cli.exe")) ||
                        !File.Exists(Path.Combine(tmpDir, "ggml-cuda.dll")))
                        throw new InvalidDataException(L.T("в архиве нет whisper-cli.exe или ggml-cuda.dll", "the archive has no whisper-cli.exe or ggml-cuda.dll"));
                    if (Directory.Exists(CudaDir)) Directory.Delete(CudaDir, true);
                    Directory.Move(tmpDir, CudaDir);
                });
            }
            finally
            {
                TryDelete(zip);
                try
                {
                    if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                }
                catch
                {
                    // пусть лежит
                }
            }
        }

        /// <summary>Файл модели whisper.cpp начинается с «lmgg» (0x67676d6c).</summary>
        private static bool LooksLikeGgml(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    if (fs.Length < 1024 * 1024) return false;
                    var b = new byte[4];
                    if (fs.Read(b, 0, 4) != 4) return false;
                    return b[0] == 0x6c && b[1] == 0x6d && b[2] == 0x67 && b[3] == 0x67;
                }
            }
            catch
            {
                return false;
            }
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

        // ================================================================ пути латиницей

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferSize);

        private static bool IsAscii(string s)
        {
            foreach (char c in s)
                if (c > 127) return false;
            return true;
        }

        /// <summary>Путь латиницей: сам путь или его короткое имя 8.3. null — если не получилось.</summary>
        public static string AsciiPath(string path)
        {
            if (IsAscii(path)) return path;
            try
            {
                var sb = new StringBuilder(1024);
                uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
                if (n > 0 && n < sb.Capacity)
                {
                    string s = sb.ToString();
                    if (IsAscii(s)) return s;
                }
            }
            catch
            {
                // не Windows или нет коротких имён
            }
            return null;
        }
    }
}
