#!/usr/bin/env bash
#
# Setup development mods for Vintage Story.
#
# If no arguments are given, shows current status without changing anything.
# If mod names are given, symlinks (or prepares) only those mods.
#
# Usage:
#   ./setup-dev.sh [mod ...]    Link specified mods
#   ./setup-dev.sh --all        Link all mods in the repository
#   ./setup-dev.sh --clean      Remove all symlinks created by this script
#   ./setup-dev.sh              Show current status only

set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_CACHE="$HOME/.cache/vsmodsetup"

# ── Helpers ──────────────────────────────────────────────────────────────────

error() { echo -e "\x1b[91mERROR\x1b[0m $*" >&2; }
warn()  { echo -e "\x1b[93mWARN \x1b[0m $*" >&2; }
info()  { echo "INFO  $*"; }

# Resolve GitHub owner/repo from git remote
resolve_github_repo() {
    local url
    url="$(git -C "$REPO_DIR" remote get-url origin 2>/dev/null)" || return 1
    url="${url#https://github.com/}"
    url="${url#git@github.com:}"
    url="${url%.git}"
    echo "$url"
}

# ── Discover mods in the repository ──────────────────────────────────────────

discover_mods() {
    local mods=()
    while IFS= read -r f; do
        local dir
        dir="$(dirname "$f")"
        dir="${dir#"$REPO_DIR"/}"
        if [ -d "$REPO_DIR/$dir" ] && [ "$dir" != "." ]; then
            mods+=("$dir")
        fi
    done < <(find "$REPO_DIR" -maxdepth 2 -name modinfo.json \
        -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/Releases/*' \
        | sort)
    printf '%s\n' "${mods[@]}"
}

# ── Detect Vintage Story Mods directory ──────────────────────────────────────

find_vs_mods_dir() {
    if [ -n "${VINTAGE_STORY:-}" ] && [ -d "$VINTAGE_STORY/Mods" ]; then
        echo "$VINTAGE_STORY/Mods"
    elif [ -d "$HOME/.config/VintageStoryData/Mods" ]; then
        echo "$HOME/.config/VintageStoryData/Mods"
    elif [ -d "$HOME/.local/share/VintageStoryData/Mods" ]; then
        echo "$HOME/.local/share/VintageStoryData/Mods"
    else
        echo ""
    fi
}

# ── Build or download a code mod into the build cache ───────────────────────
# The cache holds compiled DLLs for code mods so symlinks can point to the
# repo directory while DLLs live alongside. Content-only mods need no cache.

prepare_code_mod() {
    local mod="$1"
    local cachedir="$BUILD_CACHE/$mod"
    mkdir -p "$cachedir"

    # If cached DLL already exists and is newer than the csproj, skip rebuild
    if [ -f "$cachedir/$mod.dll" ] && \
       [ "$cachedir/$mod.dll" -nt "$REPO_DIR/$mod/$mod.csproj" ]; then
        return 0
    fi

    # Code mod with dotnet available: compile locally
    if command -v dotnet &>/dev/null; then
        rm -rf -- "$REPO_DIR/$mod/bin" "$REPO_DIR/$mod/obj"
        if dotnet publish "$REPO_DIR/$mod/$mod.csproj" -c Release \
            >/dev/null 2>&1; then
            local output="$mod/bin/Release/Mods/mod/publish"
            if [ -d "$output" ]; then
                rm -f -- "$cachedir"/*.dll "$cachedir"/*.pdb
                cp "$output"/*.dll "$cachedir/" 2>/dev/null || true
                cp "$output"/*.pdb "$cachedir/" 2>/dev/null || true
            fi
            rm -rf -- "$REPO_DIR/$mod/bin" "$REPO_DIR/$mod/obj"
            return 0
        fi
        rm -rf -- "$REPO_DIR/$mod/bin" "$REPO_DIR/$mod/obj"
        warn "Local build failed for '$mod', trying CI artifacts..."
    fi

    # Fallback: download from CI (GitHub Actions artifacts)
    if ! download_artifact "$mod" "$cachedir"; then
        warn "Could not get compiled DLL for '$mod' from CI. Build locally with 'dotnet publish' or trigger a CI build."
        return 1
    fi
}

# ── Download latest CI artifact for a mod ────────────────────────────────────

download_artifact() {
    local mod="$1"
    local dest="$2"

    local github_repo
    github_repo="$(resolve_github_repo)" \
        || { error "Could not determine GitHub repository from git remote."; return 1; }

    local branch
    branch="$(git -C "$REPO_DIR" symbolic-ref --short HEAD 2>/dev/null || echo "main")"

    local run_json
    run_json="$(curl -fsSL \
        "https://api.github.com/repos/$github_repo/actions/workflows/build.yml/runs?branch=$branch&status=success&per_page=10" 2>/dev/null)" \
        || { warn "Could not fetch workflow runs. Check your network connection."; return 1; }

    local run_id
    run_id="$(echo "$run_json" | jq -r '.workflow_runs[0].id // empty')" || true
    [ -z "$run_id" ] && { warn "No successful CI runs found for branch '$branch'."; return 1; }

    local artifacts
    artifacts="$(curl -fsSL \
        "https://api.github.com/repos/$github_repo/actions/runs/$run_id/artifacts" 2>/dev/null)" \
        || { warn "Could not fetch artifacts for run $run_id."; return 1; }

    local artifact_id
    artifact_id="$(echo "$artifacts" | jq -r --arg n "$mod" \
        '.artifacts[] | select(.name == $n) | .id // empty' | head -1)" || true
    [ -z "$artifact_id" ] && { warn "No CI artifact found for '$mod'."; return 1; }

    local tmp_zip
    tmp_zip="$(mktemp)"
    if ! curl -fsSL \
        -H "Accept: application/vnd.github+json" \
        "https://api.github.com/repos/$github_repo/actions/artifacts/$artifact_id/zip" \
        -o "$tmp_zip" 2>/dev/null; then
        rm -f "$tmp_zip"
        warn "Failed to download artifact for '$mod'."
        return 1
    fi

    local extract_dir
    extract_dir="$(mktemp -d)"
    unzip -q -o "$tmp_zip" -d "$extract_dir" 2>/dev/null
    rm -f "$tmp_zip"

    local inner_zip
    inner_zip="$(find "$extract_dir" -name "${mod}_*.zip" -type f | head -1)"
    if [ -z "$inner_zip" ]; then
        rm -rf "$extract_dir"
        warn "Artifact for '$mod' did not contain the expected zip file."
        return 1
    fi

    local inner_dir
    inner_dir="$(mktemp -d)"
    unzip -q -o "$inner_zip" -d "$inner_dir" 2>/dev/null

    rm -f -- "$dest"/*.dll "$dest"/*.pdb
    find "$inner_dir" -maxdepth 1 -name '*.dll' -exec cp {} "$dest/" \;
    find "$inner_dir" -maxdepth 1 -name '*.pdb' -exec cp {} "$dest/" \;

    rm -rf "$inner_dir" "$extract_dir"
    return 0
}

# ── Symlink management ──────────────────────────────────────────────────────

MODS_DIR=""

remove_all_links() {
    local mods_dir="$1"
    [ -d "$mods_dir" ] || return

    local count=0
    while IFS= read -r link; do
        [ -L "$link" ] || continue
        local target
        target="$(readlink "$link")"
        # Remove symlinks pointing to repo dir or build cache
        [[ "$target" == "$REPO_DIR/"* ]] || [[ "$target" == "$BUILD_CACHE/"* ]] || continue
        rm "$link"
        count=$((count + 1))
    done < <(find "$mods_dir" -maxdepth 1 -mindepth 1 -type l 2>/dev/null)
    echo "$count"
}

# For content mods: symlink points to the repo directory directly.
# For code mods: symlink points to the repo directory, DLLs are bind-mounted
# via a wrapper directory in the build cache that contains only the DLLs.
# Actually, for code mods we create a directory that is a symlink to the repo
# dir plus a copy of the DLLs. Since we can't overlay, we use a different
# approach: for code mods, the "mod dir" is a real directory containing
# symlinks to repo assets + the DLLs from cache.
#
# Simpler approach: all mods get a directory in the cache that contains:
#   - Symlinks to all repo files (modinfo.json, assets/, modicon.png, README.md)
#   - DLLs (for code mods)
# The Mods dir symlink then points to this cache directory.
# But this means edits to assets go through a double symlink...
#
# Actually, simplest approach: symlinks point to the repo directory.
# For code mods, we also copy DLLs into a wrapper directory that:
#   1. Is a real directory
#   2. Contains symlinks to all repo files
#   3. Also contains the DLLs
# This way edits to assets in the wrapper dir's symlinks go directly to the repo.

create_links() {
    local mods_dir="$1"
    shift
    local mods=("$@")
    local created=0 skipped=0

    for mod in "${mods[@]}"; do
        local modinfo="$REPO_DIR/$mod/modinfo.json"
        if [ ! -f "$modinfo" ]; then
            warn "Cannot link '$mod': no modinfo.json found."
            skipped=$((skipped + 1))
            continue
        fi

        local modid
        modid="$(jq -r '.modid // empty' "$modinfo" 2>/dev/null || true)"
        [ -z "$modid" ] && modid="$mod"

        if [ -e "$mods_dir/$modid" ] && [ ! -L "$mods_dir/$modid" ]; then
            warn "Skipping '$modid': a non-symlink directory already exists in Mods."
            skipped=$((skipped + 1))
            continue
        fi

        rm -f "$mods_dir/$modid" 2>/dev/null || true

        if [ ! -f "$REPO_DIR/$mod/$mod.csproj" ]; then
            # Content-only mod: symlink directly to repo
            if ln -s "$REPO_DIR/$mod" "$mods_dir/$modid" 2>/dev/null; then
                created=$((created + 1))
            else
                warn "Failed to create symlink for '$modid'."
                skipped=$((skipped + 1))
            fi
        else
            # Code mod: create wrapper directory with symlinks + DLLs
            local wrapper="$BUILD_CACHE/$mod"
            mkdir -p "$wrapper"

            # Create symlinks to repo files
            for entry in modinfo.json assets modicon.png README.md; do
                if [ -e "$REPO_DIR/$mod/$entry" ] && [ ! -e "$wrapper/$entry" ]; then
                    ln -s "$REPO_DIR/$mod/$entry" "$wrapper/$entry" 2>/dev/null || true
                fi
            done

            # Symlink the wrapper directory into Mods
            if ln -s "$wrapper" "$mods_dir/$modid" 2>/dev/null; then
                created=$((created + 1))
            else
                warn "Failed to create symlink for '$modid'."
                skipped=$((skipped + 1))
            fi
        fi
    done
    echo "created=$created skipped=$skipped"
}

# ── Main ────────────────────────────────────────────────────────────────────

main() {
    [ -f "$REPO_DIR/Solution.sln" ] || { error "Not running from the repository root."; exit 1; }

    MODS_DIR="$(find_vs_mods_dir)"
    [ -n "$MODS_DIR" ] || { error "Could not find Vintage Story Mods directory."; exit 1; }

    # Discover available mods
    local available_mods=()
    while IFS= read -r m; do
        [ -n "$m" ] && available_mods+=("$m")
    done < <(discover_mods)

    local has_dotnet=false has_jq=false
    command -v dotnet &>/dev/null && has_dotnet=true
    command -v jq &>/dev/null && has_jq=true

    if ! $has_jq; then
        error "'jq' is required but not installed."
        exit 1
    fi

    # Parse arguments
    if [ $# -eq 0 ]; then
        echo "Repo mods:  ${available_mods[*]}"
        local currently_linked=()
        while IFS= read -r link; do
            [ -L "$link" ] || continue
            local target
            target="$(readlink "$link")"
            [[ "$target" == "$REPO_DIR/"* || "$target" == "$BUILD_CACHE/"* ]] || continue
            local name
            name="$(basename "$link")"
            local mi="$link/modinfo.json"
            if [ -f "$mi" ]; then
                name="$(jq -r '.modid // empty' "$mi" 2>/dev/null || echo "$name")"
            fi
            currently_linked+=("$name")
        done < <(find "$MODS_DIR" -maxdepth 1 -mindepth 1 2>/dev/null)
        if [ ${#currently_linked[@]} -gt 0 ]; then
            echo "Linked:     ${currently_linked[*]}"
        else
            echo "Linked:     (none)"
        fi
        echo "dotnet:     $has_dotnet"
        echo ""
        echo "No mods specified, nothing changed. Use --all or pass mod names."
        return
    fi

    local requested_mods=()
    local clean=false

    while [ $# -gt 0 ]; do
        case "$1" in
            --help|-h)
                echo "Usage: $0 [mod ...]"
                echo "      $0 --all"
                echo "      $0 --clean"
                echo ""
                echo "No arguments: show current status without changes."
                echo "--all:        link all mods from the repository."
                echo "--clean:      remove all symlinks pointing to this repository."
                echo "[mod ...]:    link only the specified mods."
                echo ""
                echo "Available mods: ${available_mods[*]}"
                return
                ;;
            --clean) clean=true ;;
            --all)
                requested_mods=("${available_mods[@]}")
                ;;
            *)
                local found=false
                for a in "${available_mods[@]}"; do
                    if [ "$1" = "$a" ]; then found=true; break; fi
                done
                if $found; then
                    requested_mods+=("$1")
                else
                    error "Unknown mod '$1'. Available: ${available_mods[*]}"
                    exit 1
                fi
                ;;
        esac
        shift
    done

    if $clean; then
        local removed
        removed="$(remove_all_links "$MODS_DIR")"
        info "Removed $removed symlinks."
        return
    fi

    if [ ${#requested_mods[@]} -eq 0 ]; then
        info "No mods specified."
        return
    fi

    local success=0 fail=0

    for mod in "${requested_mods[@]}"; do
        if [ -f "$REPO_DIR/$mod/$mod.csproj" ]; then
            if prepare_code_mod "$mod"; then
                success=$((success + 1))
            else
                warn "Failed to prepare '$mod'."
                fail=$((fail + 1))
            fi
        else
            # Content-only mod: no preparation needed
            success=$((success + 1))
        fi
    done

    if [ "$success" -eq 0 ]; then
        error "No mods were prepared successfully."
        exit 1
    fi

    # Remove old symlinks and create new ones
    remove_all_links "$MODS_DIR" >/dev/null
    local link_result
    link_result="$(create_links "$MODS_DIR" "${requested_mods[@]}")"
    local created skipped
    created="$(echo "$link_result" | grep -o 'created=[0-9]*' | cut -d= -f2)"
    skipped="$(echo "$link_result" | grep -o 'skipped=[0-9]*' | cut -d= -f2)"

    info "Done. Linked $created mod(s) to $MODS_DIR"
    [ "${skipped:-0}" -gt 0 ] && warn "$skipped mod(s) skipped."
    [ "${fail:-0}" -gt 0 ] && warn "$fail mod(s) failed to prepare."
}

main "$@"
