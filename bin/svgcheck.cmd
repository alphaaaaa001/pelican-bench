@echo off
rem ========================================================================
rem  svgcheck.cmd - interpreter probe for the pelican-bench checker.
rem
rem  WHY THIS FILE EXISTS AT ALL, and why the version check is NOT inside
rem  svgcheck.py: a SyntaxError happens at COMPILE time. Once svgcheck.py
rem  uses syntax an old interpreter cannot parse, that interpreter dies
rem  before executing line 1, so a guard like
rem      if sys.version_info ... : sys.exit
rem  inside svgcheck.py would never get a chance to run. The probe therefore
rem  has to live in a file even Python 3.4 can digest. This batch file runs
rem  no Python of its own: it only asks one -c question per candidate and
rem  then hands everything over to whichever interpreter qualifies.
rem
rem  PROBE ORDER, first candidate with version_info at least 3.8 wins:
rem      py -3.12   then   py -3   then   python3   then   python
rem      then py.exe in the real Launcher install dir, because the py on
rem      PATH may be the 2015 stub that shipped with Python 3.4 into
rem      C:\Windows, and that one does not understand -3.12 at all
rem      then a scan of the usual install roots
rem  Escape hatch: set SVGCHECK_PYTHON to a python.exe path to force one.
rem
rem  USAGE is identical to svgcheck.py; all arguments pass straight through:
rem      bin\svgcheck.cmd fixtures\sample-pelican.html
rem      bin\svgcheck.cmd --json fixtures\sample-pelican.html
rem      bin\svgcheck.cmd --check
rem
rem  ENCODING RULES FOR THIS FILE, learned the hard way - do not break them:
rem    1. It is UTF-8 with Chinese in the user-facing messages, so chcp 65001
rem       must run before any Chinese byte is read. Everything above the chcp
rem       line is pure ASCII for exactly that reason.
rem    2. No double quote character may appear inside a rem line. An odd
rem       number of quotes makes cmd.exe believe a string is still open, it
rem       then swallows following lines and the read offset slips - the
rem       parser starts executing the middle of sentences. Reproduced: the
rem       fragment  that interpreter dies before  came back as a
rem       command-not-found error.
rem    3. Chinese text lives only in top level echo lines, never inside a
rem       parenthesised block, because a GBK trail byte can be 0x29 and would
rem       then close the block early on a machine where chcp did not take.
rem    4. Line endings MUST stay CRLF, and this rule is the nastiest of the four:
rem       cmd.exe resolves labels by byte offset, so an LF-only batch file makes
rem       it land in the middle of a line, after which it executes fragments such
rem       as heck.cmd and loops forever. Reproduced. Git hazard: this repo sets
rem       text=auto eol=lf in .gitattributes, which normalises this file to LF on
rem       checkout and therefore breaks the wrapper for anyone who clones. It
rem       needs a  *.cmd  text eol=crlf  line added there.
rem ========================================================================
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

set "ARGS=%*"
set "ROOT=%~dp0.."
set "TARGET=%ROOT%\svgcheck.py"
set "FOUND=0"
set "EXE="
set "SW="
set "SEEN="

if not exist "%TARGET%" goto :notarget

rem ---- 0. explicit override wins -------------------------------------
if defined SVGCHECK_PYTHON goto :try_env
goto :try_py312

:try_env
for %%A in ("%SVGCHECK_PYTHON%") do set "SVGCAND=%%~A"
call :try "!SVGCAND!" ""

:try_py312
rem ---- 1. the four candidates the contract spells out -----------------
call :try "py" "-3.12"
call :try "py" "-3"
call :try "python3" ""
call :try "python" ""

rem ---- 2. real Launcher, if the py on PATH is the 2015 stub -----------
if "!FOUND!"=="1" goto :run
call :launcher "%LOCALAPPDATA%\Programs\Python\Launcher\py.exe"
if "!FOUND!"=="1" goto :run
call :launcher "%ProgramFiles%\Python\Launcher\py.exe"
if "!FOUND!"=="1" goto :run
call :launcher "%ProgramFiles(x86)%\Python\Launcher\py.exe"
if "!FOUND!"=="1" goto :run

rem ---- 3. last resort: scan the usual install roots -------------------
for %%P in (
    "%LOCALAPPDATA%\Programs\Python\Python3*\python.exe"
    "%ProgramFiles%\Python3*\python.exe"
    "%ProgramFiles%\Python\Python3*\python.exe"
    "%ProgramFiles(x86)%\Python3*\python.exe"
    "%SYSTEMDRIVE%\Python3*\python.exe"
) do if "!FOUND!"=="0" if exist "%%~P" call :try "%%~P" ""

if "!FOUND!"=="0" goto :nopython

:run
rem ---- hand all arguments through to svgcheck.py ----------------------
"!EXE!" !SW! "%TARGET%" !ARGS!
exit /b !ERRORLEVEL!

:notarget
echo [svgcheck] 找不到主程序 svgcheck.py：%TARGET% 1>&2
exit /b 3

:nopython
if not "!SEEN!"=="" goto :nopython_seen
echo [svgcheck] 需要 Python 3.8 或更高版本，但一个 Python 都没找到。 1>&2
goto :nopython_tail
:nopython_seen
echo [svgcheck] 需要 Python 3.8 或更高版本，找到的是：!SEEN! 1>&2
:nopython_tail
echo [svgcheck] 装一个够新的 Python 再试；也可以 set SVGCHECK_PYTHON 直接指定解释器路径。 1>&2
exit /b 2


rem ========================================================================
rem  :try  EXE  SWITCH
rem  EXE may contain spaces and must arrive quoted; SWITCH is an optional
rem  version tag such as -3.12. Probes the version and, on success, latches
rem  EXE/SW for :run. The probe statement itself has to parse under Python
rem  3.4, so: no f-string, no 3.8-only syntax, no walrus.
rem ========================================================================
:try
if "%~1"=="" exit /b 0
if "!FOUND!"=="1" exit /b 0
set "CAND=%~1"
if not "%~2"=="" set "CAND=%~1 %~2"
"%~1" %~2 -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" >nul 2>&1
if not errorlevel 1 goto :try_hit
rem present but too old, or not present at all: remember what it actually was
set "VER="
for /f "delims=" %%V in ('"%~1" %~2 -c "import sys; sys.stdout.write(chr(46).join(str(x) for x in sys.version_info[:3]))" 2^>nul') do set "VER=%%V"
if defined VER goto :try_note
set "VER=命令不存在"
:try_note
set "SEEN=!SEEN! !CAND!=!VER!"
exit /b 0
:try_hit
set "FOUND=1"
set "EXE=%~1"
set "SW=%~2"
exit /b 0


rem ========================================================================
rem  :launcher  PATH_TO_PY_EXE
rem  A real py.exe understands -3.12 and -3; the 2015 stub does not, its
rem  probe fails and the search simply moves on.
rem ========================================================================
:launcher
if "!FOUND!"=="1" exit /b 0
if not exist "%~1" exit /b 0
call :try "%~1" "-3.12"
if "!FOUND!"=="1" exit /b 0
call :try "%~1" "-3"
exit /b 0
