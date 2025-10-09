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

root=$(pwd)

modcount=0
successcount=0

# Go through all mods we want to build
for mod in $mods; do
    ((modcount++))
    if [ ! -f "$mod/modinfo.json" ]; then
        # echo "WARN Skipping mod '$mod' because it doesn't have modinfo.json in the expected location"
        warn "Skipping mod '$mod' because it doesn't have modinfo.json in the expected location"
        continue 
    fi
    version=$(cat "$mod/modinfo.json" |jq -r '.version')
    name=$(cat "$mod/modinfo.json" | jq -r '.name')
    compile_output="$mod/bin/Release/Mods/mod/publish"
    output="Releases/${mod}_$version.zip"

    start $mod $version

    # Remove/Overwrite old versions with the same name and final compilation artifacts
    rm -rf -- "$output" "$compile_output"

    # Check general json validity
    jsonfiles=$(find "$mod/assets/" -type f -name '*.json')
    for f in $jsonfiles; do
        # TODO: We may want to check the schema (based on the path the file is in)
        if ! json5 -v "$f"; then
            error "Invalid json5"
            continue 2
        fi
    done

    # Compile
    if [ -f "$mod/$mod.csproj" ]; then
        dotnet publish "$mod/$mod.csproj" -c Release
    fi

    # Build zip file
    # I have not found a better way to do this than having separate zip commands.
    cd "$mod"
    zip -rq "../$output" \
        "assets" \
        "modinfo.json" \
        "modicon.png" \
        "README.md"

    # Add dll
    if [ -f "$mod.csproj" ]; then
        cd "bin/Release/Mods/mod/publish"
        zip -rq "$root/$output" .
    fi

    # Reset working directory
    cd "$root"

    ((successcount++))
done

if [ ! "$successcount" -eq "$modcount" ]; then
    warn "Could not build $((modcount - successcount))/$modcount mods"
    exit 1
fi
