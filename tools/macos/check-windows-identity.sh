#!/usr/bin/env bash
# Proves on a macOS clone that a change leaves every Windows build output
# byte-identical, without a Windows machine.
#
# Usage: tools/macos/check-windows-identity.sh <base-rev> [--worktree | <candidate-rev>]
#        (the candidate defaults to --worktree: tracked plus untracked,
#        non-ignored files of the working tree, uncommitted edits included)
#
# For each side it cross-builds the two Windows outputs that matter: the
# ranker worker publish that scripts/build.ps1 performs (-r win-x64,
# self-contained, no symbols) and the Windows smoke-suite build (WPF, Core,
# RankerProtocol, Tests and their data). It then compares SHA-256 manifests of
# every produced file and of every source-generator output (see below). Exit 0
# prints "IDENTICAL: <n> Windows output files"; exit 1 prints the manifest
# diff and keeps the work folder for inspection.
#
# Why exports instead of the live tree: with EnableWindowsTargeting the
# Windows worker project schedules the local CC BY-NC-SA ConSeC bundle
# (src/AsteriaScale.RankerWorker/Models/Consec, about 1.6 GB) into bin/.
# Both sides are therefore built from exports in $TMPDIR: `git archive` for a
# revision and `git ls-files -co --exclude-standard` for the working tree. The
# bundle is git-ignored, and the working-tree export also refuses any non-empty
# file under ConSeC_WNGT/ or Models/Consec/ before copying it, because the
# owner's ConSeC_WNGT/ transfer folder is ignored only by a local
# .git/info/exclude. A revision that tracks such a file trips the check below.
# Four zero-byte placeholders stand in for the bundle files so the worker's
# conditional ConSeC Content path is still exercised without licensed bytes.
#
# Two more guards keep an export from reaching outside itself:
# - Symlinks. tar stores a link, not its target, so neither the size test nor
#   the tripwire sees a bundle behind a link, and writing a placeholder
#   through a link into the real bundle would truncate it. The working-tree
#   export therefore refuses a symlink at ConSeC_WNGT, at Models/Consec or
#   anywhere below them, and at src, the worker folder or Models, whenever git
#   lists one (an untracked link at or under Models/Consec is ignored by
#   .gitignore and never listed, so the export simply omits it). Before
#   writing anything, add_consec_placeholders refuses any symlink on the
#   placeholder paths or under Models (a revision can track one too) and
#   deletes the export; rm -rf removes links and never follows them.
# - Nested repositories. `git ls-files -o` lists an untracked nested
#   repository as one "dir/" entry, and tar would recurse into it and copy
#   its ignored files and its .git. Such entries are skipped with a note, as a
#   commit would record none of their files, and the creating tar runs with
#   --no-recursion, so it archives exactly the listed paths.
#
# Why both sides build at the same absolute path, one after the other:
# [GeneratedRegex] type names and obj/ state embed the build directory, so
# the same sources built in two different folders already differ
# (AsteriaScale.Core.dll was observed as 128d640f vs 97886ec3). Build servers
# are disabled so no compiler or MSBuild node state outlives one side.
#
# Why AppxGeneratePriEnabled=false on the smoke-suite build: MakePri.exe
# cannot run on macOS. The switch applies to both sides equally, so it cannot
# hide a difference in compiled code; PRI/MRT generation itself is covered
# only by the owner's Windows build.
#
# Why the smoke suite is built twice per side: the CsWinRT AOT generator in the
# Windows SDK projection writes WinRTGlobalVtableLookup.g.cs for the test
# project with its type checks in a different order from one compiler run to
# the next, so even an unchanged tree gives a different AsteriaScale.Tests.dll
# (observed: f3e88f94 vs ee9490fe vs d913a757 from the same sources). The first
# build is the normal one and only records every generator output, compared
# as sorted token lists so that order is ignored. The second, which feeds the
# byte manifest, turns that one lookup-table generator off
# (CsWinRTCcwLookupTableGeneratorEnabled) and is reproducible; every other
# compiled byte is still compared exactly.
#
# What this does not prove: anything that runs only on Windows (the smoke
# suite itself, NPU/VitisAI, MSIX packaging). Those runs see unchanged inputs
# when this check passes.
set -euo pipefail

usage() {
    echo "usage: $0 <base-rev> [--worktree | <candidate-rev>]" >&2
    exit 2
}

[[ $# -ge 1 && $# -le 2 ]] || usage
candidate_arg=${2:---worktree}

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
repo=$(git -C "$script_dir" rev-parse --show-toplevel)
repo=$(cd "$repo" && pwd -P)
base_sha=$(git -C "$repo" rev-parse --verify --quiet "$1^{commit}") \
    || { echo "check-windows-identity: '$1' is not a commit" >&2; exit 2; }
if [[ $candidate_arg == --worktree ]]; then
    candidate_rev=--worktree
    candidate_label="working tree at $(git -C "$repo" rev-parse HEAD)"
else
    # Resolved once, so a ref that moves during the run cannot change the side.
    candidate_rev=$(git -C "$repo" rev-parse --verify --quiet "$candidate_arg^{commit}") \
        || { echo "check-windows-identity: '$candidate_arg' is not a commit" >&2; exit 2; }
    candidate_label="$candidate_rev"
fi

work=$(mktemp -d "${TMPDIR:-/tmp}/asteria-winid.XXXXXX")
work=$(cd "$work" && pwd -P)
case "$work/" in
    "$repo/"*)
        echo "check-windows-identity: refusing to build inside the repository ($work)" >&2
        rmdir "$work"
        exit 2
        ;;
esac

finished=0
on_exit() {
    if [[ $finished -eq 0 ]]; then
        echo "check-windows-identity: failed; work folder kept at $work" >&2
    fi
}
trap on_exit EXIT

# macOS tar would otherwise add AppleDouble "._" entries for extended
# attributes to the working-tree archive, which no git checkout contains.
export COPYFILE_DISABLE=1

echo "repo:      $repo"
echo "base:      $base_sha"
echo "candidate: $candidate_label"
echo "work:      $work"
echo "dotnet:    $(dotnet --version)"

run_logged() {
    local log=$1
    shift
    if ! "$@" >"$log" 2>&1; then
        echo "check-windows-identity: command failed, last lines of $log:" >&2
        tail -n 40 "$log" >&2
        return 1
    fi
}

export_tree() {
    local rev=$1
    rm -rf "$work/tree"
    mkdir "$work/tree"
    if [[ $rev == --worktree ]]; then
        # A tracked file deleted in the working tree is still listed by -c;
        # skip it, as a commit of the working tree would not contain it.
        # Licensed ConSeC payload is refused before tar reads a single byte of
        # it: ConSeC_WNGT/ is ignored only through the owner's local
        # .git/info/exclude, so on another clone it would otherwise be listed
        # and copied into $TMPDIR before the placeholder tripwire could fire.
        # Refusing (not skipping) keeps a staged licensed file from passing as
        # IDENTICAL. A symlink there is refused whatever it points at: tar
        # would store the link, which -s cannot see behind, and the
        # placeholders would later be written through it (header comment).
        # ${path%/} drops the slash git appends to a nested repository, so -L
        # tests the entry itself rather than what a trailing slash resolves to.
        # An exit inside this loop ends only its pipeline stage; pipefail
        # carries the status out of both pipelines and errexit stops the run.
        (
            cd "$repo"
            git ls-files -z -co --exclude-standard --deduplicate |
                while IFS= read -r -d '' path; do
                    entry=${path%/}
                    case $entry in
                        ConSeC_WNGT | ConSeC_WNGT/* | \
                            src/AsteriaScale.RankerWorker/Models/Consec | \
                            src/AsteriaScale.RankerWorker/Models/Consec/* | \
                            consec-wngt-fp32.onnx* | */consec-wngt-fp32.onnx*)
                            if [[ -L $entry || -s $entry ]]; then
                                echo "check-windows-identity: working tree lists ConSeC payload or a link" \
                                    "($path); refusing to export it" >&2
                                exit 3
                            fi
                            ;;
                        src | src/AsteriaScale.RankerWorker | src/AsteriaScale.RankerWorker/Models)
                            if [[ -L $entry ]]; then
                                echo "check-windows-identity: working tree has a symlink on the ConSeC path" \
                                    "($path); refusing to export it" >&2
                                exit 3
                            fi
                            ;;
                    esac
                    if [[ -d $entry && ! -L $entry ]]; then
                        echo "check-windows-identity: skipping $path (an untracked nested repository;" \
                            "a commit would record none of its files)" >&2
                        continue
                    fi
                    if [[ -e $path || -L $path ]]; then
                        printf '%s\0' "$path"
                    fi
                done |
                tar --null --no-recursion -T - -cf -
        ) | tar -x -C "$work/tree"
    else
        git -C "$repo" archive "$rev" | tar -x -C "$work/tree"
    fi
}

add_consec_placeholders() {
    local worker="$work/tree/src/AsteriaScale.RankerWorker"
    local models="$worker/Models"
    local consec="$models/Consec"
    local placeholders=(
        "$consec/tokenizer.json"
        "$consec/model-manifest.json"
        "$consec/Cpu/consec-wngt-fp32.onnx"
        "$consec/Cpu/consec-wngt-fp32.onnx.data"
    )
    local licensed link="" candidate
    # mkdir -p and the ": >" writes below follow symlinks, so a link on their
    # way would truncate whatever it points at, the real bundle included;
    # find does not follow links, so the payload tripwire cannot see through
    # one either. Checked before anything is written.
    for candidate in "$work/tree/src" "$worker" "$models" "$consec" "$consec/Cpu" "${placeholders[@]}"; do
        if [[ -L $candidate ]]; then
            link=$candidate
            break
        fi
    done
    if [[ -z $link && -d $models ]]; then
        link=$(find "$models" -type l -print -quit)
    fi
    if [[ -n $link ]]; then
        echo "check-windows-identity: export contains a symlink on the ConSeC placeholder paths" \
            "or under Models ($link); refusing to build" >&2
        # rm -rf removes the links themselves and never what they point at.
        rm -rf "$work/tree"
        exit 2
    fi
    # The exports never contain ignored files; this is the tripwire in case an
    # ignore rule or a tracked file ever changes that.
    licensed=$(find "$work/tree" -type f -size +0c \
        \( -path "$consec/*" -o -name 'consec-wngt-fp32.onnx*' -o -path "$work/tree/ConSeC_WNGT/*" \) \
        -print -quit)
    if [[ -n $licensed ]]; then
        echo "check-windows-identity: export contains ConSeC payload ($licensed); refusing to build" >&2
        # The kept work folder must not keep a licensed copy either.
        rm -rf "$work/tree"
        exit 2
    fi
    mkdir -p "$consec/Cpu"
    : >"$consec/tokenizer.json"
    : >"$consec/model-manifest.json"
    : >"$consec/Cpu/consec-wngt-fp32.onnx"
    : >"$consec/Cpu/consec-wngt-fp32.onnx.data"
}

build_side() {
    local label=$1 rev=$2
    echo "[$label] exporting"
    export_tree "$rev"
    add_consec_placeholders
    mkdir -p "$work/$label"

    echo "[$label] publishing the ranker worker (win-x64)"
    # scripts/build.ps1 publish arguments, plus EnableWindowsTargeting and a
    # fixed SourceRevisionId: an export has no .git, and both sides must embed
    # the same informational version.
    run_logged "$work/$label-worker.log" \
        dotnet publish "$work/tree/src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj" \
        -c Release -r win-x64 --self-contained true \
        -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
        -p:EnableWindowsTargeting=true -p:SourceRevisionId="$base_sha" \
        --disable-build-servers \
        -o "$work/$label/Ranker"

    local tests_project="$work/tree/tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj"
    local tests_args=(
        -c Release
        -p:EnableWindowsTargeting=true -p:AppxGeneratePriEnabled=false
        -p:DebugType=None -p:DebugSymbols=false -p:SourceRevisionId="$base_sha"
        --disable-build-servers
    )

    echo "[$label] building the Windows smoke suite (generator outputs)"
    # The first build in a fresh export compiles WPF, Core and Tests, so every
    # source generator runs and EmitCompilerGeneratedFiles writes its output
    # under each project's obj/.../generated.
    run_logged "$work/$label-tests-generated.log" \
        dotnet build "$tests_project" "${tests_args[@]}" -p:EmitCompilerGeneratedFiles=true
    local generated relative
    while IFS= read -r -d '' generated; do
        relative=${generated#"$work/tree/"}
        mkdir -p "$(dirname "$work/$label-generated/$relative")"
        # A sorted list of whitespace-separated tokens: reordering the
        # lookup chain moves "if (" and "||" between entries, so sorted lines
        # would still differ, while any added, removed or renamed entry does not
        # survive this normalization.
        tr -s '[:space:]' '\n' <"$generated" | LC_ALL=C sort >"$work/$label-generated/$relative"
    done < <(find "$work/tree" -path '*/obj/*/generated/*' -type f -print0)
    if [[ ! -d $work/$label-generated ]]; then
        echo "check-windows-identity: [$label] no source generator output was recorded" >&2
        exit 1
    fi
    (cd "$work/$label-generated" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256) \
        >"$work/$label-generated.sha256"

    echo "[$label] rebuilding the Windows smoke suite (reproducible)"
    run_logged "$work/$label-tests.log" \
        dotnet build "$tests_project" "${tests_args[@]}" --no-incremental \
        -p:CsWinRTCcwLookupTableGeneratorEnabled=false

    cp -R "$work/tree/tests/AsteriaScale.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64" \
        "$work/$label/Tests"

    (cd "$work/$label" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256) \
        >"$work/$label.sha256"

    # An empty or partial output would compare equal to another empty one.
    local required
    for required in ./Ranker/AsteriaScale.RankerWorker.dll ./Ranker/onnxruntime.dll ./Tests/AsteriaScale.Tests.dll; do
        if ! awk -v want="$required" '$2 == want { found = 1 } END { exit !found }' "$work/$label.sha256"; then
            echo "check-windows-identity: [$label] output lacks $required" >&2
            exit 1
        fi
    done
    echo "[$label] $(wc -l <"$work/$label.sha256" | tr -d ' ') files," \
        "$(wc -l <"$work/$label-generated.sha256" | tr -d ' ') generated sources"
}

build_side base "$base_sha"
build_side candidate "$candidate_rev"

for key in ./Ranker/AsteriaScale.RankerWorker.dll ./Ranker/onnxruntime.dll ./Tests/AsteriaScale.Tests.dll ./Tests/AsteriaScale.Core.dll; do
    awk -v want="$key" '$2 == want { print "candidate: " $0 }' "$work/candidate.sha256"
done

same=1
diff -u "$work/base-generated.sha256" "$work/candidate-generated.sha256" || same=0
diff -u "$work/base.sha256" "$work/candidate.sha256" || same=0

finished=1
if [[ $same -eq 1 ]]; then
    count=$(wc -l <"$work/base.sha256" | tr -d ' ')
    generated_count=$(wc -l <"$work/base-generated.sha256" | tr -d ' ')
    rm -rf "$work"
    echo "generated sources: $generated_count identical with order ignored"
    echo "IDENTICAL: $count Windows output files"
    exit 0
fi

echo "DIFFERENT: Windows outputs changed; work folder kept at $work" >&2
exit 1
