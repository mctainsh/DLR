@echo off
setlocal enabledelayedexpansion
rem ===========================================================================
rem  Publish-Android.bat
rem
rem  Builds the signed Android App Bundle (.aab) for upload to Google Play.
rem  Play has not accepted an APK for a new app or an update since August 2021,
rem  so the bundle is the only publishable format -- AndroidPackageFormat=aab is
rem  already set in BlazorDLR.csproj for Release/android.
rem
rem  Usage:
rem    Publish-Android.bat              full run (preflight + format + tests + publish)
rem    Publish-Android.bat /skiptests   skip the test suite (no Docker needed)
rem    Publish-Android.bat /force       publish from a dirty or unpushed tree
rem
rem  Signing material is read from the environment, never from this repo:
rem    DLR_ANDROID_KEYSTORE / _KEYSTORE_PASS / _KEY_ALIAS / _KEY_PASS
rem  If they are not already set, they are sourced from
rem    %USERPROFILE%\.dlr-signing\dlr-signing-env.bat
rem  which Create-AndroidUploadKey.bat writes. Run that first if you have no key.
rem ===========================================================================

pushd "%~dp0"

set "SKIPTESTS="
set "FORCE="
:parseargs
if "%~1"=="" goto argsdone
if /i "%~1"=="/skiptests" set "SKIPTESTS=1"
if /i "%~1"=="/force"     set "FORCE=1"
shift
goto parseargs
:argsdone

rem  NOTE: every variable below is DLR_-prefixed on purpose. "dotnet publish"
rem  imports the process environment as MSBuild properties, so a bare OUTDIR here
rem  silently becomes $(OutDir) and redirects every project's build output into a
rem  nested folder. Do not drop the prefix.
set "DLR_PROJ=BlazorDLR\BlazorDLR.csproj"
set "DLR_TFM=net10.0-android"
set "DLR_OUTDIR=BlazorDLR\bin\Release\%DLR_TFM%\publish"

echo ===========================================================================
echo  DLR -- Android release publish
echo ===========================================================================

rem --- 1. Signing material --------------------------------------------------
if not defined DLR_ANDROID_KEYSTORE (
	if exist "%USERPROFILE%\.dlr-signing\dlr-signing-env.bat" (
		echo [1/6] Sourcing signing environment from %USERPROFILE%\.dlr-signing\
		call "%USERPROFILE%\.dlr-signing\dlr-signing-env.bat"
	)
)
if not defined DLR_ANDROID_KEYSTORE (
	echo ERROR: DLR_ANDROID_KEYSTORE is not set and no signing env file was found.
	echo        Run Create-AndroidUploadKey.bat first, or set the four
	echo        DLR_ANDROID_* variables from your CI secret store.
	echo.
	echo        Without them the csproj deliberately produces an UNSIGNED build
	echo        rather than failing -- which Play will reject, so stop here.
	goto :fail
)
if not exist "%DLR_ANDROID_KEYSTORE%" (
	echo ERROR: keystore not found at %DLR_ANDROID_KEYSTORE%
	goto :fail
)
if not defined DLR_ANDROID_KEY_ALIAS (
	echo ERROR: DLR_ANDROID_KEY_ALIAS is not set.
	goto :fail
)
echo [1/6] Signing key OK  (%DLR_ANDROID_KEYSTORE%, alias %DLR_ANDROID_KEY_ALIAS%^)

rem --- 2. Versions agree in all three places --------------------------------
rem  store-release.md's release checklist says these are maintained by hand in
rem  the csproj, AndroidManifest.xml and Info.plist and must agree. A mismatched
rem  versionCode is an upload Play refuses, after the twenty-minute build.
for /f "delims=" %%V in ('powershell -NoProfile -Command "([xml](Get-Content '%DLR_PROJ%')).Project.PropertyGroup.ApplicationVersion.Where({$_},'First',1)"') do set "CSPROJ_CODE=%%V"
for /f "delims=" %%V in ('powershell -NoProfile -Command "([xml](Get-Content '%DLR_PROJ%')).Project.PropertyGroup.ApplicationDisplayVersion.Where({$_},'First',1)"') do set "CSPROJ_NAME=%%V"
for /f "delims=" %%V in ('powershell -NoProfile -Command "([xml](Get-Content 'BlazorDLR\Platforms\Android\AndroidManifest.xml')).manifest.versionCode"') do set "MANIFEST_CODE=%%V"
for /f "delims=" %%V in ('powershell -NoProfile -Command "([xml](Get-Content 'BlazorDLR\Platforms\Android\AndroidManifest.xml')).manifest.versionName"') do set "MANIFEST_NAME=%%V"

echo [2/6] Version %CSPROJ_NAME% (build %CSPROJ_CODE%^)
if not "%CSPROJ_CODE%"=="%MANIFEST_CODE%" (
	echo ERROR: version code mismatch -- csproj ApplicationVersion=%CSPROJ_CODE%, AndroidManifest versionCode=%MANIFEST_CODE%
	goto :fail
)
if not "%CSPROJ_NAME%"=="%MANIFEST_NAME%" (
	echo ERROR: version name mismatch -- csproj ApplicationDisplayVersion=%CSPROJ_NAME%, AndroidManifest versionName=%MANIFEST_NAME%
	goto :fail
)

rem --- 3. Clean, committed, pushed tree -------------------------------------
rem  Directory.Build.targets appends ".dirty" to SourceRevisionId when git
rem  reports uncommitted changes, and that string is visible to end users at
rem  GET /api/v1/about (section 14.6.2). A shipped build must not carry it.
for /f "delims=" %%S in ('git status --porcelain 2^>nul') do set "DIRTY=1"
if defined DIRTY (
	if not defined FORCE (
		echo ERROR: working tree has uncommitted changes.
		echo        The build would be stamped ".dirty" and end users can see it
		echo        at GET /api/v1/about. Commit and push, or pass /force.
		git status --short
		goto :fail
	)
	echo [3/6] WARNING: dirty tree, publishing anyway ^(/force^)
) else (
	echo [3/6] Working tree clean
)

rem --- 4. Format gate -------------------------------------------------------
echo [4/6] dotnet format --verify-no-changes
dotnet format BlazorDLR.slnx --verify-no-changes
if errorlevel 1 (
	echo ERROR: formatting check failed. Run: dotnet format BlazorDLR.slnx
	goto :fail
)

rem --- 5. Tests -------------------------------------------------------------
if defined SKIPTESTS (
	echo [5/6] Tests SKIPPED ^(/skiptests^)
) else (
	echo [5/6] dotnet test  -- needs Docker running for the server integration tests
	dotnet test BlazorDLR.slnx --configuration Release
	if errorlevel 1 (
		echo ERROR: tests failed.
		goto :fail
	)
)

rem --- 6. Publish -----------------------------------------------------------
echo [6/6] dotnet publish -f %DLR_TFM% -c Release
dotnet publish "%DLR_PROJ%" -f %DLR_TFM% -c Release
if errorlevel 1 (
	echo ERROR: publish failed.
	goto :fail
)

rem --- Report ---------------------------------------------------------------
set "AAB="
for %%F in ("%DLR_OUTDIR%\*-Signed.aab") do set "AAB=%%~fF"
if not defined AAB (
	echo ERROR: no *-Signed.aab in %DLR_OUTDIR%
	echo        An unsigned bundle means the signing properties did not apply --
	echo        check the four DLR_ANDROID_* variables reached MSBuild.
	goto :fail
)

echo.
echo ===========================================================================
echo  BUILD OK
echo.
echo   Bundle    %AAB%
for %%F in ("%AAB%") do echo   Size      %%~zF bytes
echo   Version   %CSPROJ_NAME% (%CSPROJ_CODE%^)
echo.
echo  Other artefacts in the publish folder:
dir /b "%DLR_OUTDIR%" 2>nul
echo.
echo  Next steps ^(Documentation/store-release.md has the detail^):
echo   1. Play Console -^> Production -^> Create new release -^> upload the .aab
echo   2. Upload the R8 deobfuscation mapping so crash reports are readable:
echo        %DLR_OUTDIR%\mapping.txt
echo      ^(Play Console -^> the release -^> App bundle explorer -^> Downloads^)
echo   3. Re-record the background-location declaration video if the disclosure
echo      dialog copy changed -- store-release.md flags this as outstanding
echo   4. Confirm the target API level still clears Play's current floor
echo ===========================================================================
popd
endlocal
exit /b 0

:fail
echo.
echo PUBLISH ABORTED.
popd
endlocal
exit /b 1
