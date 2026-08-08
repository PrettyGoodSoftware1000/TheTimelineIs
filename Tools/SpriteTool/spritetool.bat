@echo off
rem Double-click this to open spritetool's menu. It also forwards arguments,
rem so "spritetool.bat extract wolf.mp4 werewolf_attack" works from a terminal.
pushd "%~dp0"
dotnet run --project . -- %*
popd
rem keep the window up after a double-click, but not when it was called with
rem arguments from a terminal that is already showing the output
if "%~1"=="" pause
