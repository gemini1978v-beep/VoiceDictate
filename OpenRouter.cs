using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Распознавание в облаке через OpenRouter (https://openrouter.ai): платно, нужен ключ API.
    ///   GET  /models?output_modalities=transcription — модели и цены (без ключа);
    ///   POST /audio/transcriptions — распознать WAV (JSON с base64);
    ///   GET  /credits — баланс аккаунта (только «ключ управления»), иначе GET /key — расход и лимит ключа.
    /// </summary>
    internal static class OpenRouter
    {
        public const string DefaultModel = "openai/whisper-large-v3-turbo";
        public const string KeysUrl = "https://openrouter.ai/settings/keys";
        private const string Api = "https://openrouter.ai/api/v1/";

        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            h.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/voicedictate");
            h.DefaultRequestHeaders.Add("X-Title", "VoiceDictate");
            return h;
        }

        public sealed class Model
        {
            public string Id;
            public string Name;
            public double PerHour = -1;   // долларов за час записи; -1 — цена в токенах, заранее не посчитать

            /// <summary>
            /// Умеет переводить на английский: модели Whisper при языке речи English выдают английский текст,
            /// даже если говорить по-русски. Остальные пишут на том языке, на котором говорят.
            /// </summary>
            public bool Translates
            {
                get { return Id.StartsWith("openai/whisper", StringComparison.OrdinalIgnoreCase); }
            }

            public string Title
            {
                get { return Name + " — " + (PerHour < 0 ? L.T("цена в токенах", "priced per token") : FormatUsd(PerHour) + L.T("/ч", "/h")); }
            }
        }

        /// <summary>Модели на случай, если список с сайта не загрузился (цены на ноябрь 2026).</summary>
        public static List<Model> FallbackModels()
        {
            return new List<Model>
            {
                new Model { Id = "openai/whisper-large-v3-turbo", Name = "Whisper Large V3 Turbo", PerHour = 0.012 },
                new Model { Id = "qwen/qwen3-asr-1.7b", Name = "Qwen3 ASR 1.7B", PerHour = 0.027 },
                new Model { Id = "openai/whisper-large-v3", Name = "Whisper Large V3", PerHour = 0.027 },
                new Model { Id = "mistralai/voxtral-mini-3b-2507", Name = "Voxtral Mini 3B 2507", PerHour = 0.06 },
                new Model { Id = "microsoft/mai-transcribe-2", Name = "MAI-Transcribe 2", PerHour = 0.10 },
                new Model { Id = "mistralai/voxtral-mini-transcribe", Name = "Voxtral Mini Transcribe", PerHour = 0.18 },
                new Model { Id = "elevenlabs/scribe-v2", Name = "Scribe v2", PerHour = 0.22 },
                new Model { Id = "openai/gpt-transcribe", Name = "GPT Transcribe", PerHour = 0.27 },
            };
        }

        /// <summary>Все модели распознавания с ценой за час, дешёвые сверху.</summary>
        public static async Task<List<Model>> ListModelsAsync()
        {
            string json = await http.GetStringAsync(Api + "models?output_modalities=transcription");
            var list = new List<Model>();
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                foreach (JsonElement m in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    string id = Str(m, "id");
                    if (string.IsNullOrEmpty(id) || !IsSpeechToText(m)) continue;
                    string name = Str(m, "name") ?? id;
                    int colon = name.IndexOf(": ", StringComparison.Ordinal);
                    if (colon > 0) name = name.Substring(colon + 2);   // «OpenAI: Whisper…» → «Whisper…»

                    double perHour = -1;
                    JsonElement p;
                    if (m.TryGetProperty("pricing", out p))
                    {
                        double prompt = Num(p, "prompt"), completion = Num(p, "completion");
                        // Обычно цена — за секунду записи. У некоторых моделей (MAI-Transcribe) указана сразу
                        // цена за час: за секунду таких денег не бывает. Модели с ценой за выход — в токенах.
                        if (completion <= 0 && prompt > 0)
                            perHour = prompt >= 0.01 ? prompt : prompt * 3600;
                    }
                    list.Add(new Model { Id = id, Name = name, PerHour = perHour });
                }
            }
            SortModels(list);
            return list;
        }

        /// <summary>Сначала модели с переводом, внутри групп — дешёвые сверху.</summary>
        public static void SortModels(List<Model> list)
        {
            list.Sort((a, b) =>
            {
                if (a.Translates != b.Translates) return a.Translates ? -1 : 1;
                if ((a.PerHour < 0) != (b.PerHour < 0)) return a.PerHour < 0 ? 1 : -1;
                int c = a.PerHour.CompareTo(b.PerHour);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// Только модели распознавания речи: на входе звук, на выходе расшифровка, и их можно вызвать
        /// через /audio/transcriptions. Сайт и так отдаёт такие по фильтру, но проверяем сами —
        /// чтобы в список не попали чат-модели, если фильтр на сайте поменяется.
        /// </summary>
        private static bool IsSpeechToText(JsonElement m)
        {
            JsonElement arch, list;
            if (!m.TryGetProperty("architecture", out arch) || arch.ValueKind != JsonValueKind.Object) return false;
            if (!Contains(arch, "input_modalities", "audio")) return false;
            if (!Contains(arch, "output_modalities", "transcription")) return false;
            if (!m.TryGetProperty("endpoints", out list) || list.ValueKind != JsonValueKind.Array)
                return true;   // старый формат ответа без списка адресов — хватает модальностей
            foreach (JsonElement e in list.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString().EndsWith("/audio/transcriptions", StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool Contains(JsonElement obj, string name, string value)
        {
            JsonElement arr;
            if (!obj.TryGetProperty(name, out arr) || arr.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() == value) return true;
            return false;
        }

        public sealed class Balance
        {
            public string Text;    // главное: «$4,12» или «нет данных»
            public string Note;    // пояснение под ним
            public string Error;   // ключ не подошёл, нет сети…
        }

        /// <summary>Сколько денег осталось. Полный баланс аккаунта отдаётся только «ключу управления».</summary>
        public static async Task<Balance> GetBalanceAsync(string key)
        {
            var b = new Balance();
            try
            {
                using (HttpResponseMessage r = await Send(HttpMethod.Get, "credits", key, null))
                {
                    string body = await r.Content.ReadAsStringAsync();
                    if (r.IsSuccessStatusCode)
                    {
                        using (JsonDocument doc = JsonDocument.Parse(body))
                        {
                            JsonElement d = doc.RootElement.GetProperty("data");
                            double left = Num(d, "total_credits") - Num(d, "total_usage");
                            b.Text = FormatUsd(left);
                            b.Note = L.T("Баланс аккаунта. Пополнено ", "Account balance. Topped up ") + FormatUsd(Num(d, "total_credits")) +
                                     L.T(", потрачено ", ", spent ") + FormatUsd(Num(d, "total_usage")) + ".";
                            return b;
                        }
                    }
                    if (r.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        b.Error = L.T("ключ не подходит — проверьте, что он скопирован целиком", "the key is invalid — make sure it was copied completely");
                        return b;
                    }
                }

                // обычный ключ: /credits не отдаёт баланс (403) — берём расход и лимит самого ключа
                using (HttpResponseMessage r = await Send(HttpMethod.Get, "key", key, null))
                {
                    string body = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                    {
                        b.Error = ErrorText(r.StatusCode, body);
                        return b;
                    }
                    using (JsonDocument doc = JsonDocument.Parse(body))
                    {
                        JsonElement d = doc.RootElement.GetProperty("data");
                        double usage = Num(d, "usage");
                        double left = -1;
                        JsonElement v, eb;
                        if (d.TryGetProperty("limit_remaining", out v) && v.ValueKind == JsonValueKind.Number)
                            left = v.GetDouble();
                        else if (d.TryGetProperty("effective_budget", out eb) && eb.ValueKind == JsonValueKind.Object &&
                                 eb.TryGetProperty("remaining_usd", out v) && v.ValueKind == JsonValueKind.Number)
                            left = v.GetDouble();

                        if (left >= 0)
                        {
                            b.Text = FormatUsd(left);
                            b.Note = L.T("Остаток лимита этого ключа. Им потрачено ", "Remaining limit of this key. Spent with it: ") + FormatUsd(usage) + ".";
                        }
                        else
                        {
                            b.Text = L.T("потрачено ", "spent ") + FormatUsd(usage);
                            b.Note = L.T("Баланс аккаунта OpenRouter показывает только ключу управления (Management key) ", "OpenRouter shows the account balance only to a Management key ") +
                                     L.T("или ключу с лимитом трат. Сейчас виден расход этого ключа.", "or a key with a spending limit. Showing what this key has spent.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                b.Error = Friendly(ex);
            }
            return b;
        }

        /// <summary>Распознать WAV в облаке. Ошибки возвращаются в Result.Error.</summary>
        public static async Task<WhisperEngine.Result> TranscribeAsync(string wavPath, string key, string model,
            string modelTitle, string language)
        {
            var res = new WhisperEngine.Result { Device = "OpenRouter, " + modelTitle };
            var sw = Stopwatch.StartNew();
            try
            {
                string audio = Convert.ToBase64String(File.ReadAllBytes(wavPath));
                string lang = (language ?? "").Trim().ToLowerInvariant();
                string body = "{\"model\":" + JsonSerializer.Serialize(model) +
                              ",\"input_audio\":{\"data\":\"" + audio + "\",\"format\":\"wav\"}" +
                              (lang.Length > 0 && lang != "auto" ? ",\"language\":" + JsonSerializer.Serialize(lang) : "") +
                              ",\"temperature\":0}";

                using (HttpResponseMessage r = await Send(HttpMethod.Post, "audio/transcriptions", key, body))
                {
                    string text = await r.Content.ReadAsStringAsync();
                    if (!r.IsSuccessStatusCode)
                    {
                        res.Error = ErrorText(r.StatusCode, text);
                        return res;
                    }
                    using (JsonDocument doc = JsonDocument.Parse(text))
                    {
                        res.Text = (Str(doc.RootElement, "text") ?? "").Trim();
                        if (WhisperEngine.IsOnlyHallucination(res.Text)) res.Text = "";   // «Продолжение следует...» на тишине
                        JsonElement u;
                        if (doc.RootElement.TryGetProperty("usage", out u) && u.ValueKind == JsonValueKind.Object)
                        {
                            JsonElement c;
                            if (u.TryGetProperty("cost", out c) && c.ValueKind == JsonValueKind.Number) res.Cost = c.GetDouble();
                        }
                    }
                }
                if (res.Cost.HasValue) res.Device += ", " + FormatUsd(res.Cost.Value);
            }
            catch (Exception ex)
            {
                res.Error = Friendly(ex);
            }
            finally
            {
                res.Seconds = sw.Elapsed.TotalSeconds;
            }
            return res;
        }

        private static async Task<HttpResponseMessage> Send(HttpMethod method, string path, string key, string json)
        {
            var req = new HttpRequestMessage(method, Api + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await http.SendAsync(req);
        }

        private static string ErrorText(HttpStatusCode code, string body)
        {
            string msg = null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(body))
                {
                    JsonElement e;
                    if (doc.RootElement.TryGetProperty("error", out e))
                        msg = e.ValueKind == JsonValueKind.Object ? Str(e, "message") : e.ToString();
                }
            }
            catch
            {
                // не JSON
            }
            switch ((int)code)
            {
                case 401: return L.T("ключ OpenRouter не подходит (", "the OpenRouter key is invalid (") + (msg ?? "401") + ")";
                case 402: return L.T("на балансе OpenRouter закончились деньги — пополните на openrouter.ai", "your OpenRouter balance is empty — top it up at openrouter.ai");
                case 429: return L.T("OpenRouter просит подождать: слишком много запросов", "OpenRouter asks to wait: too many requests");
            }
            return "OpenRouter: " + (int)code + (msg != null ? " — " + msg : "");
        }

        private static string Friendly(Exception ex)
        {
            if (ex is TaskCanceledException) return L.T("OpenRouter не ответил за 2 минуты", "OpenRouter did not respond within 2 minutes");
            if (ex is HttpRequestException) return L.T("нет связи с OpenRouter (", "cannot reach OpenRouter (") + ex.Message + ")";
            return ex.Message;
        }

        /// <summary>Доллары: мелкие суммы с тремя-четырьмя знаками, остальные — с двумя.</summary>
        public static string FormatUsd(double v)
        {
            var ru = CultureInfo.GetCultureInfo(L.En ? "en-US" : "ru-RU");
            double a = Math.Abs(v);
            string s = a == 0 ? "0" : a < 0.001 ? v.ToString("0.00000", ru) : a < 0.1 ? v.ToString("0.0##", ru) : v.ToString("0.00", ru);
            return "$" + s;
        }

        /// <summary>Ключ для показа: «sk-or-v1-ab…wxyz».</summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 14) return "••••";
            return key.Substring(0, 11) + "…" + key.Substring(key.Length - 4);
        }

        private static string Str(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static double Num(JsonElement e, string name)
        {
            JsonElement v;
            if (!e.TryGetProperty(name, out v)) return 0;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            double d;
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return 0;
        }
    }
}
