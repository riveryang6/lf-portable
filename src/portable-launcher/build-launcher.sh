#!/usr/bin/env bash
# Build the Windows bootstrapper and launcher cores from WSL.

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: build-launcher.sh --output-root DIR [--bootstrapper-output FILE]
       [--dotnet PATH] [--framework-dir DIR]
       [--payload-archive ZIP --bundle-output EXE]

Builds:
  DIR/CodexData/tools/launchers/CodexPortable.x86.exe
  DIR/CodexData/tools/launchers/CodexPortable.x64.exe
  DIR/CodexData/tools/launchers/CodexPortable.arm64.exe
  BOOTSTRAPPER_FILE (outside DIR; defaults to ../build/CodexPortable.bootstrapper.exe)

When --payload-archive and --bundle-output are supplied together, also appends
the prepared CodexData payload ZIP to the x86 bootstrapper. The resulting EXE
is a self-extracting, architecture-specific portable release.
EOF
}

fail() {
    printf 'build-launcher.sh: %s\n' "$*" >&2
    exit 1
}

require_file() {
    local path=$1
    [[ -f "$path" ]] || fail "Required file is missing: $path"
}

find_roslyn_compiler() {
    local line version sdk_base candidate
    while IFS= read -r line; do
        if [[ $line =~ ^([^[:space:]]+)[[:space:]]+\[([^]]+)\]$ ]]; then
            version=${BASH_REMATCH[1]}
            sdk_base=${BASH_REMATCH[2]}
            candidate="$sdk_base/$version/Roslyn/bincore/csc.dll"
            if [[ -f "$candidate" ]]; then
                printf '%s\n' "$candidate"
            fi
        fi
    done < <("$dotnet_path" --list-sdks)
}

build_target() {
    local label=$1
    local source=$2
    local platform=$3
    local output=$4
    local -a compiler_args

    mkdir -p -- "$(dirname -- "$output")"
    compiler_args=(
        /nologo
        /noconfig
        /nostdlib+
        /langversion:5
        /codepage:65001
        /target:winexe
        "/platform:$platform"
        /optimize+
        /debug-
        /warn:4
        /warnaserror+
        "/out:$output"
        "/win32icon:$icon_path"
        "/win32manifest:$manifest_path"
        "/resource:$tray_dark_path,CodexPortable.Branding.TrayDark.ico"
        "/resource:$tray_light_path,CodexPortable.Branding.TrayLight.ico"
    )
    if [[ $source == "$core_source" ]]; then
        compiler_args+=("/resource:$fallback_prompt_path,CodexPortable.ModelFallbackPrompt.txt")
        compiler_args+=("/resource:$pi_cache_path,CodexPortable.PiModelsCache.json")
    fi
    local reference
    for reference in "${references[@]}"; do
        compiler_args+=("/reference:$reference")
    done
    compiler_args+=("$source")

    printf 'Building %s...\n' "$label"
    "$dotnet_path" "$csc_path" "${compiler_args[@]}"
}

output_root=''
bootstrapper_output=''
dotnet_path=''
framework_dir=''
payload_archive=''
bundle_output=''
while [[ $# -gt 0 ]]; do
    case $1 in
        --output-root)
            [[ $# -ge 2 ]] || fail '--output-root requires a directory'
            output_root=$2
            shift 2
            ;;
        --bootstrapper-output)
            [[ $# -ge 2 ]] || fail '--bootstrapper-output requires a file'
            bootstrapper_output=$2
            shift 2
            ;;
        --dotnet)
            [[ $# -ge 2 ]] || fail '--dotnet requires a path'
            dotnet_path=$2
            shift 2
            ;;
        --framework-dir)
            [[ $# -ge 2 ]] || fail '--framework-dir requires a directory'
            framework_dir=$2
            shift 2
            ;;
        --payload-archive)
            [[ $# -ge 2 ]] || fail '--payload-archive requires a ZIP path'
            payload_archive=$2
            shift 2
            ;;
        --bundle-output)
            [[ $# -ge 2 ]] || fail '--bundle-output requires an EXE path'
            bundle_output=$2
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            usage >&2
            fail "Unknown argument: $1"
            ;;
    esac
done

[[ -n $output_root ]] || {
    usage >&2
    fail '--output-root is required'
}
if [[ -n $payload_archive || -n $bundle_output ]]; then
    [[ -n $payload_archive && -n $bundle_output ]] ||
        fail '--payload-archive and --bundle-output must be supplied together'
    require_file "$payload_archive"
fi

if [[ -z $dotnet_path ]]; then
    dotnet_path=$(command -v dotnet || true)
fi
[[ -n $dotnet_path && -x $dotnet_path ]] || fail 'A usable dotnet executable is required'

if [[ -z $framework_dir ]]; then
    framework_dir=/usr/lib/mono/4.8-api
fi
[[ -d $framework_dir ]] || fail "Framework reference directory is missing: $framework_dir"

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
mkdir -p -- "$output_root"
output_root=$(cd -- "$output_root" && pwd -P)
framework_dir=$(cd -- "$framework_dir" && pwd -P)

if [[ -z $bootstrapper_output ]]; then
    bootstrapper_output="$(dirname -- "$output_root")/build/CodexPortable.bootstrapper.exe"
fi
bootstrapper_parent=$(dirname -- "$bootstrapper_output")
mkdir -p -- "$bootstrapper_parent"
bootstrapper_output="$(cd -- "$bootstrapper_parent" && pwd -P)/$(basename -- "$bootstrapper_output")"
case "$bootstrapper_output" in
    "$output_root"|"$output_root"/*)
        fail 'bootstrapper output must be outside the launcher output root'
        ;;
esac

core_source="$script_dir/CodexPortable.cs"
bootstrap_source="$script_dir/CodexPortableBootstrap.cs"
icon_path="$script_dir/codex.ico"
tray_dark_path="$script_dir/codex-tray-dark.ico"
tray_light_path="$script_dir/codex-tray-light.ico"
manifest_path="$script_dir/CodexPortable.manifest"
fallback_prompt_path="$script_dir/CodexModelFallbackPrompt.txt"
pi_cache_path="$script_dir/pi-models-cache.json"
for input_path in "$core_source" "$bootstrap_source" "$icon_path" "$tray_dark_path" \
    "$tray_light_path" "$manifest_path" "$fallback_prompt_path" "$pi_cache_path"; do
    require_file "$input_path"
done

mapfile -t csc_candidates < <(find_roslyn_compiler)
[[ ${#csc_candidates[@]} -gt 0 ]] || fail 'Could not find Roslyn csc.dll from dotnet --list-sdks'
csc_path=${csc_candidates[$((${#csc_candidates[@]} - 1))]}

reference_names=(
    mscorlib.dll
    System.dll
    System.Core.dll
    System.Drawing.dll
    System.IO.Compression.dll
    System.Web.Extensions.dll
    System.Windows.Forms.dll
    System.Xml.dll
)
references=()
for reference_name in "${reference_names[@]}"; do
    reference="$framework_dir/$reference_name"
    require_file "$reference"
    references+=("$reference")
done

build_log_dir=$(mktemp -d)
build_pids=()
build_logs=()

schedule_build() {
    local label=$1
    local log=$2
    shift 2
    {
        build_target "$label" "$@"
    } >"$log" 2>&1 &
    build_pids+=("$!")
    build_logs+=("$log")
}

schedule_build 'x86 bootstrapper' "$build_log_dir/x86-bootstrapper.log" \
    "$bootstrap_source" x86 "$bootstrapper_output"
schedule_build 'x86 launcher' "$build_log_dir/x86-launcher.log" \
    "$core_source" x86 "$output_root/CodexData/tools/launchers/CodexPortable.x86.exe"
schedule_build 'x64 launcher' "$build_log_dir/x64-launcher.log" \
    "$core_source" x64 "$output_root/CodexData/tools/launchers/CodexPortable.x64.exe"
schedule_build 'ARM64 launcher' "$build_log_dir/arm64-launcher.log" \
    "$core_source" arm64 "$output_root/CodexData/tools/launchers/CodexPortable.arm64.exe"

build_failed=0
for ((build_index = 0; build_index < ${#build_pids[@]}; build_index++)); do
    if ! wait "${build_pids[$build_index]}"; then
        echo "Build failed: ${build_logs[$build_index]}" >&2
        tail -n 40 "${build_logs[$build_index]}" >&2 || true
        build_failed=1
    fi
done
rm -rf -- "$build_log_dir"
if [[ $build_failed -ne 0 ]]; then
    exit 1
fi

printf 'Built launcher outputs under %s\n' "$output_root"
printf 'Bootstrapper input: %s (outside launcher output; release.py embeds it).\n' \
    "$bootstrapper_output"

if [[ -n $payload_archive ]]; then
    payload_archive=$(cd -- "$(dirname -- "$payload_archive")" && pwd -P)/$(basename -- "$payload_archive")
    bundle_parent=$(dirname -- "$bundle_output")
    mkdir -p -- "$bundle_parent"
    bundle_parent=$(cd -- "$bundle_parent" && pwd -P)
    bundle_output="$bundle_parent/$(basename -- "$bundle_output")"
    [[ ! -e $bundle_output ]] || fail "Bundle output already exists: $bundle_output"
    if ! cp -- "$bootstrapper_output" "$bundle_output" ||
        ! dd if="$payload_archive" of="$bundle_output" bs=4M iflag=fullblock \
            oflag=append conv=notrunc status=none; then
        rm -f -- "$bundle_output"
        fail "Could not create bundled executable: $bundle_output"
    fi
    printf 'Built bundled executable at %s\n' "$bundle_output"
fi
