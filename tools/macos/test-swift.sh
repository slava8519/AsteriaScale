#!/usr/bin/env bash
# Builds and tests the Swift package macos/AsteriaScale (AsteriaScaleKit) with
# the Command Line Tools only, in a throwaway home, and fails on anything the
# run leaves behind.
#
# Usage: tools/macos/test-swift.sh [--keep] [-- <extra swift test arguments>]
#
# Steps:
#  1. dotnet build src/AsteriaScale.CoreHost -c Release: the suite launches
#     that CoreHost to core.hello and core.init. Its informational version
#     (asked from MSBuild's own GetAssemblyAttributes target, so it is
#     exactly what core.hello reports) goes to the suite as
#     ASTERIA_COREHOST_BUILD, the build the supervisor must accept. This
#     build runs like the documented CoreHost build, with the real home:
#     NuGet's ~/.nuget cache is the existing tool-cache exception.
#  2. swift build -c release of the package, then swift build --build-tests,
#     then swift test --skip-build. Any "warning:" line in the two builds
#     fails the run. SwiftPM's cache, configuration and security state go to
#     macos/AsteriaScale/.build/swiftpm-state (--cache-path, --config-path,
#     --security-path, --manifest-cache local).
#  3. Every swift process runs with HOME and CFFIXED_USER_HOME at a fresh
#     home with an empty Library/Application Support, its own TMPDIR and an
#     empty cwd, inside a work folder W from mktemp -d (outside the clone).
#     The state flags alone are not enough: SwiftPM 6.3 still creates
#     ~/.swiftpm/{cache,configuration,security} links, ~/Library/org.swift.swiftpm
#     and ~/Library/Caches/org.swift.swiftpm in whatever home it is given, and
#     lock files plus TemporaryDirectory.* folders in TMPDIR. The builds use
#     W/build, the test run W/test; the suite's own temporary folders go to
#     ASTERIA_TEST_TMPDIR=W/test/scratch. Afterwards W/test/scratch and
#     W/test/cwd must be empty, and W/test/home and W/test/tmp may hold only
#     those SwiftPM entries.
#  4. swift test --skip-build runs only when the test bundle exists: without
#     it swiftpm-testing-helper stops with a fatal error (SIGTRAP), which
#     writes a crash report into the owner's DiagnosticReports.
#
# With the Command Line Tools, plain `swift test` fails with "no such module
# 'Testing'": the CLT keep Testing.framework and lib_TestingInterop.dylib
# outside the default search paths, so this script adds -F and -rpath for
# them (measured with CLT for Swift 6.3.3). XCTest does not exist in the CLT
# at all, so the suite is Swift Testing only.
#
# The owner's crash reports (~/Library/Logs/DiagnosticReports, names matching
# ^(AsteriaScale|dotnet|Python|python3|swift); only listed) are counted
# before and after, as in run-macos-subset.sh.
#
# Exit codes:
#   0   build and suite passed, nothing left behind
#   2   usage error, not Apple Silicon macOS, no Testing.framework, or the
#       work folder would lie inside the clone
#   3   the CoreHost build or its version query failed
#   4   a Swift build failed or printed a warning
#   9   the run left an entry in W/test outside the rules above
#   10  the crash-report count went up
#   11  a process the suite started was still running afterwards (killed)
#   12  SwiftPM state appeared under ~/Library or ~/.swiftpm
#   any other code is swift test's own
set -euo pipefail

usage() {
    echo "usage: $0 [--keep] [-- <extra swift test arguments>]" >&2
    exit 2
}

keep=0
extra_test_args=()
while [[ $# -gt 0 ]]; do
    case $1 in
        --keep) keep=1; shift ;;
        --) shift; extra_test_args=("$@"); break ;;
        *) usage ;;
    esac
done

if [[ $(uname -s) != Darwin || $(uname -m) != arm64 ]]; then
    echo "test-swift: the Swift package builds only on Apple Silicon macOS" >&2
    exit 2
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
repo=$(git -C "$script_dir" rev-parse --show-toplevel)
repo=$(cd "$repo" && pwd -P)
pkg="$repo/macos/AsteriaScale"
corehost_project="$repo/src/AsteriaScale.CoreHost/AsteriaScale.CoreHost.csproj"
state="$pkg/.build/swiftpm-state"

real_home=$(python3 -I -c 'import os, pwd; print(pwd.getpwuid(os.getuid()).pw_dir)')
reports="$real_home/Library/Logs/DiagnosticReports"
crash_pattern='^(AsteriaScale|dotnet|Python|python3|swift)'

crash_reports() {
    if [[ ! -e $reports ]]; then
        return 0
    fi
    local listing
    if ! listing=$(ls "$reports"); then
        echo "test-swift: cannot list $reports" >&2
        exit 2
    fi
    grep -E "$crash_pattern" <<<"$listing" || true
}

count_lines() {
    if [[ -z $1 ]]; then echo 0; else wc -l <<<"$1" | tr -d ' '; fi
}

clt_flags=()
developer=$(xcode-select -p)
if [[ $developer == */CommandLineTools ]]; then
    frameworks="$developer/Library/Developer/Frameworks"
    interop="$developer/Library/Developer/usr/lib"
    if [[ ! -d $frameworks/Testing.framework ]]; then
        echo "test-swift: $frameworks/Testing.framework not found; update the Command Line Tools" >&2
        exit 2
    fi
    clt_flags=(-Xswiftc -F -Xswiftc "$frameworks" -Xlinker -F -Xlinker "$frameworks"
        -Xlinker -rpath -Xlinker "$frameworks" -Xlinker -rpath -Xlinker "$interop")
fi
state_flags=(--package-path "$pkg" --cache-path "$state/cache" --config-path "$state/config"
    --security-path "$state/security" --manifest-cache local)

crashes_before=$(crash_reports)
echo "test-swift: crash reports matching $crash_pattern in $reports: $(count_lines "$crashes_before")"

# SwiftPM's state in the real home is compared by modification time.
swiftpm_marker=$(mktemp "${TMPDIR:-/tmp}/asteria-swiftpm-marker.XXXXXX")
swiftpm_homes=("$real_home/Library/Caches/org.swift.swiftpm" "$real_home/Library/org.swift.swiftpm" "$real_home/.swiftpm")

echo "test-swift: dotnet build $corehost_project -c Release"
# --disable-build-servers: no MSBuild node or VBCSCompiler outlives the run.
if ! dotnet build "$corehost_project" -c Release -nologo -v quiet --disable-build-servers; then
    echo "test-swift: the CoreHost build failed" >&2
    rm -f "$swiftpm_marker"
    exit 3
fi
if ! corehost_build=$(dotnet msbuild "$corehost_project" -nologo -nodeReuse:false -p:Configuration=Release \
    -t:GetAssemblyAttributes -getProperty:InformationalVersion) || [[ -z $corehost_build ]]; then
    echo "test-swift: cannot read the CoreHost informational version" >&2
    rm -f "$swiftpm_marker"
    exit 3
fi
echo "test-swift: CoreHost build $corehost_build"

work=$(mktemp -d "${TMPDIR:-/tmp}/asteria-swift.XXXXXX")
work=$(cd "$work" && pwd -P)
case "$work/" in
    "$repo/"*)
        echo "test-swift: refusing a work folder inside the repository ($work); set TMPDIR elsewhere" >&2
        rmdir "$work"
        rm -f "$swiftpm_marker"
        exit 2
        ;;
esac
for phase in build test; do
    mkdir -p "$work/$phase/cwd" "$work/$phase/home/Library/Application Support" "$work/$phase/tmp"
done
mkdir "$work/test/scratch"
echo "test-swift: work folder $work"

isolated() {
    local phase=$1
    shift
    (cd "$work/$phase/cwd" && exec env CFFIXED_USER_HOME="$work/$phase/home" HOME="$work/$phase/home" \
        TMPDIR="$work/$phase/tmp/" ASTERIA_TEST_TMPDIR="$work/test/scratch" \
        ASTERIA_COREHOST_BUILD="$corehost_build" "$@" </dev/null)
}

status=0
build_log="$work/build.log"
for build in release tests; do
    if [[ $build == release ]]; then
        args=(swift build -c release "${state_flags[@]}")
    else
        args=(swift build --build-tests "${state_flags[@]}" "${clt_flags[@]}")
    fi
    echo "test-swift: ${args[*]:0:4}"
    if ! isolated build "${args[@]}" >"$build_log" 2>&1; then
        cat "$build_log"
        echo "test-swift: swift build ($build) failed" >&2
        status=4
        break
    fi
    if grep -n 'warning:' "$build_log" >&2; then
        echo "test-swift: swift build ($build) printed warnings" >&2
        status=4
        break
    fi
    tail -n 1 "$build_log"
done
rm -f "$build_log"

test_log="$work/test.log"
if [[ $status -eq 0 ]]; then
    bin=$(isolated build swift build --show-bin-path --build-tests "${state_flags[@]}" "${clt_flags[@]}")
    bundle="$bin/AsteriaScalePackageTests.xctest/Contents/MacOS/AsteriaScalePackageTests"
    if [[ ! -x $bundle ]]; then
        echo "test-swift: no test bundle at $bundle; not starting swift test" >&2
        status=4
    fi
fi
if [[ $status -eq 0 ]]; then
    echo "test-swift: swift test --skip-build"
    if isolated test swift test --skip-build "${state_flags[@]}" ${extra_test_args[@]+"${extra_test_args[@]}"} \
        >"$test_log" 2>&1; then
        test_status=0
    else
        test_status=$?
    fi
    cat "$test_log"
    if [[ $test_status -ne 0 ]]; then
        echo "test-swift: swift test failed (exit $test_status)" >&2
        status=$test_status
    elif ! grep -Eq 'Test run with [0-9]+ tests? in [0-9]+ suites? passed' "$test_log"; then
        echo "test-swift: swift test printed no passing Swift Testing summary" >&2
        status=1
    fi
fi
rm -f "$test_log"

# Every process the suite starts carries the fake's script name, the work
# folder or the marker duration 3141.59 of its /bin/sleep children.
sleep 0.5
leftovers=$(pgrep -f "fake-corehost\\.py|sleep 3141\\.59|$work" || true)
if [[ -n $leftovers ]]; then
    echo "test-swift: processes the suite started are still running; killing them:" >&2
    ps -o pid=,pgid=,command= -p "$(paste -sd, - <<<"$leftovers")" >&2 || true
    xargs kill -KILL <<<"$leftovers" 2>/dev/null || true
    [[ $status -eq 0 ]] && status=11
fi

strays=()
while IFS= read -r -d '' path; do
    entry=${path#"$work/test/"}
    case $entry in
        home/Library | "home/Library/Application Support") [[ -d $path && ! -L $path ]] && continue ;;
        # SwiftPM's own state in the home it was given (see step 3).
        home/.swiftpm | home/.swiftpm/* | home/Library/org.swift.swiftpm | home/Library/org.swift.swiftpm/* | \
            home/Library/Caches | home/Library/Caches/org.swift.swiftpm | home/Library/Caches/org.swift.swiftpm/*) continue ;;
        tmp/_*.lock) [[ -f $path ]] && continue ;;
        tmp/TemporaryDirectory.* | tmp/TemporaryDirectory.*/.keep-directory) continue ;;
    esac
    strays+=("$entry")
done < <(find "$work/test/cwd" "$work/test/home" "$work/test/tmp" "$work/test/scratch" -mindepth 1 -print0 |
    LC_ALL=C sort -z)
if [[ ${#strays[@]} -gt 0 ]]; then
    echo "test-swift: the run left ${#strays[@]} entries in $work/test:" >&2
    printf '  %s\n' "${strays[@]}" >&2
    [[ $status -eq 0 ]] && status=9
fi

new_state=()
for folder in "${swiftpm_homes[@]}"; do
    if [[ -e $folder ]]; then
        while IFS= read -r path; do new_state+=("$path"); done < <(find "$folder" -newer "$swiftpm_marker" 2>/dev/null)
    fi
done
rm -f "$swiftpm_marker"
if [[ ${#new_state[@]} -gt 0 ]]; then
    echo "test-swift: SwiftPM wrote into the real home:" >&2
    printf '  %s\n' "${new_state[@]}" >&2
    [[ $status -eq 0 ]] && status=12
fi

# ReportCrash writes a report a moment after the process dies.
sleep 2
crashes_after=$(crash_reports)
before_count=$(count_lines "$crashes_before")
after_count=$(count_lines "$crashes_after")
echo "test-swift: crash reports matching $crash_pattern: $before_count before, $after_count after"
if [[ $after_count -gt $before_count ]]; then
    echo "test-swift: new crash reports (left in place for the owner):" >&2
    comm -13 <(LC_ALL=C sort <<<"$crashes_before") <(LC_ALL=C sort <<<"$crashes_after") | sed 's/^/  /' >&2
    [[ $status -eq 0 ]] && status=10
fi

if [[ $status -eq 0 && $keep -eq 0 ]]; then
    rm -rf "$work"
    echo "test-swift: passed; nothing left behind; no new crash report"
elif [[ $status -ne 0 ]]; then
    echo "test-swift: work folder kept at $work" >&2
fi
exit "$status"
