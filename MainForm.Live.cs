using System;
using System.IO;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Вставка по фразам (только модель GigaAM): пока идёт запись, после каждой паузы ~0,5 с
    /// сказанная фраза распознаётся и сразу вставляется в окно. Второе нажатие клавиши
    /// распознаёт последний кусок, вставляет его (и жмёт Enter, если включено) и выключает запись.
    /// </summary>
    internal sealed partial class MainForm
    {
        private PauseDetector live;                     // null — запись без вставки по фразам
        private Task liveChain = Task.CompletedTask;    // фразы распознаются и вставляются строго по очереди
        private int liveInserted;                       // сколько фраз уже вставлено за эту запись
        private string liveLast = "";                   // последняя вставленная фраза

        private bool LiveMode
        {
            get
            {
                WhisperModel m = SelectedModel();
                return !settings.Cloud && settings.LivePhrases && m != null && m.IsGigaAm && m.Exists;
            }
        }

        /// <summary>Запись началась.</summary>
        private void LiveStart()
        {
            live = LiveMode ? new PauseDetector() : null;
            liveChain = Task.CompletedTask;
            liveInserted = 0;
            liveLast = "";
        }

        /// <summary>
        /// Модель начинает каждый кусок с заглавной. Если прошлая фраза оборвалась посреди предложения
        /// (без точки), продолжение пишем с маленькой: «…этот текст всё работает», а не «…текст Всё работает».
        /// </summary>
        private static string ContinueSentence(string prev, string text)
        {
            if (prev.Length == 0 || text.Length < 2) return text;
            char end = prev[prev.Length - 1];
            if (end == '.' || end == '!' || end == '?' || end == '…' || end == '»' || end == '"') return text;
            if (!char.IsUpper(text[0]) || !char.IsLower(text[1])) return text;   // «МГУ», «Я» не трогаем
            return char.ToLower(text[0]) + text.Substring(1);
        }

        private short[] RecordedSince(long from)
        {
            return webRecording ? webRecorder.Samples(from) : recorder.Samples(from);
        }

        /// <summary>По таймеру записи: не закончилась ли фраза.</summary>
        private void LiveTick()
        {
            if (live == null) return;
            long from = live.SegStart;
            long cut = live.Feed(RecordedSince(live.ScanPos));
            if (cut < 0) return;
            short[] all = RecordedSince(from);
            var phrase = new short[Math.Min(all.Length, cut - from)];
            Array.Copy(all, phrase, phrase.Length);
            Task before = liveChain;
            liveChain = LivePhrase(before, phrase, false);
        }

        /// <summary>Распознать фразу (после предыдущей) и вставить. true — что-то вставили.</summary>
        private async Task<bool> LivePhrase(Task before, short[] samples, bool final)
        {
            try
            {
                await before;
            }
            catch
            {
                // ошибка прошлой фразы уже в журнале
            }
            try
            {
                string wav = Path.Combine(WhisperEngine.WorkDir, "phrase.wav");
                Recorder.SaveWav(wav, samples);
                WhisperEngine.Result res = await TranscribeAsync(wav);
                if (res.Error != null)
                {
                    Log(L.T("Ошибка распознавания фразы: ", "Phrase recognition error: ") + res.Error);
                    return false;
                }
                if (res.Text.Length == 0 || IsDisposed) return false;
                string text = ContinueSentence(liveLast, res.Text);
                liveLast = text;
                Log(L.T("Фраза за ", "Phrase in ") + res.Seconds.ToString("0.0") + L.T(" с: ", " s: ") + text);
                AppendText(text);
                bool first = liveInserted == 0;
                liveInserted++;
                if (settings.AutoSend) await SendText(final, first ? "" : " ");
                if (recording) UpdateStatus();   // вернуть «Идёт запись» вместо «Отправляю…»
                return true;
            }
            catch (Exception ex)
            {
                Log(L.T("Ошибка: ", "Error: ") + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Второе нажатие при вставке по фразам. false — по ходу записи ничего не вставили:
        /// распознаём всю запись обычным путём (там же проверки на тишину и т. п.).
        /// </summary>
        private async Task<bool> LiveFinish(PauseDetector det, short[] samples)
        {
            await liveChain;
            if (liveInserted == 0) return false;

            long from = Math.Min(det.SegStart, samples.Length);
            var rest = new short[samples.Length - from];
            Array.Copy(samples, from, rest, 0, rest.Length);
            bool inserted = false;
            if (det.ContainsSpeech(rest))
            {
                SetStatus(L.T("Распознаю последнюю фразу…", "Recognizing the last phrase…"), ColorAmber);
                inserted = await LivePhrase(Task.CompletedTask, rest, true);
            }
            if (!inserted && settings.AutoSend && settings.PressEnter)
            {
                await WaitKeysReleased(slotDictate.Key);
                await PressEnterAfterDelay();
                Log(L.T("Enter нажат.", "Enter pressed."));
            }
            if (!settings.AutoSend)
                Log(L.T("Текст в поле. Нажмите «Отправить», когда будете готовы.", "The text is in the field. Click “Send” when ready."));
            return true;
        }
    }

    /// <summary>
    /// Ищет паузы в записи по громкости кадров 20 мс. Порог подстраивается под шум комнаты.
    /// </summary>
    internal sealed class PauseDetector
    {
        private const int Frame = Recorder.SampleRate / 50;   // 20 мс
        private const int PauseFrames = 30;                    // пауза 0,6 с — фраза закончилась (короче — режет на запятых)
        private const int ShortPauseFrames = 8;                // длинную фразу (> 20 с) режем и на паузе 0,16 с
        private const int MinSpeechFrames = 10;                // меньше 0,2 с звука — щелчок, а не речь
        private const int TailFrames = 10;                     // 0,2 с тишины оставляем в конце фразы
        private const int LongPhrase = 20 * Recorder.SampleRate;

        /// <summary>Начало фразы, которую ещё не отдали на распознавание (номер отсчёта).</summary>
        public long SegStart { get; private set; }

        /// <summary>До какого отсчёта запись уже просмотрена.</summary>
        public long ScanPos { get; private set; }

        private int speechFrames, silenceFrames;
        private double noise = 0.01;

        private double Threshold
        {
            get { return Math.Max(0.004, Math.Min(0.04, noise * 4)); }
        }

        /// <summary>
        /// Новые отсчёты, начиная с ScanPos. Возвращает, где резать (конец фразы), или -1.
        /// </summary>
        public long Feed(short[] fresh)
        {
            for (int off = 0; off + Frame <= fresh.Length; off += Frame)
            {
                double rms = Rms(fresh, off);
                if (rms < noise) noise = Math.Max(rms, 0.0003);
                else noise += (rms - noise) * 0.0005;   // шум медленно растёт, речь его почти не сдвигает

                ScanPos += Frame;
                if (rms > Threshold)
                {
                    speechFrames++;
                    silenceFrames = 0;
                    continue;
                }
                silenceFrames++;
                if (speechFrames < MinSpeechFrames)
                {
                    // пока только тишина (или щелчок): не копим её в начале фразы
                    if (silenceFrames > PauseFrames)
                    {
                        SegStart = ScanPos - TailFrames * Frame;
                        speechFrames = 0;
                    }
                    continue;
                }
                bool longPhrase = ScanPos - SegStart > LongPhrase;
                if (silenceFrames >= PauseFrames || (longPhrase && silenceFrames >= ShortPauseFrames))
                {
                    long cut = ScanPos - Math.Max(0, silenceFrames - TailFrames) * Frame;
                    SegStart = cut;
                    speechFrames = 0;
                    silenceFrames = 0;
                    return cut;
                }
            }
            return -1;
        }

        /// <summary>Есть ли в куске речь (хотя бы 0,2 с громче порога).</summary>
        public bool ContainsSpeech(short[] samples)
        {
            int n = 0;
            for (int off = 0; off + Frame <= samples.Length; off += Frame)
                if (Rms(samples, off) > Threshold && ++n >= MinSpeechFrames) return true;
            return false;
        }

        private static double Rms(short[] a, int off)
        {
            double sum = 0;
            for (int i = off; i < off + Frame; i++)
            {
                double v = a[i] / 32768.0;
                sum += v * v;
            }
            return Math.Sqrt(sum / Frame);
        }
    }
}
