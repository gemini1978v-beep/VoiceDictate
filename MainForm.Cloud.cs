using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace VoiceDictate
{
    /// <summary>
    /// Где распознавать: на компьютере (Whisper) или в облаке OpenRouter (OpenRouter.cs).
    /// Ключ, модель и выбор хранятся в settings.ini.
    /// </summary>
    internal sealed partial class MainForm
    {
        private List<OpenRouter.Model> orModels = SortedFallback();
        private bool orModelsLoaded;

        private static List<OpenRouter.Model> SortedFallback()
        {
            List<OpenRouter.Model> list = OpenRouter.FallbackModels();
            OpenRouter.SortModels(list);
            return list;
        }
        private string orBalance = "";        // «$4,12», «проверяю…» или пусто
        private string orBalanceNote = "";
        private bool orBalanceError;
        private int orBalanceVersion;         // ответ на старый запрос (до смены ключа) не показываем

        /// <summary>Можно ли начинать запись: есть модель/whisper или ключ OpenRouter.</summary>
        private bool CheckEngineReady()
        {
            if (settings.Cloud)
            {
                if (string.IsNullOrEmpty(settings.OpenRouterKey))
                {
                    Log(L.T("Вставьте ключ OpenRouter в настройках (раздел «Распознавание») или выберите «На компьютере».", "Paste your OpenRouter key in Settings (the “Recognition” section) or choose “On this computer”."));
                    SetStatus(L.T("Нет ключа OpenRouter", "No OpenRouter key"), ColorRed);
                    return false;
                }
                return true;
            }
            var model = SelectedModel();
            if (model == null || !model.Exists)
            {
                Log(L.T("Сначала скачайте модель: кнопка «Скачать» рядом со списком моделей.", "Download a model first: the “Download” button next to the model list."));
                SetStatus(L.T("Модель не скачана — нажмите «Скачать»", "Model not downloaded — click “Download”"), ColorRed);
                return false;
            }
            if (!model.IsGigaAm && !File.Exists(WhisperEngine.CliPath))
            {
                Log(L.T("Не найден ", "Not found: ") + WhisperEngine.CliPath + L.T(". Положите папку whisper рядом с программой.", ". Put the whisper folder next to the program."));
                SetStatus(L.T("Не найден whisper", "whisper not found"), ColorRed);
                return false;
            }
            return true;
        }

        /// <summary>«моделью ggml-small…» или «в OpenRouter (Whisper Large V3 Turbo)» — для журнала.</summary>
        private string EngineDescription()
        {
            if (settings.Cloud) return L.T("в OpenRouter (", "in OpenRouter (") + OpenRouterModelName() + ")";
            var m = SelectedModel();
            return L.T("моделью ", "with model ") + (m != null ? m.File : "?");
        }

        /// <summary>
        /// Распознать и, если включено, перевести. На английский переводит сам Whisper (--translate на компьютере,
        /// language=en в облаке). На другие языки (и с моделью GigaAM) — вторым шагом, текстовой моделью OpenRouter.
        /// </summary>
        private async Task<WhisperEngine.Result> TranscribeAsync(string wav)
        {
            string src = settings.Language;
            string dst = settings.TranslateTo;
            var model = SelectedModel();
            bool giga = !settings.Cloud && model != null && model.IsGigaAm;
            bool translate = settings.Translate && !string.IsNullOrEmpty(dst) && dst != src && !(giga && dst == "ru");
            bool toEnglish = translate && dst == "en" && !giga;   // Whisper переводит на английский сам

            WhisperEngine.Result res;
            if (giga)
            {
                if (src != "ru" && src != "auto")
                    Log(L.T("GigaAM понимает только русскую речь. Для языка «", "GigaAM understands only Russian speech. For “") + L.LangName(src, false) +
                        L.T("» выберите в настройках модель Whisper.", "” choose a Whisper model in Settings."));
                res = await SherpaEngine.TranscribeAsync(wav, model.FullPath);
                if (res.Error == null) res.Device += ", GigaAM";
            }
            else if (!settings.Cloud)
            {
                // русская подсказка тянет Whisper писать по-русски — для перевода на английский берём английскую
                string prompt = toEnglish && settings.Prompt == Settings.DefaultPrompt ? Settings.DefaultPromptEn : settings.EffectivePrompt;
                res = await WhisperEngine.TranscribeAsync(wav, model.FullPath, src, prompt, toEnglish);
            }
            else
            {
                res = await OpenRouter.TranscribeAsync(
                    wav, settings.OpenRouterKey, settings.OpenRouterModel, OpenRouterModelName(), toEnglish ? "en" : src);
                if (res.Error == null) RefreshBalance(false);   // баланс поменялся
            }
            if (toEnglish && res.Error == null) res.Device += L.T(", перевод на English", ", translated to English");
            if (!translate || toEnglish || res.Error != null || res.Text.Length == 0) return res;

            // перевод на другие языки
            string name = L.LangName(dst, false);
            if (string.IsNullOrEmpty(settings.OpenRouterKey))
            {
                Log(L.T("Для перевода на ", "Translating to ") + name +
                    (giga
                        ? L.T(" с моделью GigaAM нужен ключ OpenRouter (Настройки → Распознавание). Без ключа на английский переводят модели Whisper. Вставляю текст без перевода.",
                              " with GigaAM needs an OpenRouter key (Settings → Recognition). Without a key, Whisper models translate to English. Pasting the text untranslated.")
                        : L.T(" нужен ключ OpenRouter (Настройки → Распознавание). На английский Whisper переводит сам. Вставляю текст без перевода.",
                              " needs an OpenRouter key (Settings → Recognition). Whisper translates to English by itself. Pasting the text untranslated.")));
                return res;
            }
            SetStatus(L.T("Перевожу на ", "Translating to ") + name + "…", ColorAmber);
            OpenRouter.TranslateResult t = await OpenRouter.TranslateAsync(
                res.Text, L.LangName(dst, true), settings.OpenRouterKey, settings.TranslateModel);
            if (t.Error != null || string.IsNullOrEmpty(t.Text))
            {
                Log(L.T("Перевод не удался: ", "Translation failed: ") + (t.Error ?? L.T("пустой ответ", "empty response")) +
                    L.T(". Вставляю текст без перевода.", ". Pasting the text untranslated."));
                return res;
            }
            Log(L.T("Распознано: ", "Recognized: ") + res.Text);
            res.Text = t.Text;
            res.Device += L.T(", перевод на ", ", translated to ") + name +
                          (t.Cost.HasValue ? " " + OpenRouter.FormatUsd(t.Cost.Value) : "");
            RefreshBalance(false);
            return res;
        }

        /// <summary>Языки для списков на странице: код и название.</summary>
        private static string LangsJson()
        {
            var sb = new System.Text.StringBuilder("[");
            foreach (string[] l in L.SpeechLangs)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append(new Js().S("c", l[0]).S("n", l[1]).ToString());
            }
            return sb.Append(']').ToString();
        }

        private void SetTranslate(bool on)
        {
            if (settings.Translate == on) return;
            settings.Translate = on;
            SaveSettings();
            Log(on ? L.T("Перевод включён: ", "Translation on: ") + L.LangName(settings.Language, false) + " → " +
                     L.LangName(settings.TranslateTo, false) + "."
                   : L.T("Перевод выключен.", "Translation off."));
        }

        private void SetTranslateTo(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 5) return;
            settings.TranslateTo = code;
            settings.Translate = true;   // выбрали язык — значит, хотят переводить
            SaveSettings();
            Log(L.T("Перевод: ", "Translation: ") + L.LangName(settings.Language, false) + " → " + L.LangName(code, false) +
                (code != "en" && string.IsNullOrEmpty(settings.OpenRouterKey)
                    ? L.T(". Для этого языка нужен ключ OpenRouter.", ". This language needs an OpenRouter key.")
                    : "."));
        }

        private int OpenRouterModelIndex()
        {
            return orModels.FindIndex(m => m.Id == settings.OpenRouterModel);
        }

        private string OpenRouterModelName()
        {
            int i = OpenRouterModelIndex();
            return i >= 0 ? orModels[i].Name : settings.OpenRouterModel;
        }

        /// <summary>Свежий список моделей и цен с сайта OpenRouter (ключ не нужен).</summary>
        private async void LoadOpenRouterModels()
        {
            try
            {
                List<OpenRouter.Model> list = await OpenRouter.ListModelsAsync();
                if (IsDisposed || list.Count == 0) return;
                orModels = list;
                orModelsLoaded = true;
            }
            catch (Exception ex)
            {
                if (settings.Cloud) Log(L.T("Не удалось загрузить список моделей OpenRouter (", "Could not load the OpenRouter model list (") + ex.Message + L.T("), показаны цены из программы.", "), showing built-in prices."));
            }
            if (OpenRouterModelIndex() < 0 && orModelsLoaded && orModels.Count > 0)
            {
                // в settings.ini модель, которая не распознаёт речь (или её убрали с сайта) — берём стандартную
                string old = settings.OpenRouterModel;
                int def = orModels.FindIndex(m => m.Id == OpenRouter.DefaultModel);
                settings.OpenRouterModel = orModels[def >= 0 ? def : 0].Id;
                SaveSettings();
                if (settings.Cloud)
                    Log(L.T("Модели «", "Model “") + old + L.T("» нет среди моделей распознавания речи OpenRouter — выбрана ", "” is not an OpenRouter speech-to-text model — switched to ") +
                        OpenRouterModelName() + ".");
            }
            else if (OpenRouterModelIndex() < 0)
            {
                orModels.Add(new OpenRouter.Model { Id = settings.OpenRouterModel, Name = settings.OpenRouterModel });
            }
            WebPushState();
        }

        /// <summary>Узнать баланс. log — написать результат в журнал (по кнопке «Обновить»).</summary>
        private async void RefreshBalance(bool log)
        {
            int version = ++orBalanceVersion;
            if (string.IsNullOrEmpty(settings.OpenRouterKey))
            {
                orBalance = "";
                orBalanceNote = "";
                orBalanceError = false;
                WebPushState();
                return;
            }
            if (orBalance.Length == 0) orBalance = L.T("проверяю…", "checking…");
            WebPushState();

            OpenRouter.Balance b = await OpenRouter.GetBalanceAsync(settings.OpenRouterKey);
            if (IsDisposed || version != orBalanceVersion) return;
            orBalanceError = b.Error != null;
            orBalance = b.Error != null ? L.T("не удалось узнать", "unavailable") : b.Text;
            orBalanceNote = b.Error ?? b.Note ?? "";
            if (log) Log(b.Error != null ? "OpenRouter: " + b.Error : L.T("OpenRouter, баланс: ", "OpenRouter balance: ") + b.Text + ". " + b.Note);
            WebPushState();
        }

        private void SetEngine(bool cloud)
        {
            if (settings.Cloud == cloud) return;
            settings.Cloud = cloud;
            SaveSettings();
            if (cloud)
            {
                Log(L.T("Распознавание: в облаке OpenRouter (", "Recognition: in the OpenRouter cloud (") + OpenRouterModelName() + L.T("). Платно — с баланса OpenRouter.", "). Paid — from your OpenRouter balance.") +
                    (string.IsNullOrEmpty(settings.OpenRouterKey) ? L.T(" Вставьте ключ API в поле ниже.", " Paste your API key in the field below.") : ""));
                if (!orModelsLoaded) LoadOpenRouterModels();
                RefreshBalance(false);
            }
            else
            {
                var m = SelectedModel();
                string family = m != null && m.IsGigaAm ? "GigaAM" : "Whisper";
                Log(L.T("Распознавание: на компьютере (", "Recognition: on this computer (") + family + L.T(", бесплатно).", ", free)."));
            }
            UpdateStatus();
        }

        private void SetOpenRouterKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length > 0 && !key.StartsWith("sk-or-", StringComparison.Ordinal))
                Log(L.T("Похоже, это не ключ OpenRouter: он обычно начинается с «sk-or-v1-». Всё равно сохраняю.", "This does not look like an OpenRouter key: it usually starts with “sk-or-v1-”. Saving it anyway."));
            settings.OpenRouterKey = key;
            SaveSettings();
            orBalance = "";
            Log(key.Length > 0 ? L.T("Ключ OpenRouter сохранён в settings.ini (", "OpenRouter key saved to settings.ini (") + OpenRouter.Mask(key) + ")." : L.T("Ключ OpenRouter удалён.", "OpenRouter key removed."));
            RefreshBalance(key.Length > 0);
        }

        private void SetOpenRouterModel(int index)
        {
            if (index < 0 || index >= orModels.Count) return;
            OpenRouter.Model m = orModels[index];
            settings.OpenRouterModel = m.Id;
            SaveSettings();
            Log(L.T("Модель OpenRouter: ", "OpenRouter model: ") + m.Title + ".");
        }

        /// <summary>Переключить язык окна (RU / EN) на лету.</summary>
        private void SetUiLang(string lang)
        {
            lang = lang == "en" ? "en" : "ru";
            if (settings.UiLang == lang) return;
            settings.UiLang = lang;
            L.En = lang == "en";
            SaveSettings();
            // списки, где названия собираются в программе
            FillMics();
            FillModels();
            if (fileForm != null && !fileForm.IsDisposed) fileForm.ApplyLang();
            Log(L.T("Язык интерфейса: русский.", "Interface language: English."));
            UpdateStatus();
        }

        /// <summary>Часть состояния для страницы: всё про облако.</summary>
        private void AddCloudState(Js js)
        {
            // модели по группам: g = 1 — переводят на английский (Whisper), 0 — только распознают
            var models = new System.Text.StringBuilder("[");
            foreach (OpenRouter.Model m in orModels)
            {
                if (models.Length > 1) models.Append(',');
                models.Append(new Js().S("t", m.Title).N("g", m.Translates ? 1 : 0).ToString());
            }
            models.Append(']');
            js.S("engine", settings.Cloud ? "openrouter" : "local")
              .B("orHasKey", !string.IsNullOrEmpty(settings.OpenRouterKey))
              .S("orKeyMask", OpenRouter.Mask(settings.OpenRouterKey))
              .Raw("orModels", models.ToString())
              .N("orModel", OpenRouterModelIndex())
              .S("orModelName", OpenRouterModelName())
              .S("orBalance", orBalance)
              .S("orBalanceNote", orBalanceNote)
              .B("orBalanceError", orBalanceError);
        }
    }
}
