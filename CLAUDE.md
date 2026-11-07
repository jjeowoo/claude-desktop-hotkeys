# Notes for Claude

- When work on this project changes the StrokesPlus settings (`%APPDATA%\StrokesPlus.net\StrokesPlus.net.json`),
  back them up first. `Install.ps1` does this itself; for any other change, run
  `powershell -NoProfile -ExecutionPolicy Bypass -File .\Backup-StrokesPlus.ps1` first and stop if it fails.
