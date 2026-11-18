@echo off
rem 配布用 zip を dist\ に作る（exe もビルドし直す）
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\package.ps1"
pause
