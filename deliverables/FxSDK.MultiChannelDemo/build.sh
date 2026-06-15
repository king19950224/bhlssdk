#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

CONFIG=${1:-Release}

dotnet restore FxSDK.MultiChannelDemo.sln
dotnet build FxSDK.MultiChannelDemo.sln -c "$CONFIG" --no-restore
