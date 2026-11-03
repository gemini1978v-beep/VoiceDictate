@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "DOTNET="
where dotnet >nul 2>nul && set "DOTNET=dotnet"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET goto install

"%DOTNET%" --list-sdks 2>nul | findstr /r /c:"^[89]\." /c:"^1[0-9]\." >nul
if errorlevel 1 goto install
goto build

:install
echo === .NET SDK не найден. Ставлю .NET SDK через winget, это один раз ===
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
if errorlevel 1 (
    echo.
    echo Не получилось поставить автоматически.
    echo Скачайте .NET SDK вручную: https://dotnet.microsoft.com/download
    echo и снова запустите build.bat
    pause
    exit /b 1
)
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

:build
echo === Собираю программу, первый раз может занять пару минут ===
"%DOTNET%" publish VoiceDictate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
if errorlevel 1 (
    echo.
    echo Сборка не удалась. Пришлите скриншот текста выше.
    pause
    exit /b 1
)
if not exist "dist\settings.ini" copy /Y settings.ini "dist\settings.ini" >nul
xcopy /E /I /Y /Q whisper "dist\whisper" >nul
xcopy /E /I /Y /Q ui "dist\ui" >nul
if not exist "dist\models" mkdir "dist\models"
copy /Y download-model.bat "dist\download-model.bat" >nul

echo.
echo Готово: dist\VoiceDictate.exe
echo Запустите его и нажмите «Скачать» (модель Whisper, ~190 МБ, один раз), потом «Проверить».
pause
