@echo off
setlocal
set "ROOT=%~dp0"
set "CLI=%ROOT%src\RimContext.Cli\bin\Release\net8.0\rimctx.exe"

if not exist "%CLI%" set "CLI=%ROOT%src\RimContext.Cli\bin\Debug\net8.0\rimctx.exe"

if not exist "%CLI%" (
  >&2 echo rimctx is not built. Run: dotnet build RimContext.sln -c Release
  exit /b 1
)

"%CLI%" %*
exit /b %ERRORLEVEL%
