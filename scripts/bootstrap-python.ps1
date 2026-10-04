param([Parameter(Mandatory=$true)][string]$BuildPython)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $root '.tools/python313'
$archive = Join-Path $root '.tools/python313.zip'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
if (!(Test-Path $archive)) { Invoke-WebRequest 'https://www.python.org/ftp/python/3.13.12/python-3.13.12-embed-amd64.zip' -OutFile $archive }
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne '76f238f606250c87c6beac75dccd35ee99070a13490555936abb6cb64ecce3d0') { throw 'Python SHA256 mismatch' }
Expand-Archive $archive -DestinationPath $runtime -Force
Set-Content -LiteralPath (Join-Path $runtime 'python313._pth') -Value @('python313.zip','.','Lib/site-packages','import site')
$python = Join-Path $runtime 'python.exe'
& $BuildPython -m pip --python $python install --disable-pip-version-check --no-warn-script-location setuptools==84.0.0 wheel==0.48.0 packaging==26.3
if ($LASTEXITCODE) { throw 'Build dependency installation failed' }
& $BuildPython -m pip --python $python install --disable-pip-version-check --no-build-isolation --no-warn-script-location -r (Join-Path $root 'worker/requirements.lock')
if ($LASTEXITCODE) { throw 'Worker dependency installation failed' }
& $python -c 'import liquidctl,hid,sys; print(sys.version); print(liquidctl.__version__)'
if ($LASTEXITCODE) { throw 'Python import verification failed' }
