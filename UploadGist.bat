@echo off
powershell Compress-Archive -Path out/* -DestinationPath AlphaChannel.zip
powershell -Command "Get-Content AlphaChannel/AlphaChannel.json | Out-String | ForEach-Object { '[{0}]' -f $_ } | Set-Content pluginmaster.json"
Addgist.sh