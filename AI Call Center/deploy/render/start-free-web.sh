#!/bin/sh
set -eu

dotnet /app/migrations/PurpleGlass.Migrations.dll
exec dotnet /app/PurpleGlass.WebBff.dll
