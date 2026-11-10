#!/usr/bin/env bash
# Runs the macOS subset of the smoke suite in a throwaway home, so a run can
# never write into the owner's real ~/Library, and fails when the run leaves
# anything behind.
#
# Usage: tools/macos/run-macos-subset.sh [--repeat N] [--allow <relative path>]... [--empty-home]
#
# Build first (the runner never builds):
#   dotnet build src/AsteriaScale.RankerWorker.MacOS/AsteriaScale.RankerWorker.MacOS.csproj -c Release
#   dotnet build tests/AsteriaScale.Tests.MacOS/AsteriaScale.Tests.MacOS.csproj -c Release
#
# Each run gets a fresh work folder W from mktemp -d (outside the clone) and
# starts the built apphost with cwd W/cwd, TMPDIR=W/tmp and both HOME and
# CFFIXED_USER_HOME set to W/home. .NET's LocalApplicationData on macOS
# ignores HOME: it follows CFFIXED_USER_HOME (NSSearchPath) and, when that is
# unset, the account's real home; only UserProfile and POSIX tools read HOME.
# Both are set, because HOME alone would let .NET reach the real ~/Library.
# W/home has an empty
# Library/Application Support, as on a real account; with --empty-home it has
# no Library at all, which is what a code path that assumes the folder exists
# must survive.
#
# After each run every entry in W/cwd, W/home (except the two pre-created
# empty folders) and W/tmp is listed. An entry is accepted only if --allow
# names it, a folder above it, or a folder that holds it; paths are relative
# to W, for example 'home/Library/Application Support/AsteriaScale/hover-translation.log'.
# The first run that fails or leaves anything else stops the loop, and its
# work folder is kept for inspection; a clean run's folder is deleted.
#
# The owner's crash reports in ~/Library/Logs/DiagnosticReports are listed
# before and after all runs with tools/macos/crash-reports.py (G-crash): a
# report named after AsteriaScale, dotnet, Python, python3 or swift, or whose
# crashed process, parent or killer has such a name, that was not there
# before fails the runner. That folder is only read, never changed.
# ReportCrash writes there for the account whatever HOME the crashed process
# had, so the folder comes from the user database, not from $HOME (a caller
# may already have pointed HOME at a sandbox).
#
# Exit codes:
#   0   every run passed, left nothing outside --allow, and no crash report appeared
#   2   usage error, no built apphost, not Apple Silicon macOS, or the work
#       folder would lie inside the clone
#   9   a passing run left an entry outside --allow
#   10  a new crash report of the runs' processes appeared
#   any other code is the subset's own (it exits 1 on FAIL)
set -euo pipefail

usage() {
    echo "usage: $0 [--repeat N] [--allow <relative path>]... [--empty-home]" >&2
    exit 2
}

repeat=1
empty_home=0
allows=()
while [[ $# -gt 0 ]]; do
    case $1 in
        --repeat)
            [[ $# -ge 2 && $2 =~ ^[1-9][0-9]{0,2}$ ]] || usage
            repeat=$2
            shift 2
            ;;
        --allow)
            [[ $# -ge 2 ]] || usage
            allow=${2%/}
            # Only something below one of the three watched folders: allowing
            # a whole folder (or escaping W) would silence the check itself.
            case /$allow/ in
                */../* | */./* | *//*) usage ;;
            esac
            [[ $allow =~ ^(cwd|home|tmp)/. ]] || usage
            allows+=("$allow")
            shift 2
            ;;
        --empty-home)
            empty_home=1
            shift
            ;;
        *)
            usage
            ;;
    esac
done

if [[ $(uname -s) != Darwin || $(uname -m) != arm64 ]]; then
    echo "run-macos-subset: the macOS subset runs only on Apple Silicon macOS" >&2
    exit 2
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
repo=$(git -C "$script_dir" rev-parse --show-toplevel)
repo=$(cd "$repo" && pwd -P)
apphost="$repo/tests/AsteriaScale.Tests.MacOS/bin/Release/net10.0/osx-arm64/AsteriaScale.Tests"
if [[ ! -x $apphost ]]; then
    echo "run-macos-subset: $apphost is missing; build the worker and" \
        "tests/AsteriaScale.Tests.MacOS with -c Release first" >&2
    exit 2
fi

user=$(id -un)
real_home=$(python3 -I -c 'import os, pwd; print(pwd.getpwuid(os.getuid()).pw_dir)') || {
    echo "run-macos-subset: cannot read the home folder of $user from the user database" >&2
    exit 2
}
reports="$real_home/Library/Logs/DiagnosticReports"

# Prints the attributed report names, one per line, sorted. A missing folder
# (an account that never crashed anything) is empty; an unreadable one is an
# error, never an empty listing.
crash_reports() {
    local listing
    if ! listing=$(python3 -I "$script_dir/crash-reports.py" "$reports"); then
        echo "run-macos-subset: cannot list $reports" >&2
        exit 2
    fi
    LC_ALL=C sort <<<"$listing"
}

count_lines() {
    if [[ -z $1 ]]; then
        echo 0
    else
        wc -l <<<"$1" | tr -d ' '
    fi
}

is_allowed() {
    local entry=$1 allow
    for allow in ${allows[@]+"${allows[@]}"}; do
        if [[ $entry == "$allow" || $entry == "$allow"/* ]]; then
            return 0
        fi
        # A folder on the way to an allowed path has to exist for the path
        # to exist; anything else inside it is still listed on its own.
        if [[ $allow == "$entry"/* && -d $work/$entry && ! -L $work/$entry ]]; then
            return 0
        fi
    done
    return 1
}

crashes_before=$(crash_reports)
echo "run-macos-subset: $apphost"
echo "run-macos-subset: crash reports of the checks' processes in $reports: $(count_lines "$crashes_before")"

status=0
passed=0
for ((run = 1; run <= repeat; run++)); do
    work=$(mktemp -d "${TMPDIR:-/tmp}/asteria-subset.XXXXXX")
    work=$(cd "$work" && pwd -P)
    case "$work/" in
        "$repo/"*)
            echo "run-macos-subset: refusing a work folder inside the repository ($work); set TMPDIR elsewhere" >&2
            rmdir "$work"
            exit 2
            ;;
    esac
    mkdir "$work/cwd" "$work/home" "$work/tmp"
    precreated=()
    if [[ $empty_home -eq 0 ]]; then
        mkdir -p "$work/home/Library/Application Support"
        precreated=("home/Library" "home/Library/Application Support")
    fi

    echo "run-macos-subset: run $run/$repeat in $work${allows[0]+ (allowed: ${allows[*]})}"
    # stdin is /dev/null: no test reads it, and a child that did must not
    # wait on the caller's terminal.
    if (cd "$work/cwd" && exec env CFFIXED_USER_HOME="$work/home" HOME="$work/home" TMPDIR="$work/tmp" \
        "$apphost" </dev/null); then
        run_status=0
    else
        run_status=$?
    fi

    strays=()
    entries=()
    while IFS= read -r -d '' path; do
        entry=${path#"$work/"}
        skip=0
        for folder in ${precreated[@]+"${precreated[@]}"}; do
            if [[ $entry == "$folder" && -d $path && ! -L $path ]]; then
                skip=1
            fi
        done
        [[ $skip -eq 1 ]] && continue
        if is_allowed "$entry"; then
            entries+=("allowed: $entry")
        else
            entries+=("NOT ALLOWED: $entry")
            strays+=("$entry")
        fi
    done < <(find "$work/cwd" "$work/home" "$work/tmp" -mindepth 1 -print0 | LC_ALL=C sort -z)

    echo "run-macos-subset: run $run/$repeat exit $run_status; entries in cwd, home and tmp: ${#entries[@]}"
    for line in ${entries[@]+"${entries[@]}"}; do
        echo "  $line"
    done

    if [[ $run_status -ne 0 ]]; then
        echo "run-macos-subset: run $run/$repeat failed (exit $run_status); work folder kept at $work" >&2
        status=$run_status
        break
    fi
    if [[ ${#strays[@]} -gt 0 ]]; then
        echo "run-macos-subset: run $run/$repeat left ${#strays[@]} entries outside --allow;" \
            "work folder kept at $work" >&2
        status=9
        break
    fi
    rm -rf "$work"
    passed=$((passed + 1))
done

# ReportCrash writes a report a moment after the process dies; without this
# pause a crash in the last run would be blamed on the next check instead.
sleep 2
crashes_after=$(crash_reports)
crashes_new=$(LC_ALL=C comm -13 <(printf '%s\n' "$crashes_before") <(printf '%s\n' "$crashes_after") | sed '/^$/d')
echo "run-macos-subset: crash reports of the checks' processes: $(count_lines "$crashes_before") before," \
    "$(count_lines "$crashes_after") after, $(count_lines "$crashes_new") new"
if [[ -n $crashes_new ]]; then
    echo "run-macos-subset: new crash reports (left in place for the owner):" >&2
    sed 's/^/  /' <<<"$crashes_new" >&2
    [[ $status -eq 0 ]] && status=10
fi

if [[ $status -eq 0 ]]; then
    echo "run-macos-subset: $passed/$repeat runs passed; nothing left outside --allow; no new crash report"
fi
exit "$status"
