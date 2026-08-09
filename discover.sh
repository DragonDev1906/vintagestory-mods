#!/usr/bin/env bash
set -euo pipefail

# Determines which mods need to be (re)built for a CI run and whether each
# built zip should be uploaded as an artifact.
#
# A mod is built when:
#   - any file inside its folder changed, or
#   - build.sh or a workflow file changed (these affect every mod), or
#   - a manual workflow_dispatch run requested it.
# The zip is only uploaded when the mod's version in modinfo.json changed
# (or the mod is new, or it was requested manually).
#
# Environment:
#   EVENT      GitHub event name: push | pull_request | workflow_dispatch
#   BEFORE     SHA of the previous branch tip (github.event.before)
#   PR_BASE    SHA of the pull request base (github.event.pull_request.base.sha)
#   INPUT_MODS workflow_dispatch input: comma-separated mods or "all"
#
# Prints a JSON array [{"mod":"...","upload":true|false}, ...] to stdout.

: "${EVENT:?EVENT must be set (push, pull_request or workflow_dispatch)}"
BEFORE="${BEFORE:-}"
PR_BASE="${PR_BASE:-}"
INPUT_MODS="${INPUT_MODS:-}"

# All mods present in the repo
all=$(find -type f -name modinfo.json -not -path '*/bin/*' -not -path '*/Releases/*' \
  | sed 's_^\./\([^/]*\)/.*_\1_' | sort -u)

build_json=""

build_all() {
  for mod in $all; do
    build_json="$build_json{\"mod\":\"$mod\",\"upload\":true},"
  done
}

case "$EVENT" in
  workflow_dispatch)
    # Build the requested mods (or all), always uploading the zip
    selected="$INPUT_MODS"
    if [ -z "$selected" ] || [ "$selected" = "all" ]; then
      selected=$all
    else
      selected=$(echo "$selected" | tr ',' ' ')
    fi
    for mod in $selected; do
      if ! echo "$all" | grep -qx "$mod"; then
        echo "WARN Skipping unknown mod '$mod'" >&2
        continue
      fi
      build_json="$build_json{\"mod\":\"$mod\",\"upload\":true},"
    done
    ;;
  *)
    # Commit to diff against
    if [ "$EVENT" = "pull_request" ]; then
      base=$(git merge-base HEAD "$PR_BASE")
    else
      base="$BEFORE"
    fi

    zero="0000000000000000000000000000000000000000"
    if [ -z "$base" ] || [ "$base" = "$zero" ]; then
      # New branch / first push / unknown base: build & upload everything
      build_all
    else
      changed_tmp=$(mktemp)
      trap 'rm -f "$changed_tmp"' EXIT
      if ! git diff --name-only "$base" HEAD > "$changed_tmp" 2>/dev/null; then
        # Could not diff against base; build & upload everything to be safe
        build_all
      else
        changed_files=$(cat "$changed_tmp")

        # Build scripts / workflow changes (e.g. a game version bump in the
        # workflow) affect every mod, but only upload if a version changed
        infra=false
        if echo "$changed_files" | grep -qE '^(build\.sh|\.github/workflows/)'; then
          infra=true
        fi

        for mod in $all; do
          mod_changed=false
          if echo "$changed_files" | grep -q "^$mod/"; then
            mod_changed=true
          fi
          if [ "$infra" = "false" ] && [ "$mod_changed" = "false" ]; then
            continue
          fi

          # Upload only when the mod's version changed (or the mod is new)
          upload=false
          if git cat-file -e "$base:$mod/modinfo.json" 2>/dev/null; then
            base_version=$(git show "$base:$mod/modinfo.json" | jq -r '.version')
            cur_version=$(jq -r '.version' "$mod/modinfo.json")
            if [ "$base_version" != "$cur_version" ]; then
              upload=true
            fi
          else
            upload=true
          fi

          build_json="$build_json{\"mod\":\"$mod\",\"upload\":$upload},"
        done
      fi
    fi
    ;;
esac

build_json="[${build_json%,}]"
echo "$build_json"
