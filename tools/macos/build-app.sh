#!/usr/bin/env bash
# Builds, assembles, signs and verifies "Asteria Scale.app" with the Command
# Line Tools only (no Xcode), runs it in a throwaway home on request and
# installs a copy of it on request. Roadmap 20.2 and 20.3 (docs/MACOS.md T34,
# T35); docs/DEVELOPMENT.md, «macOS (Apple Silicon)».
#
# Usage:
#   tools/macos/build-app.sh [--output DIR] [--sign IDENTITY] [--no-build]
#                            [--self-test | --run-isolated [--home DIR]]
#                            [--ranker-models DIR] [--install [DIR]] [--keep]
#
#   --output DIR        where the .app goes (default artifacts/macos, git-ignored);
#                       its .work folder holds the build's intermediate files;
#                       never inside a File Provider domain, no "." or ".." in
#                       the part that does not exist yet, no file or dangling
#                       link on the way (exit 2, see below)
#   --sign IDENTITY     codesign identity; default "-" (ad-hoc) or
#                       $ASTERIA_SIGN_IDENTITY; the owner's builds use the
#                       Apple Development identity (docs/MACOS.md M16), agents
#                       always pass --sign -; no effect with --no-build
#   --no-build          reuse the .app already in --output (it is verified
#                       again and keeps its signature)
#   --self-test         run the app's scripted self-test (AsteriaScale --self-test)
#   --run-isolated      run the app until it quits (^C or kill -TERM quit it in
#                       order); prints the throwaway folder and keeps it
#   --home DIR          with --run-isolated: reuse a folder an earlier
#                       --run-isolated printed (it carries a marker file)
#   --ranker-models DIR the worker's model folder for --self-test and
#                       --run-isolated, read in place (normally
#                       src/AsteriaScale.RankerWorker/Models); without it the
#                       app uses the throwaway home's empty Models/Ranker
#   --install [DIR]     after a run whose every check passed (with --self-test:
#                       after the self-test passed), copy the bundle to
#                       DIR/"Asteria Scale.app", replacing an earlier copy of
#                       this app in place (step 7); DIR is the next argument
#                       unless it starts with "--", default ~/Applications of
#                       the account (the owner's alone, so only with an
#                       identity other than "-": agents pass a DIR inside a
#                       mktemp -d folder and sign ad hoc); not with
#                       --run-isolated; the same File Provider and path rules
#                       as --output (exit 2, see below); never launches the copy
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
#  7. With --install, only when every check above passed (status 0, the
#     crash-report listing included): mkdir -p DIR, a temporary sibling
#     DIR/.asteria-install.XXXXXX (mktemp -d, so the same file system as
#     the target), a ditto copy of the bundle into it under a name without
#     .app, a byte comparison with the bundle (diff -r) and codesign
#     --verify --strict --deep of the copy. Up to here ^C and kill -TERM end
#     the run with nothing replaced (exit 130, 143); from the late re-check
#     below to the end of the run INT and TERM are ignored, so the step
#     completes, refuses or is undone and the run ends with its report (a
#     few seconds). Right before the swap the target is checked again: a
#     process running from its Contents (or a process table that cannot be
#     read), a target that is no longer this app's
#     bundle, or a target present when the check before the build found
#     none (or gone when it found one) refuses with exit 7 and replaces
#     nothing. The swap is one renamex_np(2) call (python3 ctypes):
#     RENAME_SWAP exchanges the copy with the earlier one the check before
#     the build found, atomically, RENAME_EXCL puts a first copy in place
#     only if the name is still free, so an interrupted run never leaves DIR
#     without a working "Asteria Scale.app" or with a half-copied one under
#     that name. The installed copy is verified again and its codesign lines
#     are printed; then the earlier copy, now in the temporary sibling, is
#     removed. If that verification fails, the swap is undone (exit 5); if
#     the undo itself fails, the copy that failed verification stays
#     installed, and an earlier copy stays in the sibling, both named
#     (exit 5). The sibling is removed on every other failure path,
#     ^C and kill -TERM included; only a SIGKILL, an earlier copy that rm
#     cannot remove after the swap (a warning names it; the install stands)
#     or a failed undo can leave a .asteria-install.* folder, which the next
#     --install into DIR names in a warning (never removes: it may hold the
#     only earlier copy). A folder of DIR that this run created is removed
#     again (rmdir, empty only) when the install fails. The crash reports
#     are listed once more after the install (exit 10 on a new one; the
#     install stands). The script never launches the installed copy and
#     never calls lsregister.
#  8. Report only (docs/MACOS.md T35), never a failure and never a
#     deletion: the entries of $(getconf DARWIN_USER_CACHE_DIR) (to depth 2)
#     and of the real ~/Library/Caches/com.apple.e5rt.e5bundlecache (to
#     depth 3) that are newer than the run's marker, at most 40 lines per
#     folder plus how many were left out, "none" when there are none; a
#     missing or unreadable folder is a printed note. Printed once in every
#     run that got past the up-front checks (an exit 2 prints none): at the
#     end (INT and TERM ignored), or by the EXIT trap when the run stopped
#     earlier (a failed build, ^C). Any process may have written what it
#     lists. The user cache folder is per account, not per HOME, so a
#     throwaway home does not cover it, and other processes write there all
#     the time: depth 2 names the writer (a bundle id or a service) and the
#     kind of cache (com.apple.metal, com.apple.metalfe), deeper levels are
#     per-file churn. A folder's mtime changes only when an entry directly
#     in it is added, removed or renamed, so a rewrite below the listed
#     depth does not show. The e5rt cache is <OS build>/<model
#     hash>/<bundle>, so depth 3 names the bundle.
# The File Provider guard runs after the argument, platform, tool,
# --ranker-models and TMPDIR checks (all read only) and before the output
# folder is created, in every mode including --no-build; with --install it
# runs on DIR exactly as on --output. File Provider tags each new
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
# and its CoreHost to step 6). With --install, also: no DIR with the
# identity "-" (the default ~/Applications is the owner's, signed with the
# owner's identity, M16), DIR (resolved like --output) that is the output
# folder or lies inside it, an output that lies inside DIR/"Asteria
# Scale.app" (both compared ignoring ASCII case; APFS ignores case by
# default), a
# DIR/"Asteria Scale.app" that is a symbolic link, stored under another
# spelling ("asteria scale.app"), not a folder, or a folder whose
# Contents/Info.plist does not name this app's bundle id (a foreign bundle
# is never replaced), any process running from DIR/"Asteria
# Scale.app"/Contents (an owner's copy started from Finder), and a process
# table that cannot be read. Processes of the output bundle are matched
# with pgrep -f on the physical path and, for /private/…, on its /tmp/… or
# /var/… alias as well; those of DIR/"Asteria Scale.app" the same way
# ignoring case, and also by the executable path the kernel reports
# (proc_pidpath: physical and absolute however the copy was started,
# compared ignoring case), which only compares strings and opens nothing.
# The owner's crash reports (~/Library/Logs/DiagnosticReports; only read) are
# listed before and after with tools/macos/crash-reports.py (G-crash: named
# after AsteriaScale, dotnet, Python, python3 or swift, or with such a
# crashed process, parent or killer); a new one fails the script (exit 10)
# and stays in place, and a listing that fails after the build fails it too
# (exit 10).
#
# Exit codes:
#   0   built and verified (and the self-test passed, and the copy was
#       installed)
#   2   usage error, not Apple Silicon macOS, a missing tool, an --output
#       or --install DIR inside a File Provider domain, with "." or ".." in
#       its missing part or with a file or a dangling link on its way (or
#       one the guard cannot resolve or read), a TMPDIR inside the
#       clone, a bad --home, --no-build without a bundle, an unreadable
#       crash-report folder, a process running from the bundle, --install
#       without DIR and with the identity "-", an --install DIR that is or
#       lies inside the output folder (or holds the output in its bundle), a
#       DIR/"Asteria Scale.app" that is not this app's bundle under that
#       exact name, a process running from it, or an unreadable process
#       table; always before the output folder is created
#   3   a model file or an oversized file in the bundle
#   4   a build failed or printed a warning
#   5   signing or verification failed (the --install copy's too: before
#       the swap nothing was replaced; after it the swap was undone, or, if
#       the undo failed, the message names the unverified copy at the target
#       and the folder holding the earlier one)
#   6   the self-test failed (or did not end within 150 s)
#   7   --install: right before the swap, a process ran from DIR/"Asteria
#       Scale.app", the process table could not be read, or the target was no
#       longer this app's bundle, was there although the check before the
#       build found none, or was gone although it found one; nothing was
#       replaced
#   8   --install: creating DIR or the temporary sibling, the ditto copy, the
#       byte comparison or the rename failed; nothing was replaced
#   9   the run left an entry in its working directory
#   10  a new crash report of the build's, the app's or the install's
#       processes appeared, or the crash reports could not be listed after
#       the build (an install stands)
#   11  a process of the bundle was still running afterwards (killed when
#       this run started it, listed and left running otherwise)
#   12  something new in the real ~/Library is named after the app or SwiftPM
#   130, 143  ^C or kill -TERM (with --install, before the late re-check of
#       step 7: nothing was replaced)
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
install=0
install_arg=""
install_dir=""
install_new_root=""
while [[ $# -gt 0 ]]; do
    case $1 in
        --output) [[ $# -ge 2 && -n $2 ]] || usage; out=$2; shift 2 ;;
        --sign) [[ $# -ge 2 ]] || usage; identity=$2; shift 2 ;;
        --no-build) build=0; shift ;;
        --self-test) [[ $mode == none ]] || usage; mode=self-test; shift ;;
        --run-isolated) [[ $mode == none ]] || usage; mode=run; shift ;;
        --home) [[ $# -ge 2 ]] || usage; reuse_home=$2; shift 2 ;;
        --ranker-models) [[ $# -ge 2 ]] || usage; ranker_models=$2; shift 2 ;;
        --install)
            [[ $install -eq 0 ]] || usage
            install=1
            # DIR is optional: the next argument is DIR unless it is an
            # option. The default (~/Applications) is set once the real home
            # is known below.
            if [[ $# -ge 2 && $2 != --* ]]; then
                [[ -n $2 ]] || usage
                install_arg=$2
                shift 2
            else
                shift
            fi ;;
        --keep) keep=1; shift ;;
        *) usage ;;
    esac
done
# --run-isolated ends whenever the owner quits the app, with no verdict to
# install after, so it does not combine with --install.
if [[ -n $reuse_home && $mode != run ]] || [[ -n $ranker_models && $mode == none ]] ||
    [[ $install -eq 1 && $mode == run ]]; then
    usage
fi
# The default DIR is the owner's ~/Applications, where builds carry the
# owner's identity (M16). An agent always signs ad hoc, so its --install
# without DIR (an unquoted empty "$dir", "--install" followed by another
# option) is refused instead of reaching it; an ad-hoc copy there would also
# lose the Screen Recording grant at every rebuild (its requirement is the
# cdhash).
if [[ $install -eq 1 && -z $install_arg && $identity == - ]]; then
    echo "build-app: refusing --install without DIR: it would install into ~/Applications, the owner's, which" \
        "needs the owner's identity (--sign or ASTERIA_SIGN_IDENTITY, docs/MACOS.md M16), not \"-\"; pass DIR" \
        "explicitly (agents: a folder inside mktemp -d). Nothing was built, created, replaced or killed." >&2
    exit 2
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
# The guard is one function for --output and --install DIR; it exits 2
# itself, so it is called directly, never in $(...), and leaves the physical
# path in fp_resolved (bash 3.2 has no namerefs), the nearest existing folder
# in fp_existing and the part that does not exist yet in fp_missing.
fp_refuse() {
    local subject=$1 folder=$2 marker=$3 detail=$4 remedy=$5
    echo "build-app: refusing $subject: $folder $detail ($marker)." >&2
    echo "build-app: File Provider tags every new .app folder in its domains with com.apple.FinderInfo," \
        "even under a .nosync name, and puts it back at once after xattr -d; codesign rejects such a bundle" \
        "(\"resource fork, Finder information, or similar detritus not allowed\") after a full build." >&2
    echo "build-app: $remedy (docs/DEVELOPMENT.md, «Клон и iCloud Drive»)." \
        "Nothing was built, created, replaced or killed." >&2
    exit 2
}
# fp_guard OPTION ARG WHAT REMEDY ELSEWHERE: OPTION and ARG name the path in
# messages, WHAT is what mkdir -p would create ("the output"), REMEDY the
# advice on a domain root, ELSEWHERE the advice for ~/Library/CloudStorage.
fp_guard() {
    local option=$1 arg=$2 what=$3 remedy=$4 elsewhere=$5
    local probe rest="" dots=0 part nearest dir names nearest_names="" domain tag doc why hint tag_safe
    probe=$arg
    [[ $probe == /* ]] || probe="$(pwd -P)/$probe"
    # A trailing slash makes -L and -e look through a link: "link/" to nothing
    # is neither, so the walk would take the link for a missing folder. dirname
    # never returns one, so only the start needs this.
    while [[ $probe == */ && $probe != / ]]; do probe=${probe%/}; done
    while [[ ! -d $probe ]]; do
        # -d follows links, so a link to a folder ends the walk and cd -P below
        # resolves it. What exists here and is not a folder would stop mkdir -p
        # with exit 1 and no message of this script, so it is refused instead.
        if [[ -L $probe || -e $probe ]]; then
            echo "build-app: refusing $option $arg: $probe exists but is not a folder (a file, or a" \
                "symbolic link to nothing or to a file), so mkdir -p could not create $what there." \
                "Nothing was built, created, replaced or killed." >&2
            exit 2
        fi
        part=$(basename "$probe")
        if [[ $part == . || $part == .. ]]; then dots=1; fi
        rest=$part${rest:+/$rest}
        probe=$(dirname "$probe")
    done
    nearest=$(cd -P "$probe" 2>/dev/null && pwd -P) ||
        { echo "build-app: cannot resolve $probe, the nearest existing folder of $option $arg" >&2; exit 2; }
    dir=$nearest
    while :; do
        names=$(/usr/bin/xattr "$dir" 2>/dev/null) ||
            { echo "build-app: cannot list the extended attributes of $dir ($option $arg)" >&2; exit 2; }
        [[ $dir == "$nearest" ]] && nearest_names=$names
        if [[ $'\n'$names$'\n' == *$'\n'com.apple.file-provider-domain-id$'\n'* ]]; then
            # The value is "<provider>/<account's domain UUID>": only the
            # provider is printed.
            domain=$(/usr/bin/xattr -p com.apple.file-provider-domain-id "$dir" 2>/dev/null) ||
                domain=unreadable
            fp_refuse "$option $arg" "$dir" "com.apple.file-provider-domain-id, provider ${domain%%/*}" \
                "is the root of a File Provider domain, and $what would lie inside it" "$remedy"
        fi
        [[ $dir == / ]] && break
        dir=$(dirname "$dir")
    done
    if [[ $dots -eq 1 ]]; then
        echo "build-app: refusing $option $arg: its part that does not exist yet ($rest) has a \".\" or" \
            "\"..\" component, which mkdir -p resolves only after creating the folders before it, so the File" \
            "Provider check cannot tell where the bundle would go; pass the path without them." \
            "Nothing was built, created, replaced or killed." >&2
        exit 2
    fi
    tag=$(sed -n '/^com\.apple\.fileprovider\./{p;q;}' <<<"$nearest_names")
    if [[ -n $tag ]]; then
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
        doc="docs/DEVELOPMENT.md, «Клон и iCloud Drive»"
        why="so it is most likely left over from a move out of a domain, where it is inert"
        hint="see $doc"
        tag_safe='^com\.apple\.fileprovider\.[A-Za-z0-9._#-]+$'
        case "$nearest/" in
            "$real_home/Library/CloudStorage/"*)
                why="but the folder lies in ~/Library/CloudStorage, whose providers may not mark their domain roots"
                hint="do not remove the tag, and if codesign fails, $elsewhere ($doc)" ;;
            "$repo/"*)
                if [[ $tag =~ $tag_safe && $repo != *\'* ]]; then
                    hint="to remove such tags from the clone (optional, the owner's): find '$repo' -xattrname"
                    hint+=" '$tag' -exec /usr/bin/xattr -s -d '$tag' {} + ($doc)"
                fi ;;
        esac
        echo "build-app: warning: $nearest carries $tag, a File Provider tag; no File Provider domain root" \
            "was found above it, $why; the script goes on; $hint." >&2
    fi
    # The physical path the guard checked: mkdir -p creates exactly these
    # folders, since none of rest exists and none of it is "." or "..".
    fp_existing=$nearest
    fp_missing=$rest
    if [[ -z $rest ]]; then
        fp_resolved=$nearest
    elif [[ $nearest == / ]]; then
        fp_resolved=/$rest
    else
        fp_resolved=$nearest/$rest
    fi
}
out_arg=$out
fp_guard --output "$out_arg" "the output" \
    "move the clone out of every File Provider domain, or pass --output outside them" "pass --output elsewhere"
out=$fp_resolved
if [[ $install -eq 1 ]]; then
    # The account's own ~/Applications, whatever HOME says.
    [[ -n $install_arg ]] || install_arg="$real_home/Applications"
    fp_guard --install "$install_arg" "the install folder" \
        "pass --install a folder outside every File Provider domain" "pass --install another folder"
    install_dir=$fp_resolved
    # The first folder mkdir -p will create (empty when DIR exists): a
    # failed install removes what this run created, and nothing else.
    install_new_root=""
    if [[ -n $fp_missing ]]; then
        if [[ $fp_existing == / ]]; then install_new_root=/${fp_missing%%/*}; else install_new_root=$fp_existing/${fp_missing%%/*}; fi
    fi
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

# pgrep -f takes an extended regular expression; a bundle path is matched
# literally, by its physical name and, under /private, also by the /tmp/…
# or /var/… alias a process may have been started with.
ere_literal() { sed 's/[][\\.^$*+?(){}|]/\\&/g' <<<"$1"; }
contents_pattern() {
    local contents="$1/Contents/" pattern
    pattern=$(ere_literal "$contents")
    if [[ $contents == /private/* ]]; then pattern+="|$(ere_literal "${contents#/private}")"; fi
    printf '%s\n' "$pattern"
}
# --install's own refusals (header): still before anything is created. The
# same checks of the target run again right before the swap (step 7), since
# the owner may start the installed copy while this script builds. They come
# before the output's process check: a DIR inside the output bundle puts
# that bundle's Contents path into this script's own arguments, which
# pgrep -f would then list as a copy running from it.
if [[ $install -eq 1 ]]; then
    install_target="$install_dir/Asteria Scale.app"
    install_pattern=$(contents_pattern "$install_target")
    # The pids whose executable lies in DIR/"Asteria Scale.app"/Contents, by
    # the path the kernel reports (libproc's proc_pidpath: physical and
    # absolute, however the copy was started: by a relative path, through a
    # link, in another letter case), compared ignoring case as APFS does by
    # default. Only the kernel's strings are compared, nothing of another
    # process is opened. The path goes in the environment, not the
    # arguments, so pgrep -f never sees it here. Fails (status 1) when the
    # process table cannot be read.
    install_exec_pids() {
        ASTERIA_INSTALL_CONTENTS="$install_target/Contents/" python3 -I - <<'PY'
import ctypes, os, sys
def norm(path):
    # The data volume's firmlinked prefix, if a path comes back with it.
    path = path.casefold()
    data = "/system/volumes/data/"
    return path[len(data) - 1:] if path.startswith(data) else path
prefix = norm(os.environ["ASTERIA_INSTALL_CONTENTS"])
libc = ctypes.CDLL(None, use_errno=True)
libc.proc_listallpids.argtypes = (ctypes.c_void_p, ctypes.c_int)
libc.proc_listallpids.restype = ctypes.c_int
libc.proc_pidpath.argtypes = (ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32)
libc.proc_pidpath.restype = ctypes.c_int
count = libc.proc_listallpids(None, 0)
pids = (ctypes.c_int * (max(count, 0) + 256))()
count = libc.proc_listallpids(pids, ctypes.sizeof(pids)) if count > 0 else count
if count <= 0:
    sys.exit("build-app: cannot list the processes (proc_listallpids)")
path = ctypes.create_string_buffer(4096)  # PROC_PIDPATHINFO_MAXSIZE
for pid in pids[:count]:
    # A process of another user, or one gone meanwhile, has no path here.
    if pid > 0 and libc.proc_pidpath(pid, path, ctypes.sizeof(path)) > 0:
        if norm(os.fsdecode(path.value)).startswith(prefix):
            print(pid)
PY
    }
    # Both sources: the executable path above, and pgrep -f ignoring case for
    # a process that names the target in its arguments (as for the output).
    install_pids() {
        local by_exec
        by_exec=$(install_exec_pids) || return 1
        { pgrep -i -f "$install_pattern" || true; printf '%s\n' "$by_exec"; } | sed '/^$/d' | sort -un
    }
    # Prints why DIR/"Asteria Scale.app" must not be replaced, or nothing
    # when it is absent or this app's bundle. A link is refused rather than
    # followed: the swap would move the link and the process check would
    # look at another path.
    install_target_problem() {
        local id stored
        if [[ -L $install_target ]]; then
            echo "$install_target is a symbolic link"
            return 0
        fi
        [[ -e $install_target ]] || return 0
        # The name as stored (readdir), not as typed: APFS finds "asteria
        # scale.app" under "Asteria Scale.app", and the swap would keep the
        # stored spelling.
        stored=$(find "$install_dir" -mindepth 1 -maxdepth 1 -iname 'Asteria Scale.app' 2>/dev/null) || stored=""
        if [[ -z $stored ]]; then
            echo "$install_dir cannot be listed, so the stored name of $install_target is unknown"
            return 0
        elif ! grep -qxF "$install_target" <<<"$stored"; then
            echo "$install_target is stored as \"$(sed -n '1s|.*/||p' <<<"$stored")\"; rename it to" \
                "\"Asteria Scale.app\" first"
            return 0
        fi
        if [[ ! -d $install_target ]]; then
            echo "$install_target exists and is not a folder"
            return 0
        fi
        id=$(plutil -extract CFBundleIdentifier raw -o - "$install_target/Contents/Info.plist" 2>/dev/null) || id=""
        if [[ -z $id ]]; then
            echo "$install_target has no CFBundleIdentifier in Contents/Info.plist, so it is not this app's bundle"
        elif [[ $id != "$bundle_id" ]]; then
            echo "$install_target is the bundle of $id, not of $bundle_id"
        fi
        return 0
    }
    install_refuse() {
        echo "build-app: refusing --install $install_arg: $*. Nothing was built, created, replaced or killed." >&2
        exit 2
    }
    # APFS ignores case in names by default, so the overlap checks do too
    # (ASCII only). pwd -P does not always spell the existing part as stored:
    # it may keep the case a path was typed in.
    lowercase() { tr '[:upper:]' '[:lower:]' <<<"$1"; }
    path_within() { [[ $2 == / ]] && return 0; case "$1/" in "$2/"*) return 0 ;; esac; return 1; }
    if path_within "$(lowercase "$install_dir")" "$(lowercase "$out")"; then
        install_refuse "$install_dir is the output folder $out or lies inside it (step 1 replaces its bundle" \
            "and .work)"
    fi
    if path_within "$(lowercase "$out")" "$(lowercase "$install_target")"; then
        install_refuse "the output folder $out lies inside $install_target, which the install would replace"
    fi
    install_problem=$(install_target_problem)
    [[ -z $install_problem ]] || install_refuse "$install_problem; a foreign bundle or file is never replaced"
    # Step 7 swaps only with the earlier copy found here, and puts a first
    # copy in place only where none was: whatever appears or goes meanwhile
    # is exit 7.
    install_target_existed=0
    [[ ! -e $install_target ]] || install_target_existed=1
    running=$(install_pids) || install_refuse "the process table cannot be read, so a copy running from" \
        "$install_target cannot be ruled out"
    if [[ -n $running ]]; then
        echo "build-app: refusing --install $install_arg: processes are running from $install_target; quit" \
            "that copy first (nothing was built, created, replaced or killed):" >&2
        ps -o pid=,command= -p "$(paste -sd, - <<<"$running")" >&2 || true
        exit 2
    fi
    # A SIGKILL, an earlier copy rm could not remove or a failed undo leaves
    # one (step 7); it is the owner's to look into and remove.
    if [[ -d $install_dir ]]; then
        stale=$(find "$install_dir" -mindepth 1 -maxdepth 1 -name '.asteria-install.*' 2>/dev/null || true)
        if [[ -n $stale ]]; then
            echo "build-app: warning: temporary folders of an earlier --install are left in $install_dir" \
                "(not removed; one may hold the only earlier copy of the app, so look before deleting):" >&2
            sed 's/^/  /' <<<"$stale" >&2
        fi
    fi
fi

app_pattern=$(contents_pattern "$app")
bundle_pids() { pgrep -f "$app_pattern" || true; }
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
# com.apple.provenance it accepts); the exit code stays 5. The second
# argument names another bundle (the installed copy).
xattr_hint() {
    local bundle=${2:-$app}
    grep -q -e 'detritus' -e 'Disallowed xattr' <<<"$1" || return 0
    echo "build-app: codesign failed on extended attributes; found under $bundle (files, name; the likely" \
        "culprits are marked):" >&2
    { /usr/bin/xattr -r "$bundle" 2>/dev/null || true; } | sed 's/^.*: //' | LC_ALL=C sort | uniq -c |
        sed -E 's/^ */  /; s/ (com\.apple\.(FinderInfo|ResourceFork|fileprovider\..*))$/ \1  <- likely culprit/' >&2 ||
        true
    echo "build-app: File Provider (iCloud Drive's Desktop and Documents, ~/Library/Mobile Documents, the" \
        "folders in ~/Library/CloudStorage) puts com.apple.FinderInfo and com.apple.fileprovider.* on new" \
        ".app folders and renews them after xattr -d; build with --output outside every File Provider domain" \
        "(docs/DEVELOPMENT.md, «Клон и iCloud Drive»)." >&2
}

crashes_before=$(crash_reports 2)
echo "build-app: crash reports of the checks' processes in $reports: $(count_lines "$crashes_before")"
# Step 8 (header): a report, never a failure and never a deletion. Printed
# once, at the end of the run or, after an earlier exit, by the EXIT trap.
cache_report_folder() {
    local folder=$1 depth=$2 listing total limit=40
    if [[ ! -e $folder ]]; then
        echo "  $folder: missing, nothing to list"
        return 0
    fi
    if [[ ! -d $folder || ! -r $folder || ! -x $folder ]]; then
        echo "  $folder: not a readable folder, not listed"
        return 0
    fi
    # Subfolders other processes keep private (sandbox containers) are
    # skipped silently; find then exits 1, which says nothing about the rest.
    listing=$(cd "$folder" && find . -mindepth 1 -maxdepth "$depth" -newer "$marker" 2>/dev/null |
        sed 's|^\./||' | LC_ALL=C sort) || true
    total=$(count_lines "$listing")
    if [[ $total -eq 0 ]]; then
        echo "  $folder (depth $depth): none"
        return 0
    fi
    echo "  $folder (depth $depth): $total"
    head -n "$limit" <<<"$listing" | sed 's/^/    /'
    if [[ $total -gt $limit ]]; then echo "    ... and $((total - limit)) more, not shown"; fi
    return 0
}
cache_reported=0
cache_report() {
    local user_cache
    user_cache=$(getconf DARWIN_USER_CACHE_DIR 2>/dev/null) || user_cache=""
    echo
    echo "build-app: caches newer than this run's start (docs/MACOS.md T35; a report, not a failure; removal is" \
        "the owner's):"
    if [[ -n $user_cache ]]; then
        cache_report_folder "${user_cache%/}" 2
    else
        echo "  getconf DARWIN_USER_CACHE_DIR printed nothing, not listed"
    fi
    cache_report_folder "$real_home/Library/Caches/com.apple.e5rt.e5bundlecache" 3
    cache_reported=1
}
# One EXIT trap for everything this run may leave: the cache report (step 8)
# when the run ended before it, the marker and, with
# --install, the temporary sibling in DIR (only the path mktemp -d returned
# there, never one built from DIR alone) and the folders of DIR this run
# created when the install did not complete. Each command here ends in
# "|| true", so the trap never changes the exit status. INT and TERM exit
# through it, so ^C and kill -TERM clean up as well; inside it they are
# ignored (also by its rm, rmdir and find), so a second ^C cannot cut the
# cleanup short.
marker=""
install_tmp=""
install_made_dir=0
install_done=0
cleanup() {
    local dir
    trap '' INT TERM
    if [[ -n $install_tmp && $install_tmp == "$install_dir"/.asteria-install.* ]]; then
        rm -rf "$install_tmp" 2>/dev/null || true
    fi
    if [[ $install_made_dir -eq 1 && $install_done -eq 0 && -n $install_new_root ]]; then
        dir=$install_dir
        while rmdir "$dir" 2>/dev/null && [[ $dir != "$install_new_root" ]]; do dir=$(dirname "$dir") || break; done
    fi
    if [[ -n $marker ]]; then
        [[ $cache_reported -eq 1 ]] || cache_report || true
        rm -f "$marker" || true
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir -p "$out"
marker=$(mktemp "$temp_root/asteria-build-app-marker.XXXXXX")
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
        # The app's own watchdog ends a hung self-test after 120 s; this one
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
        trap 'exit 130' INT
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

# renamex_np(2) through python3's ctypes; bash has no other way to call it.
# MODE swap exchanges two existing names atomically, excl renames only if
# the target name is still free. Exits 3 when the target appeared (excl) or
# vanished (swap) since the check, 1 on any other error.
rename_atomic() {
    python3 -I - "$@" <<'PY'
import ctypes, errno, os, sys
mode, source, target = sys.argv[1:4]
flags = {"swap": 0x2, "excl": 0x4}[mode]  # RENAME_SWAP, RENAME_EXCL (<sys/stdio.h>)
libc = ctypes.CDLL(None, use_errno=True)
libc.renamex_np.argtypes = (ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint)
libc.renamex_np.restype = ctypes.c_int
if libc.renamex_np(os.fsencode(source), os.fsencode(target), flags) != 0:
    code = ctypes.get_errno()
    print(f"build-app: renamex_np {mode} {source} -> {target}: {os.strerror(code)}", file=sys.stderr)
    sys.exit(3 if code in (errno.EEXIST, errno.ENOENT) else 1)
PY
}
# Step 7 (header). Called only in an "||" list, where set -e is off, so every
# failure is handled here; returns 0, 5, 7 or 8.
install_copy() {
    local payload verify rename_mode rename_status=0 problem
    step "Install into $install_dir"
    if [[ ! -d $install_dir ]]; then
        install_made_dir=1
        mkdir -p "$install_dir" || { echo "build-app: cannot create $install_dir" >&2; return 8; }
    fi
    install_tmp=$(mktemp -d "$install_dir/.asteria-install.XXXXXX") ||
        { install_tmp=""; echo "build-app: cannot create a temporary folder in $install_dir" >&2; return 8; }
    # The copy's name has no .app: only the target ever carries the bundle's
    # name, so nothing sees a second, half-copied app in DIR.
    payload="$install_tmp/bundle"
    if ! ditto "$app" "$payload"; then
        echo "build-app: ditto $app -> $payload failed" >&2
        return 8
    fi
    if ! diff -rq "$app" "$payload" >&2; then
        echo "build-app: the copy in $install_tmp differs from $app" >&2
        return 8
    fi
    echo "  ditto copy in $install_tmp: byte-identical to the bundle (diff -r)"
    if ! verify=$(codesign --verify --strict --deep --verbose=2 "$payload" 2>&1); then
        echo "$verify" >&2
        xattr_hint "$verify" "$payload"
        echo "build-app: codesign --verify of the copy failed; nothing was replaced" >&2
        return 5
    fi
    # From here to the end of the run INT and TERM are ignored, by this
    # shell and the commands it starts (python3 then installs no
    # KeyboardInterrupt handler): the swap either completes, with the
    # installed copy verified and reported, or is undone. A ^C up to here
    # ended the run with nothing replaced.
    trap '' INT TERM
    # The late re-check: a copy the owner started, or a bundle put in place,
    # while this run built or tested. Only the instant between this check
    # and the rename below stays open.
    if ! running=$(install_pids); then
        echo "build-app: the process table cannot be read, so a copy running from $install_target cannot" \
            "be ruled out (nothing was replaced)" >&2
        return 7
    fi
    if [[ -n $running ]]; then
        echo "build-app: processes started from $install_target during the run; quit that copy and run" \
            "--install again (nothing was replaced):" >&2
        ps -o pid=,command= -p "$(paste -sd, - <<<"$running")" >&2 || true
        return 7
    fi
    problem=$(install_target_problem)
    if [[ -n $problem ]]; then
        echo "build-app: $problem now, so it is not replaced (nothing was replaced)" >&2
        return 7
    fi
    # The mode follows the check before the build, not the target's state
    # now: a bundle that appeared meanwhile makes RENAME_EXCL fail (EEXIST),
    # one that went makes RENAME_SWAP fail (ENOENT); both are exit 7.
    if [[ $install_target_existed -eq 1 ]]; then rename_mode=swap; else rename_mode=excl; fi
    rename_atomic "$rename_mode" "$payload" "$install_target" || rename_status=$?
    if [[ $rename_status -eq 3 ]]; then
        if [[ $rename_mode == swap ]]; then
            echo "build-app: $install_target is gone since the check before the build (nothing was replaced)" >&2
        else
            echo "build-app: $install_target appeared since the check before the build found none (nothing" \
                "was replaced)" >&2
        fi
        return 7
    elif [[ $rename_status -ne 0 ]]; then
        echo "build-app: the rename into $install_target failed (nothing was replaced)" >&2
        return 8
    fi
    if ! verify=$(codesign --verify --strict --deep --verbose=2 "$install_target" 2>&1); then
        echo "$verify" >&2
        xattr_hint "$verify" "$install_target"
        # Undo: the earlier copy (or the free name) comes back. Should the
        # undo fail, nothing more is moved or removed and both are named.
        if [[ $rename_mode == swap ]]; then
            if ! rename_atomic swap "$payload" "$install_target"; then
                echo "build-app: codesign --verify of the installed copy failed and the swap could not be" \
                    "undone: the copy that failed verification stays at $install_target, and the earlier copy" \
                    "is kept at $payload (both left for the owner)" >&2
                install_tmp=""
                install_done=1
                return 5
            fi
            echo "build-app: codesign --verify of the installed copy failed; the swap was undone (the earlier" \
                "copy is back at $install_target)" >&2
        else
            if ! rename_atomic excl "$install_target" "$payload"; then
                echo "build-app: codesign --verify of the installed copy failed and the rename back failed:" \
                    "the copy that failed verification stays at $install_target (left for the owner)" >&2
                return 5
            fi
            echo "build-app: codesign --verify of the installed copy failed; the rename was undone" \
                "($install_target is free again)" >&2
        fi
        return 5
    fi
    grep -v -e '^--prepared:' -e '^--validated:' <<<"$verify" | sed 's/^/  /'
    install_done=1
    if [[ $rename_mode == swap ]]; then
        echo "  replaced the earlier copy in place (atomic swap); removing it"
    else
        echo "  installed as a first copy"
    fi
    # The earlier copy now sits where the payload was. The install stands
    # even if it cannot be removed (a flag or a policy on a copy that ran,
    # say): the leftover is named, and the next --install warns about it.
    if ! rm -rf "$install_tmp"; then
        echo "build-app: warning: the install stands, but the earlier copy could not be removed;" \
            "$install_tmp is left in place (remove it by hand)" >&2
    fi
    install_tmp=""
    echo "  installed: $install_target (not launched; LaunchServices not touched)"
}
if [[ $install -eq 1 ]]; then
    if [[ $status -eq 0 ]]; then
        install_status=0
        install_copy || install_status=$?
        status=$install_status
        # The install's own processes (python3, ditto, diff, codesign) count
        # for G-crash too; a new report fails the run, the install stands.
        sleep 2
        crashes_install=$(crash_reports 10)
        crashes_new=$(LC_ALL=C comm -13 <(printf '%s\n' "$crashes_before") <(printf '%s\n' "$crashes_install") |
            sed '/^$/d')
        echo "build-app: crash reports of the checks' processes after the install: $(count_lines "$crashes_install")," \
            "$(count_lines "$crashes_new") new"
        if [[ -n $crashes_new ]]; then
            echo "build-app: new crash reports (left in place for the owner):" >&2
            sed 's/^/  /' <<<"$crashes_new" >&2
            [[ $status -eq 0 ]] && status=10
        fi
    else
        echo "build-app: not installed into $install_dir: the run failed (exit $status); $install_target" \
            "was not touched" >&2
    fi
fi

# The report and the verdict are not cut short by a late ^C (step 8).
trap '' INT TERM
cache_report
if [[ $status -eq 0 ]]; then
    echo
    echo "OK: $app"
    [[ $install -eq 0 ]] || echo "installed: $install_target"
fi
exit "$status"
