@echo off
setlocal enabledelayedexpansion

rem ---------------------------------------------------------------------------
rem  Builds Spark in Release and then packages it into the MSI.
rem
rem  Usage:  build-installer.bat [Configuration]
rem
rem    Configuration   Release (default) or Debug.
rem
rem  Every run deletes bin\ and obj\ for both the app and the installer first, so
rem  nothing cached is ever reused and the MSI always reflects current source.
rem  This is deliberate. obj\ is the incremental cache, not bin\: MSBuild compares
rem  your sources against the assembly in obj\, so deleting bin\ on its own does
rem  not force a recompile, it just copies the same stale assembly back out of
rem  obj\ and a source edit looks like it was ignored. Stale files left in bin\
rem  are worse still, because the installer harvests that folder and would pack
rem  them into the MSI. The installer's obj\ also caches the harvested file list,
rem  so it has to go at the same time or the build fails with a WIX0103 per file
rem  that no longer exists. The cost is that every build is a full build.
rem
rem  Every variable here is prefixed SPK_. MSBuild reads environment variables as
rem  properties, so a bare name like OUTDIR or CONFIGURATION silently overrides
rem  the build's own $(OutDir) / $(Configuration) and redirects the output.
rem
rem  Spark is built with the same three properties the wixproj passes on its
rem  project reference. Spark.csproj sets RuntimeIdentifier=win-x64, so a plain
rem  "dotnet build Spark.csproj" writes the app to a win-x64\ subfolder instead;
rem  the installer harvests its source folder recursively, so that copy would be
rem  packed into the MSI on top of the flat one and roughly double its size.
rem  AppendRuntimeIdentifierToOutputPath=false keeps the output flat, where both
rem  the harvest and the bind path expect it.
rem
rem  Always builds Spark.Installer.wixproj. IgniteBot.Installer also contains a
rem  Spark.Installer.csproj, which is not in the solution and fails with MSB3441
rem  because it reads Spark.dll before the project reference has been built.
rem ---------------------------------------------------------------------------

set "SPK_ROOT=%~dp0"
if "%SPK_ROOT:~-1%"=="\" set "SPK_ROOT=%SPK_ROOT:~0,-1%"

set "SPK_CONFIG=Release"

rem "clean" is accepted and ignored: cleaning is now unconditional.
for %%A in (%*) do (
    if /I not "%%~A"=="clean" set "SPK_CONFIG=%%~A"
)

set "SPK_APP=%SPK_ROOT%\Spark.csproj"
set "SPK_INSTALLERDIR=%SPK_ROOT%\IgniteBot.Installer"
set "SPK_PROJECT=%SPK_INSTALLERDIR%\Spark.Installer.wixproj"
set "SPK_MSIDIR=%SPK_INSTALLERDIR%\Installs"

if not exist "%SPK_APP%" (
    echo ERROR: cannot find "%SPK_APP%".
    exit /b 1
)
if not exist "%SPK_PROJECT%" (
    echo ERROR: cannot find "%SPK_PROJECT%".
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet is not on PATH.
    exit /b 1
)

rem Remember the newest MSI already present, so an unchanged version number is
rem reported rather than being mistaken for a fresh build. Read before the clean
rem because Installs\ survives it, but bin\ does not.
set "SPK_PREVMSI="
for /f "delims=" %%F in ('dir /b /o-d "%SPK_MSIDIR%\*.msi" 2^>nul') do (
    if not defined SPK_PREVMSI set "SPK_PREVMSI=%%F"
)

echo [1/3] Clearing cached build output...
if exist "%SPK_ROOT%\obj" rmdir /s /q "%SPK_ROOT%\obj"
if exist "%SPK_ROOT%\bin" rmdir /s /q "%SPK_ROOT%\bin"
if exist "%SPK_INSTALLERDIR%\obj" rmdir /s /q "%SPK_INSTALLERDIR%\obj"
if exist "%SPK_INSTALLERDIR%\bin" rmdir /s /q "%SPK_INSTALLERDIR%\bin"

echo.
echo [2/3] Building Spark (%SPK_CONFIG%)...
echo.
dotnet build "%SPK_APP%" -c %SPK_CONFIG% -p:SelfContained=true -p:RuntimeIdentifier=win-x64 -p:AppendRuntimeIdentifierToOutputPath=false
if errorlevel 1 (
    echo.
    echo BUILD FAILED: Spark did not compile. The MSI was not built.
    exit /b 1
)

echo.
echo [3/3] Building the MSI (%SPK_CONFIG%)...
echo.
dotnet build "%SPK_PROJECT%" -c %SPK_CONFIG%
if errorlevel 1 (
    echo.
    echo BUILD FAILED: the installer did not link.
    exit /b 1
)

set "SPK_MSI="
for /f "delims=" %%F in ('dir /b /o-d "%SPK_MSIDIR%\*.msi" 2^>nul') do (
    if not defined SPK_MSI set "SPK_MSI=%%F"
)

if not defined SPK_MSI (
    echo.
    echo ERROR: build reported success but no .msi was written to "%SPK_MSIDIR%".
    exit /b 1
)

for %%F in ("%SPK_MSIDIR%\%SPK_MSI%") do set /a SPK_SIZEMB=%%~zF / 1048576

echo.
echo Built: %SPK_MSIDIR%\%SPK_MSI%
echo Size:  !SPK_SIZEMB! MB
if /I "%SPK_MSI%"=="%SPK_PREVMSI%" echo Note:  same filename as the previous build; the version did not change.
echo.

endlocal
exit /b 0
