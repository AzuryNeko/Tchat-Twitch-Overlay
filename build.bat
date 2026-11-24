@echo off
REM Necessite le SDK .NET 10 : https://dotnet.microsoft.com/download
cd /d "%~dp0"

echo === Tchat Overlay (Standard) ===
dotnet publish src\TchatOverlay.csproj -c Release -o publish\standard
if errorlevel 1 goto :fail

echo.
echo === Tchat Overlay Pro (reponse + moderation) ===
dotnet publish src\TchatOverlay.csproj -c Release -p:Edition=Pro -o publish\pro
if errorlevel 1 goto :fail

echo.
echo OK :
echo   "publish\standard\Tchat Overlay.exe"
echo   "publish\pro\Tchat Overlay Pro.exe"
pause
exit /b 0

:fail
echo.
echo ECHEC DU BUILD
pause
exit /b 1
