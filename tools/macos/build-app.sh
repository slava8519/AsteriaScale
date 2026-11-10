#!/usr/bin/env bash
# Builds, assembles, signs and verifies "Asteria Scale.app" with the Command
# Line Tools only (no Xcode), and runs it in a throwaway home on request.
# Roadmap 20.2; docs/DEVELOPMENT.md, «macOS (Apple Silicon)».
#
# Usage:
#   tools/macos/build-app.sh [--output DIR] [--sign IDENTITY] [--no-build]
#                            [--self-test | --run-isolated [--home DIR]]
#                            [--ranker-models DIR] [--keep]
#
#   --output DIR        where the .app goes (default artifacts/macos, git-ignored);
#                       its .work folder holds the build's intermediate files;
#                       never inside a File Provider domain, no "." or ".." in
#                       the part that does not exist yet, no file or dangling
#                       link on the way (exit 2, see below)
#   --sign IDENTITY     codesign identity; default "-" (ad-hoc) or
#                       $ASTERIA_SIGN_IDENTITY. Ad-hoc is enough for 20.2; a
#                       stable identity is chosen before 20.3 (docs/MACOS.md T15)
#   --no-build          reuse the .app already in --output (it is verified again)
#   --self-test         run the app's scripted self-test (AsteriaScale --self-test)
#   --run-isolated      run the app until it quits (^C or kill -TERM quit it in
#                       order); prints the throwaway folder and keeps it
#   --home DIR          with --run-isolated: reuse a folder an earlier
#                       --run-isolated printed (it carries a marker file)
#   --ranker-models DIR the worker's model folder for --self-test and
#                       --run-isolated, read in place (normally
#                       src/AsteriaScale.RankerWorker/Models); without it the
#                       app uses the throwaway home's empty Models/Ranker
#   --keep              keep the self-test's throwaway folder after a pass
#
# The self-test and the isolated run start the bundle's executable directly
# (never open(1), never the real home): HOME and CFFIXED_USER_HOME point at
# W/home with an empty Library/Application Support, TMPDIR at W/tmp and the
# working directory is the empty W/cwd, where W comes from mktemp -d outside
# the clone. Launching the app in the real home (open, ~/Applications) is the
# owner's alone.
#
# Steps:
#  1. swift build -c release of macos/AsteriaScale, isolated like
#     tools/macos/test-swift.sh (own HOME/CFFIXED_USER_HOME, TMPDIR and cwd,
#     SwiftPM state under .build/swiftpm-state); any "warning:" fails.
#  2. dotnet publish of src/AsteriaScale.CoreHost and
#     src/AsteriaScale.RankerWorker.MacOS: osx-arm64, self-contained,
#     single-file, ReadyToRun (docs/MACOS.md T11), with --artifacts-path in
#     the .work folder, so the clone's bin/ and obj/ stay as they are. Each
#     runs at -v minimal, where MSBuild prints every warning; a warning fails
#     the build as in step 1. The CoreHost informational version (MSBuild's
#     GetAssemblyAttributes, exactly what core.hello reports) is stamped into
#     Info.plist as AsteriaCoreHostBuild; the app accepts no other CoreHost
#     build.
#  3. Assembly (Apple's "Placing content in a bundle"):
#       Contents/MacOS/AsteriaScale                       the Swift app
#       Contents/Helpers/AsteriaScale.CoreHost            CoreHost
#       Contents/Helpers/Ranker/AsteriaScale.RankerWorker + libonnxruntime.dylib
#                                                         + libhf_tokenizers.dylib
#       Contents/Resources/Data/{Dictionaries,Morphology} CoreHost's data root
#       Contents/Resources/Legal/                         licences and notices
#     Models are never bundled (licence M7, rule 8, size): any *.onnx,
#     *.onnx.data, *.gguf, *.safetensors, *.vec, "consec" or Models/ path, or a
#     file over 120 MB, stops the build with exit 3.
#  4. Signing inside out with the hardened runtime, never --deep: the two
#     dylibs, the worker (allow-jit, disable-library-validation; identifier
#     <bundle id>.rankerworker), CoreHost (allow-jit; <bundle id>.corehost),
#     then the app (no entitlements). The bundle id is
#     io.github.slava8519.AsteriaScale (docs/MACOS.md M13).
#  5. Verification: codesign --verify --strict --deep (prints "valid on disk"
#     and "satisfies its Designated Requirement"), then each binary's
#     entitlements, runtime flag and identifier.
#  6. With --self-test or --run-isolated: the run above, then no process of
#     the bundle may be left (exit 11; killed when this run started it,
#     that is the app, a process naming W in its arguments, under the
#     physical /private/tmp/… or the /tmp/… the app passes on, and the
#     members and children of those; only listed otherwise), W/cwd must be
#     empty (exit 9), nothing named
#     after the app may be newer than the run in the real ~/Library
#     (exit 12).
# The File Provider guard runs after the argument, platform, tool,
# --ranker-models and TMPDIR checks (all read only) and before the output
# folder is created, in every mode including --no-build. File Provider tags each new
# folder named *.app in its domains with com.apple.FinderInfo and
# com.apple.fileprovider.fpfs#P within about a second, even under a .nosync
# name (that name stops the upload, not the tags), and puts FinderInfo back
# at once after xattr -d, so codesign rejects the bundle in step 4
# ("resource fork, Finder information, or similar detritus not allowed")
# after a full build. The guard only reads extended attributes (with
# /usr/bin/xattr, never another xattr on PATH): it resolves the output's
# nearest existing folder physically (pwd -P) and exits 2 when that folder
# or any folder above it is a domain root (com.apple.file-provider-domain-id:
# measured on ~/Desktop and ~/Documents under iCloud Drive's Desktop and
# Documents and on ~/Library/Mobile Documents; expected, not measured, on
# the folders in ~/Library/CloudStorage, which is empty on the Mac this was
# written on), when the part of --output that does not exist yet has a "."
# or ".." component (mkdir -p resolves "new/.." only after creating "new",
# so the text alone cannot say where the bundle would go), when a part of
# --output exists but is not a folder (a file, or a symbolic link to nothing
# or to a file: mkdir -p would stop there with exit 1), or, failing closed,
# when it cannot resolve that folder or list a folder's attributes.
# A File Provider tag (com.apple.fileprovider.*, dir#N on a tracked folder)
# on the nearest existing folder with no domain root found above it only
# prints a warning: a rename keeps the tag after a move out of a domain, and
# outside a domain it is inert (measured on 2026-11-10 in a moved clone: a
# new .app folder and a signed bundle copied with ditto under tagged folders
# got no FinderInfo or File Provider tag and passed codesign --strict). If
# codesign still reports such detritus, the script names the extended
# attributes it finds on the bundle and marks the likely culprits (exit 5).
# Every exit 2 comes before the output folder is created, so nothing is
# built, created, replaced or killed then: besides usage, platform and tool
# errors and the guard, a TMPDIR inside the clone, a --home folder that
# --run-isolated did not make (or one in the clone or the real home),
# --no-build without a bundle, a crash-report folder that cannot be listed
# and any process running from the bundle's Contents (an owner's copy
# started with open(1), say: it would otherwise lose its bundle to step 1
# and its CoreHost to step 6).
# The owner's crash reports (~/Library/Logs/DiagnosticReports; only read) are
# listed before and after with tools/macos/crash-reports.py (G-crash: named
# after AsteriaScale, dotnet, Python, python3 or swift, or with such a
# crashed process, parent or killer); a new one fails the script (exit 10)
# and stays in place, and a listing that fails after the build fails it too
# (exit 10).
#
# Exit codes:
#   0   built and verified (and the self-test passed)
#   2   usage error, not Apple Silicon macOS, a missing tool, an --output
#       inside a File Provider domain, with "." or ".." in its missing part
#       or with a file or a dangling link on its way (or one the guard
#       cannot resolve or read), a TMPDIR inside the
#       clone, a bad --home, --no-build without a bundle, an unreadable
#       crash-report folder, or a process running from the bundle; always
#       before the output folder is created
#   3   a model file or an oversized file in the bundle
#   4   a build failed or printed a warning
#   5   signing or verification failed
#   6   the self-test failed (or did not end within 150 s)
#   9   the run left an entry in its working directory
#   10  a new crash report of the build's or the app's processes appeared,
#       or the crash reports could not be listed after the build
#   11  a process of the bundle was still running afterwards (killed when
#       this run started it, listed and left running otherwise)
#   12  something new in the real ~/Library is named after the app or SwiftPM
set -euo pipefail

bundle_id=io.github.slava8519.AsteriaScale
usage() {
    sed -n '/^# Usage:/,/^#   --keep/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
    exit 2
}

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
repo=$(git -C "$script_dir" rev-parse --show-toplevel)
repo=$(cd "$repo" && pwd -P)
out="$repo/artifacts/macos"
identity="${ASTERIA_SIGN_IDENTITY:--}"
build=1
mode=none
keep=0
reuse_home=""
ranker_models=""
while [[ $# -gt 0 ]]; do
    case $1 in
        --output) [[ $# -ge 2 && -n $2 ]] || usage; out=$2; shift 2 ;;
        --sign) [[ $# -ge 2 ]] || usage; identity=$2; shift 2 ;;
        --no-build) build=0; shift ;;
        --self-test) [[ $mode == none ]] || usage; mode=self-test; shift ;;
        --run-isolated) [[ $mode == none ]] || usage; mode=run; shift ;;
        --home) [[ $# -ge 2 ]] || usage; reuse_home=$2; shift 2 ;;
        --ranker-models) [[ $# -ge 2 ]] || usage; ranker_models=$2; shift 2 ;;
        --keep) keep=1; shift ;;
        *) usage ;;
    esac
done
if [[ -n $reuse_home && $mode != run ]] || [[ -n $ranker_models && $mode == none ]]; then
    usage
fi

if [[ $(uname -s) != Darwin || $(uname -m) != arm64 ]]; then
    echo "build-app: Apple Silicon macOS only" >&2
    exit 2
fi
# The Command Line Tools whenever they are installed, whatever xcode-select
# says (docs/MACOS.md T33): Xcode.app may be on this Mac only for the
# owner's signing certificate (M16). The swift shim honors DEVELOPER_DIR.
if [[ -d /Library/Developer/CommandLineTools ]]; then
    export DEVELOPER_DIR=/Library/Developer/CommandLineTools
fi
for tool in swift dotnet codesign plutil ditto python3; do
    command -v "$tool" >/dev/null || { echo "build-app: $tool not found" >&2; exit 2; }
done
# The system xattr by its path: a pip "xattr" earlier on PATH (in a venv,
# say) has other options and output, and the guard below fails closed on it.
[[ -x /usr/bin/xattr ]] || { echo "build-app: /usr/bin/xattr not found" >&2; exit 2; }
if [[ -n $ranker_models ]]; then
    [[ -d $ranker_models ]] || { echo "build-app: no folder $ranker_models" >&2; exit 2; }
    ranker_models=$(cd "$ranker_models" && pwd -P)
fi
count_lines() { if [[ -z $1 ]]; then echo 0; else wc -l <<<"$1" | tr -d ' '; fi; }
step() { printf '\n== %s\n' "$*"; }
fail() {
    local code=$1
    shift
    echo "build-app: $*" >&2
    exit "$code"
}
# First, so that not even bash's here-strings below write into the clone.
temp_root=$(cd "${TMPDIR:-/tmp}" && pwd -P)
case "$temp_root/" in
    "$repo/"*) fail 2 "TMPDIR lies inside the repository; set it elsewhere" ;;
esac
real_home=$(python3 -I -c 'import os, pwd; print(pwd.getpwuid(os.getuid()).pw_dir)')

# The File Provider guard (header): read only, no probe folder and no wait,
# so a refused run creates nothing, not even the output folder. The walk
# starts at the nearest existing folder and resolves it physically (cd -P,
# pwd -P), as mkdir would: a symbolic link such as the old
# ~/Documents/Claude/AsteriaScale, a relative --output or ".." in the
# existing part then lead to the folder the bundle would really be written
# into. The part that does not exist yet stays text, so it may hold no "."
# or "..": mkdir -p resolves "new/.." only after it has created "new", and
# for "new/../fp/app" the walk would start above fp and never read fp's
# attributes. Creating the folders first to look would break "a refused run
# creates nothing", possibly inside a domain.
out_arg=$out
fp_refuse() {
    local folder=$1 marker=$2 detail=$3 remedy=$4
    echo "build-app: refusing --output $out_arg: $folder $detail ($marker)." >&2
    echo "build-app: File Provider tags every new .app folder in its domains with com.apple.FinderInfo," \
        "even under a .nosync name, and puts it back at once after xattr -d; codesign rejects such a bundle" \
        "(\"resource fork, Finder information, or similar detritus not allowed\") after a full build." >&2
    echo "build-app: $remedy (docs/DEVELOPMENT.md, «Клон и iCloud Drive»)." \
        "Nothing was built, created, replaced or killed." >&2
    exit 2
}
fp_probe=$out_arg
[[ $fp_probe == /* ]] || fp_probe="$(pwd -P)/$fp_probe"
# A trailing slash makes -L and -e look through a link: "link/" to nothing
# is neither, so the walk would take the link for a missing folder. dirname
# never returns one, so only the start needs this.
while [[ $fp_probe == */ && $fp_probe != / ]]; do fp_probe=${fp_probe%/}; done
fp_rest=""
fp_dots=0
while [[ ! -d $fp_probe ]]; do
    # -d follows links, so a link to a folder ends the walk and cd -P below
    # resolves it. What exists here and is not a folder would stop mkdir -p
    # with exit 1 and no message of this script, so it is refused instead.
    if [[ -L $fp_probe || -e $fp_probe ]]; then
        echo "build-app: refusing --output $out_arg: $fp_probe exists but is not a folder (a file, or a" \
            "symbolic link to nothing or to a file), so mkdir -p could not create the output there." \
            "Nothing was built, created, replaced or killed." >&2
        exit 2
    fi
    fp_part=$(basename "$fp_probe")
    if [[ $fp_part == . || $fp_part == .. ]]; then fp_dots=1; fi
    fp_rest=$fp_part${fp_rest:+/$fp_rest}
    fp_probe=$(dirname "$fp_probe")
done
fp_nearest=$(cd -P "$fp_probe" 2>/dev/null && pwd -P) ||
    { echo "build-app: cannot resolve $fp_probe, the nearest existing folder of --output $out_arg" >&2; exit 2; }
fp_dir=$fp_nearest
fp_nearest_names=""
while :; do
    fp_names=$(/usr/bin/xattr "$fp_dir" 2>/dev/null) ||
        { echo "build-app: cannot list the extended attributes of $fp_dir (--output $out_arg)" >&2; exit 2; }
    [[ $fp_dir == "$fp_nearest" ]] && fp_nearest_names=$fp_names
    if [[ $'\n'$fp_names$'\n' == *$'\n'com.apple.file-provider-domain-id$'\n'* ]]; then
        # The value is "<provider>/<account's domain UUID>": only the
        # provider is printed.
        fp_domain=$(/usr/bin/xattr -p com.apple.file-provider-domain-id "$fp_dir" 2>/dev/null) ||
            fp_domain=unreadable
        fp_refuse "$fp_dir" "com.apple.file-provider-domain-id, provider ${fp_domain%%/*}" \
            "is the root of a File Provider domain, and the output would lie inside it" \
            "move the clone out of every File Provider domain, or pass --output outside them"
    fi
    [[ $fp_dir == / ]] && break
    fp_dir=$(dirname "$fp_dir")
done
if [[ $fp_dots -eq 1 ]]; then
    echo "build-app: refusing --output $out_arg: its part that does not exist yet ($fp_rest) has a \".\" or" \
        "\"..\" component, which mkdir -p resolves only after creating the folders before it, so the File" \
        "Provider check cannot tell where the bundle would go; pass the path without them." \
        "Nothing was built, created, replaced or killed." >&2
    exit 2
fi
fp_tag=$(sed -n '/^com\.apple\.fileprovider\./{p;q;}' <<<"$fp_nearest_names")
if [[ -n $fp_tag ]]; then
    # No folder above carries the domain marker, so the tag is most likely
    # left over from a move out of a domain: a rename keeps extended
    # attributes (the clone moved from ~/Documents to ~/Developer kept dir#N
    # on 16 folders), and outside a domain nothing renews it (header). The
    # guard sees only a domain that marks its root, which a provider in
    # ~/Library/CloudStorage is expected, not measured, to do: there the
    # warning suggests no removal. Elsewhere it suggests one only for a
    # folder in the clone, never for the owner's other folders, and only
    # for a tag name and a clone path that are safe inside single quotes.
    # The removal acts on the entries find lists and on nothing a link
    # points at: find does not follow symbolic links, and xattr -s acts on
    # a link itself. Without -s, xattr -d follows a link that carries the
    # tag and strips it from the link's target, which may lie outside the
    # clone (the clone has links, among them the absolute ones SwiftPM
    # leaves in .work/swift/home/.swiftpm); xattr -r -d also follows links.
    fp_doc="docs/DEVELOPMENT.md, «Клон и iCloud Drive»"
    fp_why="so it is most likely left over from a move out of a domain, where it is inert"
    fp_hint="see $fp_doc"
    fp_tag_safe='^com\.apple\.fileprovider\.[A-Za-z0-9._#-]+$'
    case "$fp_nearest/" in
        "$real_home/Library/CloudStorage/"*)
            fp_why="but the folder lies in ~/Library/CloudStorage, whose providers may not mark their domain roots"
            fp_hint="do not remove the tag, and if codesign fails, pass --output elsewhere ($fp_doc)" ;;
        "$repo/"*)
            if [[ $fp_tag =~ $fp_tag_safe && $repo != *\'* ]]; then
                fp_hint="to remove such tags from the clone (optional, the owner's): find '$repo' -xattrname"
                fp_hint+=" '$fp_tag' -exec /usr/bin/xattr -s -d '$fp_tag' {} + ($fp_doc)"
            fi ;;
    esac
    echo "build-app: warning: $fp_nearest carries $fp_tag, a File Provider tag; no File Provider domain root" \
        "was found above it, $fp_why; the script goes on; $fp_hint." >&2
fi
# The physical path the guard checked: mkdir -p creates exactly these
# folders, since none of fp_rest exists and none of it is "." or "..".
if [[ -z $fp_rest ]]; then
    out=$fp_nearest
elif [[ $fp_nearest == / ]]; then
    out=/$fp_rest
else
    out=$fp_nearest/$fp_rest
fi
pkg="$repo/macos/AsteriaScale"
state="$pkg/.build/swiftpm-state"
app="$out/Asteria Scale.app"
contents="$app/Contents"
work="$out/.work"

# Every other exit 2 comes before mkdir as well, so that a refused run
# leaves no output folder behind.
if [[ -n $reuse_home ]]; then
    [[ -f $reuse_home/.asteria-isolated ]] || fail 2 "--home: $reuse_home was not made by --run-isolated"
    reuse_home=$(cd "$reuse_home" && pwd -P)
    case "$reuse_home/" in
        "$repo/"* | "$real_home/") fail 2 "refusing the run folder $reuse_home" ;;
    esac
fi

# pgrep -f takes an extended regular expression; the bundle path is matched
# literally.
contents_pattern=$(sed 's/[][\\.^$*+?(){}|]/\\&/g' <<<"$contents/")
bundle_pids() { pgrep -f "$contents_pattern" || true; }
running=$(bundle_pids)
if [[ -n $running ]]; then
    echo "build-app: processes are running from $app; quit that copy first" \
        "(nothing was built, created, replaced or killed):" >&2
    ps -o pid=,command= -p "$(paste -sd, - <<<"$running")" >&2 || true
    exit 2
fi
[[ $build -eq 1 || -d $app ]] || fail 2 "--no-build: no $app"

reports="$real_home/Library/Logs/DiagnosticReports"
# The G-crash listing: reports attributed to the checks' processes, sorted.
# Unreadable before the build it is a refusal (2); after it, the run cannot
# prove that nothing crashed (10).
crash_reports() {
    local code=$1 listing
    listing=$(python3 -I "$script_dir/crash-reports.py" "$reports") ||
        { echo "build-app: cannot list $reports" >&2; exit "$code"; }
    LC_ALL=C sort <<<"$listing"
}
# The fallback of the File Provider guard: a domain the guard cannot see, or
# a bundle that carries the tags anyway (ditto copies extended attributes),
# still ends in codesign's "detritus" or "Disallowed xattr". Name what the
# bundle carries and mark the names codesign rejects (com.apple.macl and
# com.apple.provenance it accepts); the exit code stays 5.
xattr_hint() {
    grep -q -e 'detritus' -e 'Disallowed xattr' <<<"$1" || return 0
    echo "build-app: codesign failed on extended attributes; found under $app (files, name; the likely" \
        "culprits are marked):" >&2
    { /usr/bin/xattr -r "$app" 2>/dev/null || true; } | sed 's/^.*: //' | LC_ALL=C sort | uniq -c |
        sed -E 's/^ */  /; s/ (com\.apple\.(FinderInfo|ResourceFork|fileprovider\..*))$/ \1  <- likely culprit/' >&2 ||
        true
    echo "build-app: File Provider (iCloud Drive's Desktop and Documents, ~/Library/Mobile Documents, the" \
        "folders in ~/Library/CloudStorage) puts com.apple.FinderInfo and com.apple.fileprovider.* on new" \
        ".app folders and renews them after xattr -d; build with --output outside every File Provider domain" \
        "(docs/DEVELOPMENT.md, «Клон и iCloud Drive»)." >&2
}

crashes_before=$(crash_reports 2)
echo "build-app: crash reports of the checks' processes in $reports: $(count_lines "$crashes_before")"
mkdir -p "$out"
marker=$(mktemp "$temp_root/asteria-build-app-marker.XXXXXX")
trap 'rm -f "$marker"' EXIT
# Read only: what in the real home could be named after the app or SwiftPM.
real_library_watch=(
    "$real_home/Library/Application Support/AsteriaScale" "$real_home/Library/Logs/AsteriaScale"
    "$real_home/Library/Caches/org.swift.swiftpm" "$real_home/Library/org.swift.swiftpm" "$real_home/.swiftpm"
)
real_library_named=(
    "$real_home/Library/Preferences" "$real_home/Library/Caches" "$real_home/Library/Saved Application State"
    "$real_home/Library/HTTPStorages" "$real_home/Library/Application Support"
)

if [[ $build -eq 1 ]]; then
    rm -rf "$work" "$app"
    mkdir -p "$work/swift/home/Library/Application Support" "$work/swift/tmp" "$work/swift/cwd"

    step "Swift app (release)"
    swift_isolated() {
        (cd "$work/swift/cwd" && exec env CFFIXED_USER_HOME="$work/swift/home" HOME="$work/swift/home" \
            TMPDIR="$work/swift/tmp/" swift "$@" --package-path "$pkg" --cache-path "$state/cache" \
            --config-path "$state/config" --security-path "$state/security" --manifest-cache local </dev/null)
    }
    if ! swift_isolated build -c release --arch arm64 >"$work/swift-build.log" 2>&1; then
        tail -n 40 "$work/swift-build.log" >&2
        fail 4 "swift build failed"
    fi
    if grep -n 'warning:' "$work/swift-build.log" >&2; then
        fail 4 "swift build printed warnings"
    fi
    tail -n 1 "$work/swift-build.log"
    swift_bin="$(swift_isolated build -c release --arch arm64 --show-bin-path)/AsteriaScale"
    [[ -x $swift_bin ]] || fail 4 "no app binary at $swift_bin"

    step ".NET helpers (osx-arm64, self-contained single-file ReadyToRun)"
    # -v minimal, not quiet: MSBuild prints each warning there, and a warning
    # (single-file analysis and the ReadyToRun compiler run only in a
    # publish) fails the build like a Swift one. The .work folder is new on
    # every build, so nothing is skipped as up to date and every warning is
    # printed again.
    dotnet_flags=(-c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
        --artifacts-path "$work/dotnet" -nologo -v minimal --disable-build-servers)
    publish() {
        local project=$1 dest=$2
        echo "dotnet publish $project"
        if ! dotnet publish "$repo/$project" "${dotnet_flags[@]}" -o "$dest" >"$dest.log" 2>&1; then
            tail -n 40 "$dest.log" >&2
            fail 4 "dotnet publish $project failed"
        fi
        # MSBuild's canonical form: "<origin>: warning CS0168: …", "<project> : warning NU1603: …".
        if grep -nE ':[[:space:]]*warning([[:space:]]+[A-Za-z]+[0-9]+)?[[:space:]]*:' "$dest.log" >&2; then
            fail 4 "dotnet publish $project printed warnings"
        fi
    }
    publish src/AsteriaScale.CoreHost/AsteriaScale.CoreHost.csproj "$work/corehost"
    publish src/AsteriaScale.RankerWorker.MacOS/AsteriaScale.RankerWorker.MacOS.csproj "$work/ranker"
    msbuild_property() {
        dotnet msbuild "$repo/src/AsteriaScale.CoreHost/AsteriaScale.CoreHost.csproj" -nologo -nodeReuse:false \
            -p:Configuration=Release -p:RuntimeIdentifier=osx-arm64 -p:ArtifactsPath="$work/dotnet" \
            -t:GetAssemblyAttributes -getProperty:"$1"
    }
    corehost_build=$(msbuild_property InformationalVersion) || fail 4 "cannot read the CoreHost informational version"
    version=$(msbuild_property Version) || fail 4 "cannot read the CoreHost version"
    [[ -n $corehost_build && -n $version ]] || fail 4 "empty CoreHost version"
    echo "CoreHost build $corehost_build"

    step "Assemble $app"
    mkdir -p "$contents/MacOS" "$contents/Helpers/Ranker" "$contents/Resources/Data" "$contents/Resources/Legal"
    ditto "$swift_bin" "$contents/MacOS/AsteriaScale"
    bundle_version=$(git -C "$repo" rev-list --count HEAD)
    python3 -I - "$pkg/Bundle/Info.plist" "$contents/Info.plist" "$version" "$bundle_version" "$corehost_build" <<'PY'
import sys
source, target, version, build, corehost = sys.argv[1:6]
text = open(source, encoding="utf-8").read()
for key, value in (("@VERSION@", version), ("@BUILD@", build), ("@COREHOST_BUILD@", corehost)):
    if key not in text:
        sys.exit(f"{key} missing from {source}")
    text = text.replace(key, value)
open(target, "w", encoding="utf-8").write(text)
PY
    plutil -lint "$contents/Info.plist" >/dev/null || fail 4 "Info.plist is not a valid property list"
    printf 'APPL????' >"$contents/PkgInfo"
    ditto "$work/corehost/AsteriaScale.CoreHost" "$contents/Helpers/AsteriaScale.CoreHost"
    ditto "$work/corehost/Dictionaries" "$contents/Resources/Data/Dictionaries"
    ditto "$work/corehost/Morphology" "$contents/Resources/Data/Morphology"
    ditto "$work/ranker/AsteriaScale.RankerWorker" "$contents/Helpers/Ranker/AsteriaScale.RankerWorker"
    for lib in libonnxruntime.dylib libhf_tokenizers.dylib; do
        ditto "$work/ranker/$lib" "$contents/Helpers/Ranker/$lib"
    done
    for doc in LICENSE THIRD_PARTY_NOTICES.md DATA-LICENSES.md; do
        ditto "$repo/$doc" "$contents/Resources/Legal/$doc"
    done
fi

step "Guard: no model files, no oversized files"
# ConSeC is CC BY-NC-SA (M7) and every model is pinned and read in place
# (rule 8): a model in the bundle is a build error, not a warning.
models=$(find "$app" \( -iname '*.onnx' -o -iname '*.onnx.data' -o -iname '*.gguf' -o -iname '*.safetensors' \
    -o -iname '*.vec' -o -ipath '*consec*' -o -path '*/Models/*' -o -name Models \) -print)
[[ -z $models ]] || fail 3 "model files must never be bundled:"$'\n'"$models"
large=$(find "$app" -type f -size +120M -print)
[[ -z $large ]] || fail 3 "unexpectedly large file in the bundle:"$'\n'"$large"
symlinks=$(find "$app" -type l -print)
[[ -z $symlinks ]] || fail 3 "symbolic links in the bundle:"$'\n'"$symlinks"
echo "none"

if [[ $build -eq 1 ]]; then
    step "Sign inside out (identity: $identity)"
    entitlements="$pkg/Bundle/Entitlements"
    sign_log="$work/sign.log"
    sign() {
        codesign --force --sign "$identity" --options runtime --timestamp=none "$@" >>"$sign_log" 2>&1 ||
            { cat "$sign_log" >&2; xattr_hint "$(cat "$sign_log")"; fail 5 "codesign failed: ${*: -1}"; }
    }
    for lib in "$contents"/Helpers/Ranker/*.dylib; do sign "$lib"; done
    sign --entitlements "$entitlements/ranker-worker.plist" -i "$bundle_id.rankerworker" \
        "$contents/Helpers/Ranker/AsteriaScale.RankerWorker"
    sign --entitlements "$entitlements/corehost.plist" -i "$bundle_id.corehost" "$contents/Helpers/AsteriaScale.CoreHost"
    # Never --deep: nested code keeps its own entitlements and identifier.
    sign --entitlements "$entitlements/app.plist" "$app"
fi

step "Verify"
if ! verify=$(codesign --verify --strict --deep --verbose=2 "$app" 2>&1); then
    echo "$verify" >&2
    xattr_hint "$verify"
    fail 5 "codesign --verify failed"
fi
# --verbose=2 also lists each nested file as --prepared/--validated.
grep -v -e '^--prepared:' -e '^--validated:' <<<"$verify" | sed 's/^/  /'
entitlements_json() {
    codesign -d --entitlements - --xml "$1" 2>/dev/null | python3 -I -c '
import json, plistlib, sys
data = sys.stdin.buffer.read()
print(json.dumps(plistlib.loads(data) if data.strip() else {}, sort_keys=True))'
}
check_binary() {
    local file=$1 expected_entitlements=$2 expected_identifier=$3 details flags identifier actual
    details=$(codesign -dv "$file" 2>&1)
    flags=$(sed -n 's/.*flags=0x[0-9a-f]*(\([^)]*\)).*/\1/p' <<<"$details")
    identifier=$(sed -n 's/^Identifier=//p' <<<"$details")
    actual=$(entitlements_json "$file")
    echo "  ${file#"$app/"}: identifier $identifier, flags $flags, entitlements $actual"
    [[ ,$flags, == *,runtime,* ]] || fail 5 "${file#"$app/"} lacks the hardened runtime"
    [[ $actual == "$expected_entitlements" ]] || fail 5 "${file#"$app/"} has entitlements $actual, expected $expected_entitlements"
    [[ $identifier == "$expected_identifier" ]] || fail 5 "${file#"$app/"} has identifier $identifier"
}
check_binary "$app" '{}' "$bundle_id"
check_binary "$contents/Helpers/AsteriaScale.CoreHost" '{"com.apple.security.cs.allow-jit": true}' "$bundle_id.corehost"
check_binary "$contents/Helpers/Ranker/AsteriaScale.RankerWorker" \
    '{"com.apple.security.cs.allow-jit": true, "com.apple.security.cs.disable-library-validation": true}' \
    "$bundle_id.rankerworker"
for lib in "$contents"/Helpers/Ranker/*.dylib; do
    flags=$(codesign -dv "$lib" 2>&1 | sed -n 's/.*flags=0x[0-9a-f]*(\([^)]*\)).*/\1/p')
    [[ ,$flags, == *,runtime,* ]] || fail 5 "${lib#"$app/"} lacks the hardened runtime"
done
plist_value() { plutil -extract "$1" raw -o - "$contents/Info.plist"; }
[[ $(plist_value CFBundleIdentifier) == "$bundle_id" ]] || fail 5 "Info.plist names another bundle id"
echo "  bundle id $(plist_value CFBundleIdentifier), version $(plist_value CFBundleShortVersionString)" \
    "($(plist_value CFBundleVersion)), CoreHost build $(plist_value AsteriaCoreHostBuild)"
echo "  designated requirement: $(codesign -d -r- "$app" 2>&1 | sed -n 's/^.*designated => //p')"
du -sh "$app" | sed 's/^/  size: /'

status=0
if [[ $mode != none ]]; then
    # --home was checked before anything was created; a new folder lies in
    # temp_root, which is outside the clone, and is never the real home.
    if [[ -n $reuse_home ]]; then
        run=$reuse_home
    else
        run=$(mktemp -d "$temp_root/asteria-app.XXXXXX")
        run=$(cd "$run" && pwd -P)
        mkdir -p "$run/home/Library/Application Support" "$run/tmp" "$run/cwd"
        : >"$run/.asteria-isolated"
    fi
    app_args=()
    [[ $mode == self-test ]] && app_args+=(--self-test)
    [[ -n $ranker_models ]] && app_args+=(--ranker-models "$ranker_models")
    exe="$contents/MacOS/AsteriaScale"
    if [[ $mode == self-test ]]; then
        step "Self-test (throwaway home $run)"
    else
        step "Isolated run (throwaway home $run)"
        echo "  app log: $run/home/Library/Logs/AsteriaScale/app.log"
        echo "  quit with «Выход», ^C or kill -TERM; the folder is kept"
    fi
    # Direct exec, not open(1): the terminal stays the responsible process.
    # The self-test's output also goes to a file outside its home, which the
    # summary line is read from.
    selftest_log="$run.selftest.log"
    if [[ $mode == self-test ]]; then
        (cd "$run/cwd" && exec env CFFIXED_USER_HOME="$run/home" HOME="$run/home" TMPDIR="$run/tmp/" \
            "$exe" ${app_args[@]+"${app_args[@]}"} </dev/null >"$selftest_log") &
    else
        (cd "$run/cwd" && exec env CFFIXED_USER_HOME="$run/home" HOME="$run/home" TMPDIR="$run/tmp/" \
            "$exe" ${app_args[@]+"${app_args[@]}"} </dev/null) &
    fi
    app_pid=$!
    echo "  app pid $app_pid"
    if [[ $mode == self-test ]]; then
        # The app's own watchdog ends a hung self-test after 90 s; this one
        # covers an app that hangs before its watchdog starts. Its sleep is
        # killed with it, so nothing outlives the script.
        (
            sleep 150 &
            sleeper=$!
            trap 'kill "$sleeper" 2>/dev/null; exit 0' TERM
            wait "$sleeper" && kill -KILL "$app_pid" 2>/dev/null
        ) &
        watchdog=$!
        app_status=0
        wait "$app_pid" || app_status=$?
        kill "$watchdog" 2>/dev/null || true
        wait "$watchdog" 2>/dev/null || true
        cat "$selftest_log"
        # Exit 0 alone is not a pass: the summary line must say so.
        summary=$(grep -E '^SELFTEST (PASS|FAIL) [0-9]+/[0-9]+' "$selftest_log" || true)
        rm -f "$selftest_log"
        if [[ $app_status -ne 0 || $summary != "SELFTEST PASS "* ]]; then
            echo "build-app: the self-test failed (exit $app_status, ${summary:-no summary line}); app log:" \
                "$run/home/Library/Logs/AsteriaScale/app.log" >&2
            status=6
        fi
    else
        trap 'kill -INT "$app_pid" 2>/dev/null || true' INT
        app_status=0
        wait "$app_pid" || app_status=$?
        while kill -0 "$app_pid" 2>/dev/null; do wait "$app_pid" || app_status=$?; done
        trap - INT
        echo "  the app exited with $app_status"
    fi

    sleep 0.5
    # Killed: only what this run started, that is the app, every process
    # whose arguments name the run folder (CoreHost's --user-data and
    # --log-dir lie in it), and the members and children of those (the
    # worker sits in CoreHost's group). The check before step 1 refused a
    # bundle that was already running, so any other match was started by
    # someone else during the run, or nothing ties it to this run any more:
    # it fails the run too, but is only listed and left running. The run
    # folder is pwd -P's /private/tmp/… (or /private/var/…), but the app
    # hands CoreHost the home it canonicalised, /tmp/…: both names count,
    # or an orphaned CoreHost would only be listed.
    run_alias=$run
    [[ $run == /private/* ]] && run_alias=${run#/private}
    leaders=" $app_pid "
    snapshot=""
    candidates=$(bundle_pids)
    if [[ -n $candidates ]]; then
        snapshot=$(ps -o pid=,ppid=,pgid=,command= -p "$(paste -sd, - <<<"$candidates")" || true)
    fi
    while read -r pid ppid pgid command; do
        [[ -n $pid ]] || continue
        if [[ $pid == "$app_pid" || $command == *"$run/"* || $command == *"$run_alias/"* ]]; then
            leaders+="$pid "
        fi
    done <<<"$snapshot"
    leftovers=""
    others=""
    while read -r pid ppid pgid command; do
        [[ -n $pid ]] || continue
        if [[ $leaders == *" $pid "* || $leaders == *" $pgid "* || $leaders == *" $ppid "* ]]; then
            leftovers+="$pid"$'\n'
        else
            others+="  $pid $pgid $command"$'\n'
        fi
    done <<<"$snapshot"
    if [[ -n $leftovers ]]; then
        echo "build-app: processes this run started are still running; killing them:" >&2
        ps -o pid=,pgid=,command= -p "$(sed '/^$/d' <<<"$leftovers" | paste -sd, -)" >&2 || true
        sed '/^$/d' <<<"$leftovers" | xargs kill -KILL 2>/dev/null || true
        [[ $status -eq 0 ]] && status=11
    fi
    if [[ -n $others ]]; then
        echo "build-app: processes of the bundle that this run cannot claim are running; left running" \
            "(pid, pgid, command):" >&2
        printf '%s' "$others" >&2
        [[ $status -eq 0 ]] && status=11
    fi
    strays=$(find "$run/cwd" -mindepth 1 -print)
    if [[ -n $strays ]]; then
        echo "build-app: the run left entries in its working directory:" >&2
        echo "$strays" >&2
        [[ $status -eq 0 ]] && status=9
    fi
    echo "  throwaway home after the run:"
    (cd "$run" && find home tmp -mindepth 1 -maxdepth 6 | LC_ALL=C sort | sed 's/^/    /')
fi

new_entries=()
for folder in "${real_library_watch[@]}"; do
    if [[ -e $folder ]]; then
        while IFS= read -r path; do new_entries+=("$path"); done < <(find "$folder" -newer "$marker" 2>/dev/null)
    fi
done
for folder in "${real_library_named[@]}"; do
    if [[ -d $folder ]]; then
        while IFS= read -r path; do new_entries+=("$path"); done < <(find "$folder" -maxdepth 1 -newer "$marker" \
            \( -iname "*$bundle_id*" -o -iname '*AsteriaScale*' -o -iname '*Asteria Scale*' \) 2>/dev/null)
    fi
done
if [[ ${#new_entries[@]} -gt 0 ]]; then
    echo "build-app: new entries in the real home:" >&2
    printf '  %s\n' "${new_entries[@]}" >&2
    [[ $status -eq 0 ]] && status=12
fi

# ReportCrash writes a report a moment after the process dies.
sleep 2
crashes_after=$(crash_reports 10)
crashes_new=$(LC_ALL=C comm -13 <(printf '%s\n' "$crashes_before") <(printf '%s\n' "$crashes_after") | sed '/^$/d')
echo "build-app: crash reports of the checks' processes: $(count_lines "$crashes_before") before," \
    "$(count_lines "$crashes_after") after, $(count_lines "$crashes_new") new"
if [[ -n $crashes_new ]]; then
    echo "build-app: new crash reports (left in place for the owner):" >&2
    sed 's/^/  /' <<<"$crashes_new" >&2
    [[ $status -eq 0 ]] && status=10
fi

if [[ $mode == self-test ]]; then
    if [[ $status -eq 0 && $keep -eq 0 ]]; then
        rm -rf "$run"
    else
        echo "build-app: self-test folder kept at $run" >&2
    fi
elif [[ $mode == run ]]; then
    echo "build-app: isolated run folder kept at $run (reuse it with --run-isolated --home $run)"
fi
[[ $status -eq 0 ]] && echo && echo "OK: $app"
exit "$status"
