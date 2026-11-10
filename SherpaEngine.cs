using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;

namespace VoiceDictate
{
    /// <summary>
    /// Распознавание моделью GigaAM v3 (Сбер) через sherpa-onnx — прямо в программе, без whisper-cli.
    /// Только русский язык; знаки препинания и заглавные буквы модель ставит сама.
    /// Модель держим в памяти между записями и выгружаем через несколько минут без дела.
    /// </summary>
    internal static class SherpaEngine
    {
        public const string GigaAmDir = "gigaam-v3-ru";   // папка в models
        public static readonly string[] GigaAmFiles = { "encoder.int8.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" };
        private const string GigaAmRepo = "csukuangfj/sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16/resolve/main/";
        public const string GigaAmUrlHf = "https://huggingface.co/" + GigaAmRepo;
        public const string GigaAmUrlMirror = "https://hf-mirror.com/" + GigaAmRepo;

        private const int Rate = 16000;
        private const int MaxChunkSec = 25;   // модель обучена на коротких фразах: длинную запись режем по паузам
        private static readonly TimeSpan KeepLoaded = TimeSpan.FromMinutes(5);

        private static readonly object sync = new object();
        private static OfflineRecognizer recognizer;
        private static string loadedDir;
        private static Timer unloadTimer;

        public static Task<WhisperEngine.Result> TranscribeAsync(string wavPath, string modelDir)
        {
            return Task.Run(() => Transcribe(wavPath, modelDir));
        }

        private static WhisperEngine.Result Transcribe(string wavPath, string modelDir)
        {
            var res = new WhisperEngine.Result();
            var sw = Stopwatch.StartNew();
            try
            {
                float[] samples;
                int rate;
                ReadWav(wavPath, out samples, out rate);

                var parts = new List<string>();
                lock (sync)
                {
                    if (unloadTimer != null) unloadTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    OfflineRecognizer r = Load(modelDir);
                    foreach (float[] chunk in Split(samples, rate))
                    {
                        using (OfflineStream s = r.CreateStream())
                        {
                            s.AcceptWaveform(rate, chunk);
                            r.Decode(s);
                            string t = (s.Result.Text ?? "").Trim();
                            if (t.Length > 0) parts.Add(t);
                        }
                    }
                    if (unloadTimer == null) unloadTimer = new Timer(_ => Unload());
                    unloadTimer.Change(KeepLoaded, Timeout.InfiniteTimeSpan);
                }
                res.Text = string.Join(" ", parts);
                res.Device = L.T("процессор", "CPU");
            }
            catch (Exception ex)
            {
                res.Error = ex is DllNotFoundException || ex is BadImageFormatException
                    ? L.T("не загрузился sherpa-onnx (нет onnxruntime.dll или sherpa-onnx-c-api.dll рядом с программой)", "sherpa-onnx did not load (onnxruntime.dll or sherpa-onnx-c-api.dll is missing next to the program)")
                    : ex.Message;
            }
            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        private static OfflineRecognizer Load(string dir)
        {
            if (recognizer != null && loadedDir == dir) return recognizer;
            UnloadLocked();
            foreach (string f in GigaAmFiles)
                if (!File.Exists(Path.Combine(dir, f)))
                    throw new FileNotFoundException(L.T("модель не скачана: нет файла ", "model is not downloaded: missing ") + f);

            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = Rate;
            config.FeatConfig.FeatureDim = 64;
            config.ModelConfig.Transducer.Encoder = Path.Combine(dir, GigaAmFiles[0]);
            config.ModelConfig.Transducer.Decoder = Path.Combine(dir, GigaAmFiles[1]);
            config.ModelConfig.Transducer.Joiner = Path.Combine(dir, GigaAmFiles[2]);
            config.ModelConfig.Tokens = Path.Combine(dir, GigaAmFiles[3]);
            config.ModelConfig.ModelType = "nemo_transducer";
            config.ModelConfig.NumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "greedy_search";
            recognizer = new OfflineRecognizer(config);
            loadedDir = dir;
            return recognizer;
        }

        /// <summary>Освободить память (модель ~300 МБ). Следующее распознавание загрузит её снова (~1 с).</summary>
        public static void Unload()
        {
            lock (sync) UnloadLocked();
        }

        private static void UnloadLocked()
        {
            if (recognizer == null) return;
            recognizer.Dispose();
            recognizer = null;
            loadedDir = null;
        }

        /// <summary>Длинную запись — на куски не длиннее MaxChunkSec, разрез в самом тихом месте.</summary>
        private static IEnumerable<float[]> Split(float[] samples, int rate)
        {
            int max = MaxChunkSec * rate, min = max * 3 / 5, frame = rate / 50;   // кадр 20 мс
            int pos = 0;
            while (samples.Length - pos > max)
            {
                int best = pos + max;
                double bestEnergy = double.MaxValue;
                for (int i = pos + min; i + frame <= pos + max; i += frame)
                {
                    double e = 0;
                    for (int k = i; k < i + frame; k++) e += samples[k] * samples[k];
                    if (e < bestEnergy)
                    {
                        bestEnergy = e;
                        best = i + frame / 2;
                    }
                }
                yield return Slice(samples, pos, best - pos);
                pos = best;
            }
            if (samples.Length - pos > rate / 10) yield return Slice(samples, pos, samples.Length - pos);
        }

        private static float[] Slice(float[] a, int from, int count)
        {
            var r = new float[count];
            Array.Copy(a, from, r, 0, count);
            return r;
        }

        /// <summary>WAV PCM 16 бит; стерео сводится в моно. Частоту sherpa-onnx пересчитает сам.</summary>
        private static void ReadWav(string path, out float[] samples, out int rate)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException(L.T("не WAV-файл", "not a WAV file"));
                r.ReadInt32();
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException(L.T("не WAV-файл", "not a WAV file"));
                int channels = 1, bits = 16;
                rate = Rate;
                while (true)
                {
                    string id = Encoding.ASCII.GetString(r.ReadBytes(4));
                    int size = r.ReadInt32();
                    if (id == "fmt ")
                    {
                        int format = r.ReadInt16();
                        channels = r.ReadInt16();
                        rate = r.ReadInt32();
                        r.ReadInt32();
                        r.ReadInt16();
                        bits = r.ReadInt16();
                        if (size > 16) r.ReadBytes(size - 16);
                        if ((format != 1 && format != -2) || bits != 16)
                            throw new InvalidDataException(L.T("нужен WAV PCM 16 бит", "a 16-bit PCM WAV is required"));
                    }
                    else if (id == "data")
                    {
                        long left = r.BaseStream.Length - r.BaseStream.Position;
                        int bytes = (int)Math.Min(size < 0 ? left : size, left);
                        byte[] data = r.ReadBytes(bytes);
                        int n = data.Length / 2 / channels;
                        samples = new float[n];
                        for (int i = 0; i < n; i++)
                        {
                            int sum = 0;
                            for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(data, (i * channels + c) * 2);
                            samples[i] = sum / (32768f * channels);
                        }
                        return;
                    }
                    else
                    {
                        r.ReadBytes(size + (size & 1));
                    }
                }
            }
        }
    }
}
