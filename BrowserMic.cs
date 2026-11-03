using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Запись через вкладку обычного браузера: если антивирус не даёт звук ни программе, ни её окну,
    /// микрофон открывает браузер (где он работает в чатах ИИ), а звук приходит сюда.
    ///
    /// Крошечный HTTP-сервер только на 127.0.0.1 (наружу не виден):
    ///   GET  /           — страница ui\mic.html;
    ///   GET  /poll?id=   — ждёт команду для страницы (JSON {type:"mic", cmd, name}) до 20 с;
    ///   POST /msg?id=    — сообщение от страницы, те же, что у записи через веб ("pcm|…", "micStarted|…", …).
    /// Страница по адресу http://127.0.0.1 считается браузером «безопасной», поэтому микрофон ей доступен.
    /// </summary>
    internal sealed class BrowserMic : IDisposable
    {
        public const int Port = 47811;   // постоянный: браузер запоминает разрешение на микрофон для адреса с портом
        public static readonly string Url = "http://127.0.0.1:" + Port + "/";

        private const int MaxBody = 4 * 1024 * 1024;
        private readonly Action<string> onMessage;   // вызывается из потоков пула
        private readonly ConcurrentQueue<string> commands = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private TcpListener listener;
        private volatile string activeId;           // вкладка, которая подключилась последней
        private int polling;
        private long lastPoll;

        public BrowserMic(Action<string> onMessage)
        {
            this.onMessage = onMessage;
        }

        /// <summary>Вкладка открыта и ждёт команд.</summary>
        public bool Connected
        {
            get
            {
                if (activeId == null) return false;
                return Volatile.Read(ref polling) > 0 ||
                       DateTime.UtcNow.Ticks - Interlocked.Read(ref lastPoll) < TimeSpan.FromSeconds(5).Ticks;
            }
        }

        public void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch (SocketException ex)
            {
                listener = null;
                throw new InvalidOperationException(ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "порт " + Port + " занят — возможно, программа уже запущена"
                    : ex.Message);
            }
            Task.Run(AcceptLoop);
        }

        /// <summary>Отправить команду странице (JSON).</summary>
        public void Send(string json)
        {
            // старые команды, которые никто не забрал, не нужны: страница должна получить последнюю
            string dummy;
            while (commands.Count > 4 && commands.TryDequeue(out dummy)) { }
            commands.Enqueue(json);
            signal.Release();
        }

        private async Task AcceptLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    if (stop.IsCancellationRequested) return;
                    continue;
                }
                var _ = Task.Run(() => Handle(client));
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream ns = client.GetStream();
                    string method, target;
                    Dictionary<string, string> headers;
                    byte[] rest;
                    if (!ReadHead(ns, out method, out target, out headers, out rest)) return;

                    // только страницы с нашего адреса: чужие сайты не смогут ни читать, ни слать звук
                    string host;
                    headers.TryGetValue("host", out host);
                    if (host != "127.0.0.1:" + Port && host != "localhost:" + Port)
                    {
                        await Reply(ns, 403, "text/plain", "forbidden");
                        return;
                    }
                    string origin;
                    if (headers.TryGetValue("origin", out origin) &&
                        origin != "http://127.0.0.1:" + Port && origin != "http://localhost:" + Port)
                    {
                        await Reply(ns, 403, "text/plain", "forbidden");
                        return;
                    }

                    string path = target, query = "";
                    int q = target.IndexOf('?');
                    if (q >= 0)
                    {
                        path = target.Substring(0, q);
                        query = target.Substring(q + 1);
                    }
                    string id = QueryValue(query, "id");

                    if (method == "GET" && (path == "/" || path == "/mic.html"))
                    {
                        await Reply(ns, 200, "text/html; charset=utf-8", WebUi.LoadHtml("mic.html", false));
                    }
                    else if (method == "GET" && path == "/poll")
                    {
                        await Reply(ns, 200, "application/json; charset=utf-8", await Poll(id));
                    }
                    else if (method == "POST" && path == "/msg")
                    {
                        string len;
                        int n;
                        if (!headers.TryGetValue("content-length", out len) || !int.TryParse(len, out n) ||
                            n < 0 || n > MaxBody)
                        {
                            await Reply(ns, 400, "text/plain", "bad length");
                            return;
                        }
                        byte[] body = ReadBody(ns, rest, n);
                        string msg = Encoding.UTF8.GetString(body);
                        if (msg.StartsWith("hello", StringComparison.Ordinal))
                        {
                            activeId = id;
                            Interlocked.Exchange(ref lastPoll, DateTime.UtcNow.Ticks);
                            string dummy;
                            while (commands.TryDequeue(out dummy)) { }
                        }
                        else if (id == null || id != activeId)
                        {
                            await Reply(ns, 409, "text/plain", "bye");
                            return;
                        }
                        onMessage(msg);
                        await Reply(ns, 200, "text/plain", "ok");
                    }
                    else
                    {
                        await Reply(ns, 404, "text/plain", "not found");
                    }
                }
                catch
                {
                    // вкладку закрыли посреди запроса — не важно
                }
            }
        }

        /// <summary>Длинный опрос: отдать команду, как только появится, или пустой ответ через 20 с.</summary>
        private async Task<string> Poll(string id)
        {
            if (id == null || id != activeId) return "{\"type\":\"bye\"}";
            Interlocked.Increment(ref polling);
            try
            {
                if (await signal.WaitAsync(20000, stop.Token))
                {
                    string cmd;
                    if (id == activeId && commands.TryDequeue(out cmd)) return cmd;
                    if (id != activeId) signal.Release();   // команду заберёт новая вкладка
                }
                return id == activeId ? "{}" : "{\"type\":\"bye\"}";
            }
            catch (OperationCanceledException)
            {
                return "{}";
            }
            finally
            {
                Interlocked.Exchange(ref lastPoll, DateTime.UtcNow.Ticks);
                Interlocked.Decrement(ref polling);
            }
        }

        private static bool ReadHead(Stream s, out string method, out string target,
            out Dictionary<string, string> headers, out byte[] rest)
        {
            method = target = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            rest = new byte[0];
            var buf = new byte[16384];
            int len = 0, end = -1;
            while (end < 0)
            {
                if (len == buf.Length) return false;
                int r = s.Read(buf, len, buf.Length - len);
                if (r <= 0) return false;
                len += r;
                for (int i = Math.Max(0, len - r - 3); i + 3 < len; i++)
                {
                    if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    {
                        end = i;
                        break;
                    }
                }
            }
            string[] lines = Encoding.ASCII.GetString(buf, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return false;
            method = first[0];
            target = first[1];
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) headers[lines[i].Substring(0, c).Trim().ToLowerInvariant()] = lines[i].Substring(c + 1).Trim();
            }
            rest = new byte[len - end - 4];
            Buffer.BlockCopy(buf, end + 4, rest, 0, rest.Length);
            return true;
        }

        private static byte[] ReadBody(Stream s, byte[] rest, int n)
        {
            var body = new byte[n];
            int have = Math.Min(rest.Length, n);
            Buffer.BlockCopy(rest, 0, body, 0, have);
            while (have < n)
            {
                int r = s.Read(body, have, n - have);
                if (r <= 0) throw new IOException("соединение закрыто");
                have += r;
            }
            return body;
        }

        private static async Task Reply(Stream s, int code, string type, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string head = "HTTP/1.1 " + code + " " + (code == 200 ? "OK" : "Error") + "\r\n" +
                          "Content-Type: " + type + "\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length);
            await s.WriteAsync(body, 0, body.Length);
            await s.FlushAsync();
        }

        private static string QueryValue(string query, string key)
        {
            foreach (string part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == key) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        public void Dispose()
        {
            stop.Cancel();
            try
            {
                if (listener != null) listener.Stop();
            }
            catch
            {
                // закрываемся
            }
        }
    }
}
