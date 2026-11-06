#!/bin/zsh
# Start the app unless it is already running, then open it in the default browser.
# Glasba.app runs this; the server quits itself after 30 idle minutes.
cd "${0:A:h}"
export PATH="/usr/local/share/dotnet:/usr/local/bin:$PATH"
url=http://localhost:5080/

if ! curl -s -o /dev/null --max-time 2 "${url}ping"; then
  nohup dotnet run app.cs > server.log 2>&1 < /dev/null &
  for i in {1..90}; do
    curl -s -o /dev/null --max-time 2 "${url}ping" && break
    sleep 1
  done
fi
open "$url"
