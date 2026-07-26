#!/bin/sh
set -eu

exec dotnet /app/PurpleGlass.Integrations.Worker.dll
