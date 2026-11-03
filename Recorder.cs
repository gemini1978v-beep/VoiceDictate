using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VoiceDictate
{
    /// <summary>
    /// Запись с микрофона через WinAPI waveIn: 16 кГц, моно, 16 бит — ровно то, что нужно Whisper.
    /// Буферы опрашиваются таймером (Poll) из потока окна, без колбэков из чужих потоков.
    /// </summary>
    internal sealed class Recorder : IDisposable
    {
        public const int SampleRate = 16000;
        private const int BufferMs = 100;
        private const int BufferCount = 20;
        private const int BufferBytes = SampleRate * 2 * BufferMs / 1000;   // 3200 байт

        private const int MMSYSERR_NOERROR = 0;
        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_NULL = 0;
        private const int WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [DllImport("winmm.dll")]
        private static extern int waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveInGetDevCaps(IntPtr uDeviceID, ref WAVEINCAPS pwic, int cbwic);

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx,
            IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        private static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        private static readonly int OffFlags = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
        private static readonly int OffRecorded = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwBytesRecorded");

        private IntPtr hwi = IntPtr.Zero;
        private readonly IntPtr[] headers = new IntPtr[BufferCount];
        private readonly IntPtr[] datas = new IntPtr[BufferCount];
        private readonly Queue<int> order = new Queue<int>();   // порядок, в котором буферы заполняются
        private MemoryStream pcm;
        private readonly byte[] chunk = new byte[BufferBytes];

        public bool IsRecording { get; private set; }

        /// <summary>Пиковый уровень последнего куска, 0..1 (для индикатора громкости).</summary>
        public float LastLevel { get; private set; }

        /// <summary>Пиковый уровень за всю запись, 0..1.</summary>
        public float MaxLevel { get; private set; }

        public double Seconds
        {
            get { return pcm == null ? 0 : pcm.Length / 2.0 / SampleRate; }
        }

        /// <summary>Список микрофонов: индекс в списке = номер устройства.</summary>
        public static List<string> GetDevices()
        {
            var list = new List<string>();
            try
            {
                int n = waveInGetNumDevs();
                for (int i = 0; i < n; i++)
                {
                    var caps = new WAVEINCAPS();
                    if (waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == MMSYSERR_NOERROR)
                        list.Add(caps.szPname);
                    else
                        list.Add("Микрофон " + (i + 1));
                }
            }
            catch
            {
                // нет winmm — вернём пустой список
            }
            return list;
        }

        /// <summary>Начать запись. device = -1 — микрофон по умолчанию.</summary>
        public void Start(int device)
        {
            if (IsRecording) return;

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1,   // PCM
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = SampleRate * 2,
                cbSize = 0,
            };
            uint dev = device < 0 ? WAVE_MAPPER : (uint)device;
            int rc = waveInOpen(out hwi, dev, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (rc != MMSYSERR_NOERROR)
            {
                hwi = IntPtr.Zero;
                throw new InvalidOperationException(rc == 2 || rc == 6
                    ? "микрофон не найден (код " + rc + ")"
                    : "не удалось открыть микрофон (код " + rc + ")");
            }

            pcm = new MemoryStream();
            LastLevel = 0;
            MaxLevel = 0;
            order.Clear();
            try
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    datas[i] = Marshal.AllocHGlobal(BufferBytes);
                    headers[i] = Marshal.AllocHGlobal(HdrSize);
                    var h = new WAVEHDR { lpData = datas[i], dwBufferLength = BufferBytes };
                    Marshal.StructureToPtr(h, headers[i], false);
                    Check(waveInPrepareHeader(hwi, headers[i], HdrSize), "waveInPrepareHeader");
                    Check(waveInAddBuffer(hwi, headers[i], HdrSize), "waveInAddBuffer");
                    order.Enqueue(i);
                }
                Check(waveInStart(hwi), "waveInStart");
                IsRecording = true;
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        /// <summary>Забрать заполненные буферы. Вызывать по таймеру во время записи.</summary>
        public void Poll()
        {
            if (!IsRecording) return;
            Drain(true);
        }

        /// <summary>Остановить запись и вернуть звук (16 кГц, моно, 16 бит).</summary>
        public short[] Stop()
        {
            if (!IsRecording) return new short[0];
            IsRecording = false;

            Drain(true);
            waveInStop(hwi);              // текущий недозаполненный буфер помечается готовым
            System.Threading.Thread.Sleep(30);
            Drain(false);
            Cleanup();

            byte[] bytes = pcm.ToArray();
            pcm = null;
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return samples;
        }

        private void Drain(bool requeue)
        {
            while (order.Count > 0)
            {
                int i = order.Peek();
                IntPtr h = headers[i];
                int flags = Marshal.ReadInt32(h, OffFlags);
                if ((flags & WHDR_DONE) == 0) break;
                order.Dequeue();

                int n = Marshal.ReadInt32(h, OffRecorded);
                if (n > 0)
                {
                    n = Math.Min(n, BufferBytes);
                    Marshal.Copy(datas[i], chunk, 0, n);
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

                if (requeue)
                {
                    waveInUnprepareHeader(hwi, h, HdrSize);
                    var hdr = new WAVEHDR { lpData = datas[i], dwBufferLength = BufferBytes };
                    Marshal.StructureToPtr(hdr, h, false);
                    if (waveInPrepareHeader(hwi, h, HdrSize) == MMSYSERR_NOERROR &&
                        waveInAddBuffer(hwi, h, HdrSize) == MMSYSERR_NOERROR)
                        order.Enqueue(i);
                }
            }
        }

        private void Cleanup()
        {
            IsRecording = false;
            if (hwi != IntPtr.Zero)
            {
                waveInReset(hwi);   // вернуть все буферы из очереди
                for (int i = 0; i < BufferCount; i++)
                    if (headers[i] != IntPtr.Zero)
                        waveInUnprepareHeader(hwi, headers[i], HdrSize);
                waveInClose(hwi);
                hwi = IntPtr.Zero;
            }
            for (int i = 0; i < BufferCount; i++)
            {
                if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
                headers[i] = IntPtr.Zero;
                datas[i] = IntPtr.Zero;
            }
            order.Clear();
        }

        private static void Check(int rc, string what)
        {
            if (rc != MMSYSERR_NOERROR) throw new InvalidOperationException(what + ": код " + rc);
        }

        public void Dispose()
        {
            if (IsRecording) Stop();
            else Cleanup();
        }

        /// <summary>Записать WAV (PCM 16 бит, моно, 16 кГц).</summary>
        public static void SaveWav(string path, short[] samples)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                int dataBytes = samples.Length * 2;
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);              // PCM
                w.Write((short)1);              // моно
                w.Write(SampleRate);
                w.Write(SampleRate * 2);        // байт в секунду
                w.Write((short)2);              // выравнивание блока
                w.Write((short)16);             // бит на отсчёт
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(dataBytes);
                var buf = new byte[dataBytes];
                Buffer.BlockCopy(samples, 0, buf, 0, dataBytes);
                w.Write(buf);
            }
        }
    }
}
