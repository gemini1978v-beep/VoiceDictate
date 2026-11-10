using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace VoiceDictate
{
    /// <summary>Настройки программы, хранятся в settings.ini рядом с exe.</summary>
    internal sealed class Settings
    {
        public const string DefaultPrompt =
            "Привет. Это текст на русском языке, с правильной пунктуацией: запятыми, точками и вопросами?";

        public string HotkeyDictate = "F3";   // 1-е нажатие — запись, 2-е — распознать и отправить
        public string Model = "ggml-small-q5_1.bin";
        public bool Cloud = false;            // engine = openrouter — распознавать в облаке OpenRouter (платно)
        public string OpenRouterKey = "";     // ключ API OpenRouter (sk-or-v1-…)
        public string OpenRouterModel = OpenRouter.DefaultModel;
        public int MicDevice = -1;            // -1 — микрофон по умолчанию
        public string MicSource = "direct";   // запись звука: direct — WinAPI, web — страница окна, browser — вкладка браузера
        public const string DefaultPromptEn =
            "Hello. This is text in English, with correct punctuation: commas, periods, and questions?";

        public string UiLang;                 // ui_lang = ru / en — язык окна; по умолчанию как у Windows
        public string Language;               // язык речи; по умолчанию ru для русского окна, иначе auto
        public string Prompt = DefaultPrompt;
        public bool Translate = false;        // translate = yes — переводить распознанный текст
        public string TranslateTo;            // на какой язык; по умолчанию «другой» из ru/en
        public string TranslateModel = OpenRouter.DefaultTranslateModel;   // для языков кроме английского

        /// <summary>
        /// Подсказка для Whisper. Стандартная русская мешает распознавать другие языки,
        /// поэтому для английского берём английскую, для остальных — никакой.
        /// </summary>
        public string EffectivePrompt
        {
            get
            {
                if (Prompt != DefaultPrompt || Language == "ru") return Prompt;
                return Language == "en" ? DefaultPromptEn : "";
            }
        }
        public bool UseGpu = true;            // видеокарта NVIDIA, если скачано ускорение
        public bool AutoSend = true;          // сразу отправлять распознанный текст
        public bool PressEnter = false;
        public double EnterDelay = 0.2;       // секунд от вставки до Enter
        public bool TypeText = false;
        public bool LivePhrases = true;       // GigaAM: вставлять каждую фразу после паузы, не дожидаясь конца записи
        public bool TopMost = true;
        public bool ClassicUi = false;        // ui = classic — старое окно вместо HTML-интерфейса
        public string Theme = "light";       // тема окна: light / dark
        public bool TrayHintShown = false;    // подсказку «работаю в трее» уже показывали
        public bool FileSrt = false;          // распознавание файлов: ещё и субтитры .srt

        public static string FilePath
        {
            get { return Path.Combine(AppContext.BaseDirectory, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s.WithDefaults();
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "hotkey_dictate":
                            if (val.Length > 0) s.HotkeyDictate = val;
                            break;
                        case "model":
                            if (val.Length > 0) s.Model = val;
                            break;
                        case "mic_device":
                            int dev;
                            if (int.TryParse(val, out dev)) s.MicDevice = dev;
                            break;
                        case "engine":
                            s.Cloud = val.Trim().ToLowerInvariant() == "openrouter";
                            break;
                        case "openrouter_key":
                            s.OpenRouterKey = val;
                            break;
                        case "openrouter_model":
                            if (val.Length > 0) s.OpenRouterModel = val;
                            break;
                        case "mic_source":
                            string src = val.Trim().ToLowerInvariant();
                            s.MicSource = src == "web" || src == "browser" ? src : "direct";
                            break;
                        case "ui_lang":
                            s.UiLang = val.Trim().ToLowerInvariant();
                            break;
                        case "translate":
                            s.Translate = ParseBool(val);
                            break;
                        case "translate_to":
                            if (val.Length > 0) s.TranslateTo = val.Trim().ToLowerInvariant();
                            break;
                        case "translate_model":
                            if (val.Length > 0) s.TranslateModel = val;
                            break;
                        case "language":
                            if (val.Length > 0) s.Language = val;
                            break;
                        case "prompt":
                            s.Prompt = val;
                            break;
                        case "gpu":
                            s.UseGpu = ParseBool(val);
                            break;
                        case "auto_send":
                            s.AutoSend = ParseBool(val);
                            break;
                        case "press_enter":
                            s.PressEnter = ParseBool(val);
                            break;
                        case "enter_delay":
                            double d;
                            if (double.TryParse(val.Replace(',', '.'), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out d) && d >= 0 && d <= 10)
                                s.EnterDelay = d;
                            break;
                        case "type_text":
                            s.TypeText = ParseBool(val);
                            break;
                        case "live_phrases":
                            s.LivePhrases = ParseBool(val);
                            break;
                        case "topmost":
                            s.TopMost = ParseBool(val);
                            break;
                        case "file_srt":
                            s.FileSrt = ParseBool(val);
                            break;
                        case "tray_hint_shown":
                            s.TrayHintShown = ParseBool(val);
                            break;
                        case "theme":
                            s.Theme = val.Trim().ToLowerInvariant() == "dark" ? "dark" : "light";
                            break;
                        case "ui":
                            s.ClassicUi = val.Trim().ToLowerInvariant() == "classic";
                            break;
                        // hotkey_send и auto_winh от старых версий больше не используются
                    }
                }
            }
            catch
            {
                // битый файл — работаем на значениях по умолчанию
            }
            return s.WithDefaults();
        }

        private Settings WithDefaults()
        {
            if (UiLang != "ru" && UiLang != "en") UiLang = L.SystemDefault();
            if (string.IsNullOrEmpty(Language)) Language = UiLang == "ru" ? "ru" : "auto";
            if (string.IsNullOrEmpty(TranslateTo)) TranslateTo = Language == "en" ? "ru" : "en";
            return this;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; Настройки программы. Их удобнее менять прямо в окне.");
            sb.AppendLine("[general]");
            sb.AppendLine("; Язык окна / Window language: ru, en");
            sb.AppendLine("ui_lang = " + UiLang);
            sb.AppendLine("; Клавиша: 1-е нажатие — запись с микрофона, 2-е — распознать (Whisper) и отправить");
            sb.AppendLine("hotkey_dictate = " + HotkeyDictate);
            sb.AppendLine("; Файл модели Whisper в папке models");
            sb.AppendLine("model = " + Model);
            sb.AppendLine("; Где распознавать: local — на компьютере (Whisper, бесплатно), openrouter — в облаке OpenRouter (платно)");
            sb.AppendLine("engine = " + (Cloud ? "openrouter" : "local"));
            sb.AppendLine("; Ключ API OpenRouter (https://openrouter.ai/settings/keys). Храните файл в надёжном месте");
            sb.AppendLine("openrouter_key = " + OpenRouterKey);
            sb.AppendLine("; Модель OpenRouter для распознавания, например openai/whisper-large-v3-turbo");
            sb.AppendLine("openrouter_model = " + OpenRouterModel);
            sb.AppendLine("; Микрофон: -1 — по умолчанию, 0, 1, 2… — номер в списке");
            sb.AppendLine("mic_device = " + MicDevice);
            sb.AppendLine("; Запись звука: direct — напрямую (WinAPI), web — через веб-движок окна,");
            sb.AppendLine(";   browser — через вкладку обычного браузера (если антивирус режет звук программе)");
            sb.AppendLine("mic_source = " + MicSource);
            sb.AppendLine("; Язык речи: ru, en, uk… или auto");
            sb.AppendLine("language = " + Language);
            sb.AppendLine("; Переводить распознанный текст (yes / no) и на какой язык (en, ru, de…)");
            sb.AppendLine("translate = " + (Translate ? "yes" : "no"));
            sb.AppendLine("translate_to = " + TranslateTo);
            sb.AppendLine("; Модель OpenRouter для перевода на языки кроме английского (на английский переводит сам Whisper)");
            sb.AppendLine("translate_model = " + TranslateModel);
            sb.AppendLine("; Подсказка для Whisper: пример текста в нужном стиле (помогает с пунктуацией)");
            sb.AppendLine("prompt = " + Prompt);
            sb.AppendLine("; Распознавать на видеокарте NVIDIA, если скачано ускорение (папка whisper-cuda) (yes / no)");
            sb.AppendLine("gpu = " + (UseGpu ? "yes" : "no"));
            sb.AppendLine("; Сразу отправлять распознанный текст в активное окно (yes / no)");
            sb.AppendLine("auto_send = " + (AutoSend ? "yes" : "no"));
            sb.AppendLine("; Нажимать Enter после вставки (yes / no)");
            sb.AppendLine("press_enter = " + (PressEnter ? "yes" : "no"));
            sb.AppendLine("; Задержка перед Enter, секунды (например 0.2)");
            sb.AppendLine("enter_delay = " + EnterDelay.ToString("0.##", CultureInfo.InvariantCulture));
            sb.AppendLine("; Печатать посимвольно вместо Ctrl+V (yes / no)");
            sb.AppendLine("type_text = " + (TypeText ? "yes" : "no"));
            sb.AppendLine("; Модель GigaAM: вставлять текст по фразам — после каждой паузы, не дожидаясь второго нажатия (yes / no)");
            sb.AppendLine("live_phrases = " + (LivePhrases ? "yes" : "no"));
            sb.AppendLine("; Окно программы поверх остальных (yes / no)");
            sb.AppendLine("topmost = " + (TopMost ? "yes" : "no"));
            sb.AppendLine("; Интерфейс: web — новый (нужен WebView2, есть в Windows 10/11), classic — старое окно");
            sb.AppendLine("ui = " + (ClassicUi ? "classic" : "web"));
            sb.AppendLine("; Тема окна: light — светлая, dark — тёмная (переключатель рядом с шестерёнкой)");
            sb.AppendLine("theme = " + Theme);
            sb.AppendLine("; Подсказку про трей уже показывали (yes / no)");
            sb.AppendLine("tray_hint_shown = " + (TrayHintShown ? "yes" : "no"));
            sb.AppendLine("; Распознавание файлов: кроме .txt сохранять субтитры .srt с таймкодами (yes / no)");
            sb.AppendLine("file_srt = " + (FileSrt ? "yes" : "no"));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }

        private static bool ParseBool(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "yes" || v == "true" || v == "да" || v == "on";
        }
    }
}
