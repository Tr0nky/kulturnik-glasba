# Start the app unless it is already running, then open it in the default browser.
# The Glasba desktop shortcut runs this; the server quits itself after 30 idle minutes.
Set-Location $PSScriptRoot
$url = 'http://localhost:5080/'

function Test-Running {
    curl.exe -s -o NUL --max-time 2 "${url}ping"
    $LASTEXITCODE -eq 0
}

if (-not (Test-Running)) {
    Start-Process dotnet -ArgumentList 'run', 'app.cs' -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
    foreach ($i in 1..90) {
        if (Test-Running) { break }
        Start-Sleep -Seconds 1
    }
}
Start-Process $url
