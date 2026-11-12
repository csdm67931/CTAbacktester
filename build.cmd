@echo off
rem 建置自製回測器：bin\CtaBacktest.dll（模擬介面＋引擎）與 bin\Backtester.exe（視窗）
rem 用群益交易王 CTA 附的 csc（與平台同版），執行於 .NET Framework 4.x
setlocal
set CSC=
for /d %%D in ("%APPDATA%\*CTA") do if exist "%%D\roslyn\csc.exe" set "CSC=%%D\roslyn\csc.exe" & set "PLAT=%%D"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set HERE=%~dp0
if "%CSC%"=="" (echo 找不到交易王附的 csc.exe，需要安裝群益交易王 CTA & exit /b 1)
tasklist /fi "imagename eq Backtester.exe" 2>nul | find /i "Backtester.exe" >nul && (echo Backtester.exe 還開著，請先關閉再建置；否則編譯器將刪掉 bin\CtaBacktest.dll & exit /b 1)
tasklist /fi "imagename eq BacktestWorker.exe" 2>nul | find /i "BacktestWorker.exe" >nul && (echo BacktestWorker.exe 還在執行，請等回測結束或關閉回測器再建置 & exit /b 1)
if not exist "%HERE%bin" mkdir "%HERE%bin"
rem 載入 EvoCode 策略 DLL 要用交易王附的 Mono.Cecil（spec.md 13.1）
copy /y "%PLAT%\Mono.Cecil.dll" "%HERE%bin\" >nul || (echo 找不到 %PLAT%\Mono.Cecil.dll，需要安裝群益交易王 CTA & exit /b 1)

"%CSC%" -nologo -utf8output -noconfig -t:library -optimize+ -debug:pdbonly -nowarn:CS1702 -out:"%HERE%bin\CtaBacktest.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" -r:"%FW%\System.Drawing.dll" -r:"%HERE%bin\Mono.Cecil.dll" -recurse:"%HERE%Shim\*.cs" -recurse:"%HERE%Engine\*.cs"
if errorlevel 1 (echo CtaBacktest.dll 建置失敗 & exit /b 1)

rem 回測子程式：策略只在這裡執行（spec.md 13.2）
"%CSC%" -nologo -utf8output -noconfig -t:exe -optimize+ -debug:pdbonly -nowarn:CS1702 -out:"%HERE%bin\BacktestWorker.exe" -r:"%HERE%bin\CtaBacktest.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" "%HERE%Worker\Program.cs"
if errorlevel 1 (echo BacktestWorker.exe 建置失敗 & exit /b 1)

"%CSC%" -nologo -utf8output -noconfig -t:winexe -optimize+ -debug:pdbonly -nowarn:CS1702 -out:"%HERE%bin\Backtester.exe" -r:"%HERE%bin\CtaBacktest.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" -r:"%FW%\System.Drawing.dll" -r:"%FW%\System.Windows.Forms.dll" -recurse:"%HERE%App\*.cs"
if errorlevel 1 (echo Backtester.exe 建置失敗 & exit /b 1)

echo 建置完成：%HERE%bin\Backtester.exe
exit /b 0
