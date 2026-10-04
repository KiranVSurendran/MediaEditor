@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   Media Toolbox - WPF Windows Publisher
echo ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 8 SDK is required to build Media Toolbox.
  echo Install it from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

if not exist ffmpeg.exe (
  echo ffmpeg.exe is missing from this folder.
  pause
  exit /b 1
)
if not exist ffprobe.exe (
  echo ffprobe.exe is missing from this folder.
  pause
  exit /b 1
)

echo Publishing the Windows app. This may take a few minutes...
for /f %%i in ('powershell -NoProfile -Command "$base=Get-Date -Format yyyyMMdd_HHmmss; $candidate='dist\Media_Toolbox_WPF_'+$base; $n=1; while(Test-Path $candidate){$candidate='dist\Media_Toolbox_WPF_'+$base+'_'+$n; $n++}; $candidate"') do set "BUILD_DIR=%%i"
dotnet publish MediaToolbox.Wpf\MediaToolbox.Wpf.csproj -c Release --self-contained false -p:PublishSingleFile=false -o "%BUILD_DIR%"
if errorlevel 1 (
  echo.
  echo Publish failed. The existing Media_Toolbox build was left untouched.
  pause
  exit /b 1
)

copy /y ffmpeg.exe "%BUILD_DIR%\ffmpeg.exe" >nul
copy /y ffprobe.exe "%BUILD_DIR%\ffprobe.exe" >nul
copy /y README.md "%BUILD_DIR%\README.md" >nul
copy /y LICENSE "%BUILD_DIR%\LICENSE" >nul
copy /y THIRD_PARTY_NOTICES.md "%BUILD_DIR%\THIRD_PARTY_NOTICES.md" >nul
echo.
echo Build complete.
echo Run "%BUILD_DIR%\MediaToolbox.Wpf.exe"
echo Previous build folders have been preserved.
echo.
pause
