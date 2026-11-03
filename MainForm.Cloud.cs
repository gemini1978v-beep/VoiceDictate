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
        private List<OpenRouter.Model> orModels = OpenRouter.FallbackModels();
        private bool orModelsLoaded;
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
                    Log("Вставьте ключ OpenRouter в настройках (раздел «Распознавание») или выберите «На компьютере».");
                    SetStatus("Нет ключа OpenRouter", ColorRed);
                    return false;
                }
                return true;
            }
            var model = SelectedModel();
            if (model == null || !model.Exists)
            {
                Log("Сначала скачайте модель: кнопка «Скачать» рядом со списком моделей.");
                SetStatus("Модель не скачана — нажмите «Скачать»", ColorRed);
                return false;
            }
            if (!File.Exists(WhisperEngine.CliPath))
            {
                Log("Не найден " + WhisperEngine.CliPath + ". Положите папку whisper рядом с программой.");
                SetStatus("Не найден whisper", ColorRed);
                return false;
            }
            return true;
        }

        /// <summary>«моделью ggml-small…» или «в OpenRouter (Whisper Large V3 Turbo)» — для журнала.</summary>
        private string EngineDescription()
        {
            if (settings.Cloud) return "в OpenRouter (" + OpenRouterModelName() + ")";
            var m = SelectedModel();
            return "моделью " + (m != null ? m.File : "?");
        }

        private async Task<WhisperEngine.Result> TranscribeAsync(string wav)
        {
            if (!settings.Cloud)
            {
                var model = SelectedModel();
                return await WhisperEngine.TranscribeAsync(wav, model.FullPath, settings.Language, settings.Prompt);
            }
            WhisperEngine.Result res = await OpenRouter.TranscribeAsync(
                wav, settings.OpenRouterKey, settings.OpenRouterModel, OpenRouterModelName(), settings.Language);
            if (res.Error == null) RefreshBalance(false);   // баланс поменялся
            return res;
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
                if (settings.Cloud) Log("Не удалось загрузить список моделей OpenRouter (" + ex.Message + "), показаны цены из программы.");
            }
            if (OpenRouterModelIndex() < 0 && orModelsLoaded && orModels.Count > 0)
            {
                // в settings.ini модель, которая не распознаёт речь (или её убрали с сайта) — берём стандартную
                string old = settings.OpenRouterModel;
                int def = orModels.FindIndex(m => m.Id == OpenRouter.DefaultModel);
                settings.OpenRouterModel = orModels[def >= 0 ? def : 0].Id;
                SaveSettings();
                if (settings.Cloud)
                    Log("Модели «" + old + "» нет среди моделей распознавания речи OpenRouter — выбрана " +
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
            if (orBalance.Length == 0) orBalance = "проверяю…";
            WebPushState();

            OpenRouter.Balance b = await OpenRouter.GetBalanceAsync(settings.OpenRouterKey);
            if (IsDisposed || version != orBalanceVersion) return;
            orBalanceError = b.Error != null;
            orBalance = b.Error != null ? "не удалось узнать" : b.Text;
            orBalanceNote = b.Error ?? b.Note ?? "";
            if (log) Log(b.Error != null ? "OpenRouter: " + b.Error : "OpenRouter, баланс: " + b.Text + ". " + b.Note);
            WebPushState();
        }

        private void SetEngine(bool cloud)
        {
            if (settings.Cloud == cloud) return;
            settings.Cloud = cloud;
            SaveSettings();
            if (cloud)
            {
                Log("Распознавание: в облаке OpenRouter (" + OpenRouterModelName() + "). Платно — с баланса OpenRouter." +
                    (string.IsNullOrEmpty(settings.OpenRouterKey) ? " Вставьте ключ API в поле ниже." : ""));
                if (!orModelsLoaded) LoadOpenRouterModels();
                RefreshBalance(false);
            }
            else
            {
                Log("Распознавание: на компьютере (Whisper, бесплатно).");
            }
            UpdateStatus();
        }

        private void SetOpenRouterKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length > 0 && !key.StartsWith("sk-or-", StringComparison.Ordinal))
                Log("Похоже, это не ключ OpenRouter: он обычно начинается с «sk-or-v1-». Всё равно сохраняю.");
            settings.OpenRouterKey = key;
            SaveSettings();
            orBalance = "";
            Log(key.Length > 0 ? "Ключ OpenRouter сохранён в settings.ini (" + OpenRouter.Mask(key) + ")." : "Ключ OpenRouter удалён.");
            RefreshBalance(key.Length > 0);
        }

        private void SetOpenRouterModel(int index)
        {
            if (index < 0 || index >= orModels.Count) return;
            OpenRouter.Model m = orModels[index];
            settings.OpenRouterModel = m.Id;
            SaveSettings();
            Log("Модель OpenRouter: " + m.Title + ".");
        }

        /// <summary>Часть состояния для страницы: всё про облако.</summary>
        private void AddCloudState(Js js)
        {
            var titles = new List<string>();
            foreach (OpenRouter.Model m in orModels) titles.Add(m.Title);
            js.S("engine", settings.Cloud ? "openrouter" : "local")
              .B("orHasKey", !string.IsNullOrEmpty(settings.OpenRouterKey))
              .S("orKeyMask", OpenRouter.Mask(settings.OpenRouterKey))
              .Raw("orModels", Js.Arr(titles))
              .N("orModel", OpenRouterModelIndex())
              .S("orModelName", OpenRouterModelName())
              .S("orBalance", orBalance)
              .S("orBalanceNote", orBalanceNote)
              .B("orBalanceError", orBalanceError);
        }
    }
}
