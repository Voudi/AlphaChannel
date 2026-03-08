#!/bin/bash
set -e  # Stop on errors
pwsh -Command "Compress-Archive -Update -Path 'out/*' -DestinationPath 'AlphaChannel.zip'"
pwsh -Command "Get-Content 'AlphaChannel/AlphaChannel.json' | Out-String | ForEach-Object { '[{0}]' -f \$_ } | Set-Content 'pluginmaster.json'"
./AddgistTestversion.sh