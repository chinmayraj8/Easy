#!/bin/zsh
# Builds Easy from this checkout into ~/Applications/Easy.app and restarts it, signed with a local certificate.
# macOS ties the Accessibility permission to the signature: with an ad-hoc one every build is a new app and the
# donut gesture stops; with a certificate the permission stays across builds. The first run creates that certificate
# ("Easy Local Signing", in the login keychain, for code signing only) and asks for the keychain password once.

set -euo pipefail
repo=${0:A:h:h:h}
app=~/Applications/Easy.app
identity="Easy Local Signing"
dotnet=/usr/local/share/dotnet/dotnet
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT

if ! security find-certificate -c "$identity" ~/Library/Keychains/login.keychain-db >/dev/null 2>&1; then
    echo "Creating the signing certificate \"$identity\"…"
    cat > "$out/cfg" <<'EOF'
[req]
distinguished_name=dn
x509_extensions=ext
prompt=no
[dn]
CN=Easy Local Signing
[ext]
basicConstraints=critical,CA:false
keyUsage=critical,digitalSignature
extendedKeyUsage=critical,codeSigning
EOF
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config "$out/cfg" -keyout "$out/key.pem" -out "$out/cert.pem" 2>/dev/null
    openssl pkcs12 -export -legacy -inkey "$out/key.pem" -in "$out/cert.pem" -name "$identity" -out "$out/id.p12" -passout pass:easy 2>/dev/null \
        || openssl pkcs12 -export -inkey "$out/key.pem" -in "$out/cert.pem" -name "$identity" -out "$out/id.p12" -passout pass:easy
    security import "$out/id.p12" -k ~/Library/Keychains/login.keychain-db -P easy -T /usr/bin/codesign
    security add-trusted-cert -p codeSign -k ~/Library/Keychains/login.keychain-db "$out/cert.pem"
    first_time=1
fi

echo "Building…"
"$dotnet" publish "$repo/src/Filee.App" -c Release -r osx-arm64 --self-contained -o "$out/publish" -v quiet -nologo

osascript -e 'quit app "Easy"' 2>/dev/null || true
for _ in {1..20}; do pgrep -f "$app/Contents/MacOS/Filee" >/dev/null || break; sleep 0.5; done
pkill -f "$app/Contents/MacOS/Filee" 2>/dev/null || true

cp "$out"/publish/Filee.dll "$out"/publish/Filee.Core.dll "$out"/publish/Filee.Engines.dll "$out"/publish/Filee.Platform.MacOS.dll "$app/Contents/MacOS/"
codesign --force --deep --sign "$identity" --identifier com.chinmayraj8.easy "$app"
codesign --verify --deep "$app"
open "$app"

if [[ -n ${first_time:-} ]]; then
    # Last time: the permission given to the ad-hoc build doesn't carry over to the certificate.
    tccutil reset Accessibility com.chinmayraj8.easy >/dev/null
    open "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"
    echo "Switch Easy on under Accessibility once more. From now on it stays on after every install."
fi
echo "Easy is installed and running."
