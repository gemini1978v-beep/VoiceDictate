using System;
using System.IO;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Запись «через веб»: микрофон открывает страница в WebView2 (getUserMedia, как в браузере),
    /// а не сама программа. Нужна, когда антивирус (например, Касперский) не даёт программе звук напрямую.
    /// Страница присылает куски звука 16 кГц, моно, 16 бит в base64: "pcm|...".
    ///
    /// Программа → страница: {type: "mic", cmd: "start" | "stop", name}.
    /// Страница → программа: "micStarted|название", "micError|текст", "pcm|base64", "micStopped".
    /// </summary>
    internal sealed class WebRecorder
    {
        /// <summary>Куда слать команды: странице окна (WebView2) или вкладке браузера (BrowserMic).</summary>
        public Action<string> Target;
        private MemoryStream pcm;
        private TaskCompletionSource<string> startWait;
        private TaskCompletionSource<bool> stopWait;

        private void post(string json)
        {
            if (Target != null) Target(json);
        }

        public bool IsRecording { get; private set; }

        /// <summary>Микрофон, который открыла страница.</summary>
        public string DeviceName { get; private set; }

        public float LastLevel { get; private set; }

        public float MaxLevel { get; private set; }

        public double Seconds
        {
            get { return pcm == null ? 0 : pcm.Length / 2.0 / Recorder.SampleRate; }
        }

        /// <summary>Начать запись. micName — название микрофона из списка Windows, пусто — по умолчанию.</summary>
        public async Task Start(string micName)
        {
            if (IsRecording) return;
            pcm = new MemoryStream();
            LastLevel = 0;
            MaxLevel = 0;
            DeviceName = null;
            startWait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            IsRecording = true;
            post(new MainForm.Js().S("type", "mic").S("cmd", "start").S("name", micName ?? "").ToString());

            Task done = await Task.WhenAny(startWait.Task, Task.Delay(10000));
            string error = done == startWait.Task ? startWait.Task.Result : L.T("страница с микрофоном не ответила", "the microphone page did not respond");
            startWait = null;
            if (error != null)
            {
                IsRecording = false;
                pcm = null;
                post(new MainForm.Js().S("type", "mic").S("cmd", "stop").ToString());
                throw new InvalidOperationException(error);
            }
        }

        /// <summary>Остановить запись и вернуть звук (16 кГц, моно, 16 бит).</summary>
        public async Task<short[]> Stop()
        {
            if (!IsRecording) return new short[0];
            stopWait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            post(new MainForm.Js().S("type", "mic").S("cmd", "stop").ToString());
            // страница досылает хвост записи и отвечает "micStopped"
            await Task.WhenAny(stopWait.Task, Task.Delay(2000));
            stopWait = null;
            IsRecording = false;

            byte[] bytes = pcm.ToArray();
            pcm = null;
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return samples;
        }

        /// <summary>Сообщение от страницы. Возвращает текст ошибки, если микрофон сломался посреди записи.</summary>
        public string OnMessage(string cmd, string arg)
        {
            switch (cmd)
            {
                case "micStarted":
                    DeviceName = arg;
                    if (startWait != null) startWait.TrySetResult(null);
                    break;
                case "micError":
                    if (startWait != null)
                    {
                        startWait.TrySetResult(arg.Length > 0 ? arg : L.T("неизвестная ошибка", "unknown error"));
                        break;
                    }
                    if (IsRecording) return arg;
                    break;
                case "micStopped":
                    if (stopWait != null) stopWait.TrySetResult(true);
                    break;
                case "pcm":
                    if (IsRecording && pcm != null) AddChunk(arg);
                    break;
            }
            return null;
        }

        private void AddChunk(string base64)
        {
            byte[] chunk;
            try
            {
                chunk = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return;
            }
            int n = chunk.Length & ~1;
            pcm.Write(chunk, 0, n);
            int peak = 0;
            for (int k = 0; k + 1 < n; k += 2)
            {
                int s = Math.Abs((int)(short)(chunk[k] | (chunk[k + 1] << 8)));
                if (s > peak) peak = s;
            }
            LastLevel = peak / 32768f;
            if (LastLevel > MaxLevel) MaxLevel = LastLevel;
        }
    }
}
