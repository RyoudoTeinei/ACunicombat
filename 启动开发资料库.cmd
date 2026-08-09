@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "URL=http://127.0.0.1:5187/"
set "REPO_DIR=%~dp0"
if "%REPO_DIR:~-1%"=="\" set "REPO_DIR=%REPO_DIR:~0,-1%"
set "TOOL_DIR=%REPO_DIR%\..\Tools\AC6WikiEditor"
set "PROJECT=%TOOL_DIR%\AC6WikiEditor.csproj"
set "DLL=%TOOL_DIR%\bin\Debug\net8.0\AC6WikiEditor.dll"

set "LOG_DIR="
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "$p=$null; foreach($f in @(Get-ChildItem -LiteralPath $env:REPO_DIR -Filter AGENTS.md -File -Recurse -ErrorAction SilentlyContinue)){ if(@(Get-ChildItem -LiteralPath $f.DirectoryName -Directory -Filter '01_*' -ErrorAction SilentlyContinue).Count -gt 0 -and @(Get-ChildItem -LiteralPath $f.DirectoryName -Directory -Filter '06_*' -ErrorAction SilentlyContinue).Count -gt 0){ $p=$f.DirectoryName; break } }; if($p){$p}"`) do set "LOG_DIR=%%D"

set "DATA_DIR="
if defined LOG_DIR for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "$p=$null; foreach($d in @(Get-ChildItem -LiteralPath $env:LOG_DIR -Directory -Recurse -ErrorAction SilentlyContinue)){ if(@(Get-ChildItem -LiteralPath $d.FullName -File -Filter '*.csv' -ErrorAction SilentlyContinue).Count -gt 0 -and @(Get-ChildItem -LiteralPath $d.FullName -File -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt 0){ $p=$d.FullName; break } }; if($p){$p}"`) do set "DATA_DIR=%%D"

call :check_server
if not errorlevel 1 goto open_page

if not defined LOG_DIR (
  echo [ERROR] Development log directory was not found:
  echo         %REPO_DIR%
  goto failed
)

if not defined DATA_DIR (
  echo [ERROR] AC6 Wiki data directory was not found below:
  echo         %LOG_DIR%
  goto failed
)

if not exist "%PROJECT%" (
  echo [ERROR] Web tool project was not found:
  echo         %PROJECT%
  goto failed
)

set "DOTNET=C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Data\DotNetSdk\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET="

if not defined DOTNET (
  for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "$p=$null; foreach($f in @(Get-ChildItem -Path 'C:\Program Files\Unity\Hub\Editor' -Filter dotnet.exe -Recurse -ErrorAction SilentlyContinue)){ if($f.FullName -like '*\Editor\Data\DotNetSdk\dotnet.exe'){ $p=$f.FullName; break } }; if($p){$p}"`) do set "DOTNET=%%D"
)

if not defined DOTNET (
  for /f "delims=" %%D in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%D"
)

if not defined DOTNET (
  echo [ERROR] A .NET 8 runtime was not found.
  echo         Install it through Unity Hub or install the .NET 8 SDK.
  goto failed
)

set "NEED_BUILD="
if not exist "%DLL%" set "NEED_BUILD=1"
if exist "%DLL%" for /f "usebackq delims=" %%B in (`powershell -NoProfile -Command "$dll=Get-Item -LiteralPath $env:DLL; $newer=Get-ChildItem -LiteralPath $env:TOOL_DIR -File -Recurse -ErrorAction SilentlyContinue ^| Where-Object { $_.FullName -notmatch '\\(bin^|obj)\\' -and $_.Extension -in @('.cs','.csproj','.html','.css','.js') -and $_.LastWriteTimeUtc -gt $dll.LastWriteTimeUtc }; if($newer){'1'}"`) do set "NEED_BUILD=%%B"

if defined NEED_BUILD (
  echo Building the development library...
  "%DOTNET%" build "%PROJECT%" --configfile "%TOOL_DIR%\NuGet.Config"
  if errorlevel 1 goto failed
)

echo Starting the development library...
set "GAME_DEV_LOG_DIR=%LOG_DIR%"
set "AC6_WIKI_DATA_DIR=%DATA_DIR%"
set "DOTNET_EXE=%DOTNET%"
set "APP_DLL=%DLL%"
set "APP_URL=%URL%"
set "APP_WORKDIR=%TOOL_DIR%"
powershell -NoProfile -Command "Start-Process -FilePath $env:DOTNET_EXE -ArgumentList @($env:APP_DLL,'--urls',$env:APP_URL) -WorkingDirectory $env:APP_WORKDIR -WindowStyle Hidden" >nul 2>&1
if errorlevel 1 goto failed

for /l %%I in (1,1,20) do (
  call :check_server
  if not errorlevel 1 goto open_page
  ping 127.0.0.1 -n 2 >nul
)

echo [ERROR] The service did not become ready at %URL%
goto failed

:open_page
if /i "%~1"=="--no-open" goto success
start "" "%URL%"

:success
echo Development library is ready: %URL%
exit /b 0

:check_server
powershell -NoProfile -Command "try { $r=Invoke-WebRequest -UseBasicParsing '%URL%api/logs' -TimeoutSec 2; if($r.StatusCode -eq 200){exit 0}; exit 1 } catch { exit 1 }" >nul 2>&1
exit /b %errorlevel%

:failed
echo.
echo Press any key to close.
pause >nul
exit /b 1
