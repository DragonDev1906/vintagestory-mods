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
# Environment (used by .github/workflows/build.yml):
#   EVENT      GitHub event name: push | pull_request | workflow_dispatch
#   BEFORE     SHA of the previous branch tip (github.event.before)
#   PR_BASE    SHA of the pull request base (github.event.pull_request.base.sha)
#   INPUT_MODS workflow_dispatch input: comma-separated mods or "all"
#
# Without EVENT it behaves as if a `git push` were about to happen on the
# current checkout: on the default branch it compares against the last pushed
# commit (the upstream tracking ref), on any other branch against the fork
# point (merge-base with the default branch). Uncommitted working-tree changes
# are taken into account as well.
#
# Output: a JSON array to stdout (valid for `fromJSON` in CI). The outer level
# is indented with each mod on its own line. When stdout is a terminal the
# output is colorized (mod names, upload flags, brackets); pipes and CI get
# plain JSON.

BEFORE="${BEFORE:-}"
PR_BASE="${PR_BASE:-}"
INPUT_MODS="${INPUT_MODS:-}"
EVENT="${EVENT:-}"

LOCAL=false
if [ -z "$EVENT" ]; then
  LOCAL=true
  EVENT=push
fi

# Colors are only used when the output is a terminal, so CI and pipes stay
# plain JSON.
c_dim=""
c_reset=""
c_mod=""
c_yes=""
c_no=""
color_sed=""
if [ -t 1 ]; then
  c_dim=$(printf '\033[2m')
  c_reset=$(printf '\033[0m')
  c_mod=$(printf '\033[1;94m')
  c_yes=$(printf '\033[32m')
  c_no=$(printf '\033[91m')
  color_sed="s/\"mod\":\"\([^\"]*\)\"/\"mod\":\"${c_mod}\1${c_reset}\"/;s/\"upload\":true/\"upload\":${c_yes}true${c_reset}/;s/\"upload\":false/\"upload\":${c_no}false${c_reset}/"
fi

warn() {
  if [ -t 2 ]; then
    printf '\033[93mWARN\033[0m %s\n' "$*" >&2
  else
    printf 'WARN %s\n' "$*" >&2
  fi
}

# All mods present in the repo
all=$(find -type f -name modinfo.json -not -path '*/bin/*' -not -path '*/Releases/*' \
  | sed 's_^\./\([^/]*\)/.*_\1_' | sort -u)

build_json=""

build_all() {
  for mod in $all; do
    build_json="$build_json{\"mod\":\"$mod\",\"upload\":true},"
  done
}

default_branch() {
  local d
  d=$(git symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null || true)
  if [ -n "$d" ]; then
    echo "$d"
    return
  fi
  for b in main master; do
    if git show-ref --quiet "refs/heads/$b" 2>/dev/null || git show-ref --quiet "refs/remotes/origin/$b" 2>/dev/null; then
      echo "$b"
      return
    fi
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
        warn "Skipping unknown mod '$mod'"
        continue
      fi
      build_json="$build_json{\"mod\":\"$mod\",\"upload\":true},"
    done
    ;;
  pull_request)
    base=$(git merge-base HEAD "$PR_BASE")
    ;;
  *)
    if [ "$LOCAL" = true ]; then
      # Emulate a push on the current branch
      cur=$(git rev-parse --abbrev-ref HEAD)
      def=$(default_branch)
      if [ -n "$def" ] && [ "$cur" = "$def" ]; then
        # Default branch: compare against the last pushed commit
        base=$(git rev-parse --quiet --verify "@{upstream}" 2>/dev/null || true)
        if [ -z "$base" ]; then
          base=$(git rev-parse --quiet --verify "origin/$cur" 2>/dev/null || true)
        fi
        [ -z "$base" ] && base=HEAD
      else
        # Other branch: compare against the fork point with the default branch
        base=""
        if [ -n "$def" ]; then
          ref="refs/remotes/origin/$def"
          if ! git show-ref --quiet "$ref" 2>/dev/null; then
            ref="$def"
          fi
          base=$(git merge-base HEAD "$ref" 2>/dev/null || true)
        fi
      fi
    else
      base="$BEFORE"
    fi
    ;;
esac

if [ "$EVENT" != "workflow_dispatch" ]; then
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
      if [ "$LOCAL" = true ]; then
        # Also take uncommitted (staged, unstaged, untracked) changes into account
        git diff --name-only HEAD >> "$changed_tmp" 2>/dev/null || true
        git diff --cached --name-only HEAD >> "$changed_tmp" 2>/dev/null || true
        git ls-files --others --exclude-standard >> "$changed_tmp" 2>/dev/null || true
      fi
      changed_files=$(sort -u "$changed_tmp")

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
fi

build_json="[${build_json%,}]"
if [ "$build_json" = "[]" ]; then
  echo "[]"
else
  if [ -t 1 ]; then echo "${c_dim}[${c_reset}"; else echo "["; fi
  if [ -n "$color_sed" ]; then
    echo "$build_json" | jq -c '.[]' | sed 's/^/  /' | sed '$!s/$/,/' | sed "$color_sed"
  else
    echo "$build_json" | jq -c '.[]' | sed 's/^/  /' | sed '$!s/$/,/'
  fi
  if [ -t 1 ]; then echo "${c_dim}]${c_reset}"; else echo "]"; fi
fi
