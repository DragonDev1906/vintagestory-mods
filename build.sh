# If no arguments are given, build everything
if [ $# -eq 0 ]; then
    mods=$(find -type f -name modinfo.json -not -path '*/bin/*' -not -path '*/Releases/*' | sed 's_^\./\([^/]*\).*_\1_')
else
    mods=$@
fi

# Make sure the output directory exists
mkdir -p Releases

function start() {
    echo -e "\x1b[94m$1\x1b[0m ${@:2}"
}
function error() {
    echo -e "\x1b[91mERROR\x1b[0m $*"
}
function warn() {
    echo -e "\x1b[93mWARN \x1b[0m $*"
}
function info() {
    echo "INFO  $*"
}

# Go through all mods we want to build
for mod in $mods; do
    if [ ! -f "$mod/modinfo.json" ]; then
        # echo "WARN Skipping mod '$mod' because it doesn't have modinfo.json in the expected location"
        warn "Skipping mod '$mod' because it doesn't have modinfo.json in the expected location"
        continue 
    fi
    version=$(cat "$mod/modinfo.json" |jq -r '.version')
    name=$(cat "$mod/modinfo.json" | jq -r '.name')
    output="Releases/${mod}_$version.zip"

    start $mod $version

    # Remove/Overwrite old versions with the same name
    rm -f -- "$output"

    # Check json validity
    jsonfiles=$(find "$mod/assets/" -type f -name '*.json')
    for f in $jsonfiles; do
        # TODO: We may want to check the schema (based on the path the file is in)
        if ! json5 -v "$f"; then
            error "Invalid json5"
            continue 2
        fi
    done

    # TODO: Compile

    # Build zip file
    cd "$mod"
    zip -rq "../$output" \
        "assets" \
        "modinfo.json" \
        "modicon.png" \
        "README.md"
    cd ..
done
