@echo off
rem 自我檢查：建置後跑 Tests\SelfTest.cs
rem Usage: selftest.cmd [--real "<folder>"]
setlocal
set HERE=%~dp0
call "%HERE%build.cmd" || exit /b 1
set CSC=
for /d %%D in ("%APPDATA%\*CTA") do if exist "%%D\roslyn\csc.exe" set "CSC=%%D\roslyn\csc.exe"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%CSC%" -nologo -utf8output -noconfig -t:exe -nowarn:CS1702 -out:"%HERE%bin\SelfTest.exe" -r:"%HERE%bin\CtaBacktest.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" "%HERE%Tests\SelfTest.cs" || exit /b 1
rem Default: data = samples\data, strategies = samples\strategies, output = bin\selftest (never under Export).
rem Optional extra args: --real "<private strategies folder>" (also runs your own strategies; passed through as is).
"%HERE%bin\SelfTest.exe" "%HERE%samples\data" "%HERE%samples\strategies" "%HERE%bin\selftest" %*
exit /b %errorlevel%
