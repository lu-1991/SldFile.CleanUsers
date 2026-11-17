@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================
echo   SolidWorks 用户清理工具 - 编译
echo ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet，请先安装 .NET 8 SDK：
    echo        https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/2] 编译窗体版（自包含单文件）...
dotnet publish "src\Gui\SldFile.CleanUsers.Gui.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0发布\窗体版"
if errorlevel 1 goto :fail

echo [2/2] 编译命令行版（自包含单文件）...
dotnet publish "src\Cli\SldFile.CleanUsers.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0发布\命令行版"
if errorlevel 1 goto :fail

echo.
echo 完成：
echo   窗体版 %~dp0发布\窗体版\SolidWorks用户清理工具.exe
echo   命令行 %~dp0发布\命令行版\SldFile.CleanUsers.exe
echo.
pause
exit /b 0

:fail
echo.
echo [失败] 编译出错，请查看上方日志。
pause
exit /b 1
