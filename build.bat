@echo off
cd /d "%~dp0"
set R=
for %%f in (assets\sprites\*.png) do call set R=%%R%% /resource:%%f,%%~nxf
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:TaskbarCat.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll %R% *.cs
if errorlevel 1 exit /b 1

rem self-sign (non-fatal; certificate is created on first run). see tools\sign.ps1
powershell -NoProfile -File "%~dp0tools\sign.ps1"
if errorlevel 1 echo [warn] signing skipped. TaskbarCat.exe is still usable.
exit /b 0
