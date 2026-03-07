#!/bin/bash
# Build, install and run MassangerMaximka on Android emulator.
# Start the emulator first (e.g. from Android Studio or: emulator -avd Medium_Phone_API_36.1)

set -e
cd "$(dirname "$0")"
ADB="${HOME}/Library/Android/sdk/platform-tools/adb"
JDK="/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home"

echo "Checking for device..."
if ! $ADB devices | grep -q 'device$'; then
  echo "No device/emulator found. Start an AVD first, e.g.:"
  echo "  \$HOME/Library/Android/sdk/emulator/emulator -avd Medium_Phone_API_36.1"
  exit 1
fi

VOICE_PORT=45780
AUTH_TOKEN=$(cat "$HOME/.emulator_console_auth_token" 2>/dev/null || echo "")
if [ -n "$AUTH_TOKEN" ]; then
  echo "Setting up UDP port forwarding (voice port $VOICE_PORT)..."
  (echo "auth $AUTH_TOKEN"; sleep 0.3; echo "redir add udp:${VOICE_PORT}:${VOICE_PORT}"; sleep 0.3; echo "quit") \
    | nc localhost 5554 > /dev/null 2>&1 || true
fi

echo "Building and installing..."
dotnet build MassangerMaximka/MassangerMaximka.csproj -f net9.0-android -t:Install \
  -p:JavaSdkDirectory="$JDK" \
  -p:AcceptAndroidSDKLicenses=true

echo "Launching app..."
$ADB shell am start -n com.companyname.massangermaximka/crc64774254c419b4816d.MainActivity
