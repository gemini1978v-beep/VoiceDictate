@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Запасной способ скачать модель Whisper, если кнопка «Скачать» в программе не сработала.
rem Использование: download-model.bat            — модель small (рекомендую, ~190 МБ)
rem                download-model.bat turbo      — large-v3-turbo (~550 МБ, точнее, медленнее)

set "NAME=ggml-small-q5_1.bin"
if /i "%~1"=="turbo"  set "NAME=ggml-large-v3-turbo-q5_0.bin"
if /i "%~1"=="medium" set "NAME=ggml-medium-q5_0.bin"
if /i "%~1"=="base"   set "NAME=ggml-base-q5_1.bin"

if not exist models mkdir models
echo === Скачиваю %NAME% в папку models ===
curl.exe -L --fail -o "models\%NAME%.part" "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/%NAME%"
if errorlevel 1 (
    echo Не получилось с huggingface.co, пробую зеркало hf-mirror.com...
    curl.exe -L --fail -o "models\%NAME%.part" "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/%NAME%"
)
if errorlevel 1 (
    del "models\%NAME%.part" >nul 2>nul
    echo.
    echo Не получилось скачать. Откройте ссылку в браузере и положите файл в папку models:
    echo https://huggingface.co/ggerganov/whisper.cpp/resolve/main/%NAME%
    pause
    exit /b 1
)
move /Y "models\%NAME%.part" "models\%NAME%" >nul
echo.
echo Готово: models\%NAME%
pause
