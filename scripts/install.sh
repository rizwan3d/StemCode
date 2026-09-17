#!/usr/bin/env bash
set -euo pipefail

readonly OWNER="rizwan3d"
readonly REPO="StemCode"
readonly APP_NAME="StemCode.CLI"
readonly VOICE_APP_NAME="StemCode.Voice"
readonly EXECUTABLE_NAME="StemCode.CLI"
readonly VOICE_EXECUTABLE_NAME="stemcode-voice"
# Installed command name: '/update' sets StemCode_COMMAND_NAME so the running
# binary's filename is preserved when replacing it in place.
readonly COMMAND_NAME="${STEMCODE_COMMAND_NAME:-${StemCode_COMMAND_NAME:-stemcode}}"
readonly CHECKSUMS_NAME="SHA256SUMS"
readonly POSIX_DEFAULT_INSTALL_DIR="${HOME}/.local/bin"
readonly TOTAL_STEPS=7

# Anonymous install analytics. Mirrors the in-product PostHog defaults so installs
# and usage land in the same project. Opt out with STEMCODE_TELEMETRY_DISABLED=1
# or the cross-tool DO_NOT_TRACK convention.
readonly TELEMETRY_HOST="https://us.i.posthog.com"
readonly TELEMETRY_PROJECT_TOKEN="phc_AKZFSyU239kkQ5GQ2y4idb8MtFX96kVekgezgnsELHRk"
readonly TELEMETRY_EVENT="cli installed"

TEMP_ROOT=""
CURRENT_STEP=0
COMMAND_AVAILABLE_SCOPE="current"

cleanup() {
  if [[ -n "${TEMP_ROOT:-}" && -d "$TEMP_ROOT" ]]; then
    rm -rf "$TEMP_ROOT"
  fi
}

trap cleanup EXIT

log() {
  # Important: logs must go to stderr so command substitution captures only data.
  printf '[%s] %s\n' "$APP_NAME" "$1" >&2
}

fail() {
  printf '[%s] Error: %s\n' "$APP_NAME" "$1" >&2
  exit 1
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    fail "Required command '$1' is not available."
  fi
}

progress_enabled() {
  local value="${STEMCODE_NO_PROGRESS:-${StemCode_NO_PROGRESS:-}}"

  case "$value" in
    1|true|TRUE|True|yes|YES|Yes)
      return 1
      ;;
    *)
      [[ -t 2 ]]
      ;;
  esac
}

is_windows_platform() {
  [[ "$1" == win-* ]]
}

start_step() {
  CURRENT_STEP=$((CURRENT_STEP + 1))
  log "[$CURRENT_STEP/$TOTAL_STEPS] $1"
}

finish_step() {
  log "    $1"
}

to_posix_path() {
  if ! command -v cygpath >/dev/null 2>&1; then
    fail "Windows installs from bash require 'cygpath' to translate paths."
  fi

  cygpath -au "$1"
}

to_windows_path() {
  if ! command -v cygpath >/dev/null 2>&1; then
    fail "Windows installs from bash require 'cygpath' to translate paths."
  fi

  cygpath -aw "$1"
}

resolve_default_install_dir() {
  local platform="$1"
  local local_app_data

  if ! is_windows_platform "$platform"; then
    printf '%s\n' "$POSIX_DEFAULT_INSTALL_DIR"
    return
  fi

  local_app_data="${LOCALAPPDATA:-${LocalAppData:-}}"
  if [[ -z "$local_app_data" && -n "${USERPROFILE:-}" ]]; then
    local_app_data="${USERPROFILE}\\AppData\\Local"
  fi

  if [[ -z "$local_app_data" ]]; then
    fail "Unable to determine LOCALAPPDATA for the default Windows install directory. Set STEMCODE_INSTALL_DIR and try again."
  fi

  to_posix_path "${local_app_data}\\Programs\\StemCode\\bin"
}

normalize_install_dir() {
  local platform="$1"
  local install_dir="$2"

  if is_windows_platform "$platform"; then
    case "$install_dir" in
      [A-Za-z]:[\\/]*|\\\\*)
        to_posix_path "$install_dir"
        return
        ;;
    esac
  fi

  printf '%s\n' "$install_dir"
}

resolve_archive_executable_name() {
  local platform="$1"

  if is_windows_platform "$platform"; then
    printf '%s.exe\n' "$EXECUTABLE_NAME"
  else
    printf '%s\n' "$EXECUTABLE_NAME"
  fi
}

resolve_voice_archive_executable_name() {
  local platform="$1"

  if is_windows_platform "$platform"; then
    printf '%s.exe\n' "$VOICE_EXECUTABLE_NAME"
  else
    printf '%s\n' "$VOICE_EXECUTABLE_NAME"
  fi
}

resolve_destination_file_name() {
  local platform="$1"

  if is_windows_platform "$platform"; then
    case "$COMMAND_NAME" in
      *.exe|*.EXE)
        printf '%s\n' "$COMMAND_NAME"
        ;;
      *)
        printf '%s.exe\n' "$COMMAND_NAME"
        ;;
    esac
  else
    printf '%s\n' "$COMMAND_NAME"
  fi
}

format_bytes() {
  local bytes="$1"

  awk -v bytes="$bytes" '
    BEGIN {
      split("B KiB MiB GiB", units, " ")
      value = bytes + 0
      unit = 1

      while (value >= 1024 && unit < 4) {
        value = value / 1024
        unit++
      }

      if (unit == 1) {
        printf "%d %s", value, units[unit]
      } else {
        printf "%.1f %s", value, units[unit]
      }
    }
  '
}

file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

download_to_file() {
  local url="$1"
  local destination="$2"
  local show_progress="${3:-0}"

  if command -v curl >/dev/null 2>&1; then
    local curl_args=(
      -fL
      -H "User-Agent: ${APP_NAME}-installer"
      --retry 3
      --retry-delay 2
      --connect-timeout 15
      -o "$destination"
    )

    if [[ "$show_progress" == "1" ]] && progress_enabled; then
      curl_args+=(--progress-bar)
    else
      curl_args+=(-sS)
    fi

    curl "${curl_args[@]}" "$url"
    return
  fi

  if command -v wget >/dev/null 2>&1; then
    local wget_args=(
      --header="User-Agent: ${APP_NAME}-installer"
      -O "$destination"
    )

    if [[ "$show_progress" == "1" ]] &&
      progress_enabled &&
      wget --help 2>&1 | grep -q -- '--show-progress'; then
      wget_args+=(--show-progress --progress=bar:force)
    else
      wget_args+=(-q)
    fi

    wget "${wget_args[@]}" "$url"
    return
  fi

  fail "Neither curl nor wget is available. Install one of them and try again."
}

compute_sha256() {
  local path="$1"

  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$path" | awk '{ print tolower($1) }'
    return
  fi

  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$path" | awk '{ print tolower($1) }'
    return
  fi

  if command -v openssl >/dev/null 2>&1; then
    openssl dgst -sha256 -r "$path" | awk '{ print tolower($1) }'
    return
  fi

  fail "No SHA256 checksum tool is available. Install sha256sum, shasum, or openssl and try again."
}

read_expected_sha256() {
  local asset_name="$1"
  local checksums_path="$2"

  awk -v name="$asset_name" '
    {
      hash = $1
      file = $2
      sub(/^\*/, "", file)
      sub(/^\.\//, "", file)

      if (file == name) {
        print hash
        exit
      }
    }
  ' "$checksums_path"
}

read_release_asset_sha256() {
  local asset_name="$1"
  local metadata_path="$2"
  local escaped_asset_name

  escaped_asset_name="$(printf '%s\n' "$asset_name" | sed 's/[.[\*^$\\]/\\&/g')"

  sed 's/},{/}\
{/g' "$metadata_path" |
    sed -n "/\"name\":\"${escaped_asset_name}\"/s/.*\"digest\":\"sha256:\([0-9A-Fa-f]\{64\}\)\".*/\1/p" |
    head -n 1
}

resolve_release_asset_sha256() {
  local tag="$1"
  local asset_name="$2"
  local metadata_url="https://api.github.com/repos/${OWNER}/${REPO}/releases/tags/${tag}"
  local metadata_path="${TEMP_ROOT}/release-metadata.json"
  local digest

  if ! download_to_file "$metadata_url" "$metadata_path"; then
    return 1
  fi

  digest="$(read_release_asset_sha256 "$asset_name" "$metadata_path" | tr '[:upper:]' '[:lower:]')"
  if [[ -z "$digest" ]]; then
    return 1
  fi

  printf '%s\n' "$digest"
}

verify_archive_sha256() {
  local tag="$1"
  local asset_name="$2"
  local archive_path="$3"
  local checksums_url="https://github.com/${OWNER}/${REPO}/releases/download/${tag}/${CHECKSUMS_NAME}"
  local checksums_path="${TEMP_ROOT}/${CHECKSUMS_NAME}"
  local expected_sha256
  local actual_sha256

  log "Downloading ${CHECKSUMS_NAME}..."
  if ! download_to_file "$checksums_url" "$checksums_path"; then
    expected_sha256="$(resolve_release_asset_sha256 "$tag" "$asset_name" || true)"

    if [[ -z "$expected_sha256" ]]; then
      fail "Unable to download ${CHECKSUMS_NAME} from ${checksums_url}, and no GitHub release metadata digest was found. Checksum verification is mandatory."
    fi

    log "Using SHA256 digest from GitHub release metadata for ${asset_name}."
  else
    expected_sha256="$(read_expected_sha256 "$asset_name" "$checksums_path" | tr '[:upper:]' '[:lower:]')"
  fi

  if [[ -z "$expected_sha256" ]]; then
    fail "${CHECKSUMS_NAME} does not contain a checksum for ${asset_name}."
  fi

  if ! printf '%s\n' "$expected_sha256" | grep -Eq '^[0-9a-f]{64}$'; then
    fail "${CHECKSUMS_NAME} contains an invalid SHA256 checksum for ${asset_name}."
  fi

  actual_sha256="$(compute_sha256 "$archive_path")"
  if [[ "$actual_sha256" != "$expected_sha256" ]]; then
    fail "SHA256 verification failed for ${asset_name}. Expected ${expected_sha256}, got ${actual_sha256}."
  fi

  log "Verified SHA256 checksum for ${asset_name}."
}

resolve_latest_tag() {
  local api_url="https://api.github.com/repos/${OWNER}/${REPO}/releases/latest"
  local metadata
  local tag

  log "Resolving the latest release tag..."
  metadata="$(mktemp)"

  if ! download_to_file "$api_url" "$metadata"; then
    rm -f "$metadata"
    fail "Unable to determine the latest release tag from GitHub. Set STEMCODE_TAG and try again."
  fi

  tag="$(sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$metadata" | head -n 1)"
  rm -f "$metadata"

  if [[ -z "$tag" ]]; then
    fail "Unable to determine the latest release tag from GitHub. Set STEMCODE_TAG and try again."
  fi

  printf '%s\n' "$tag"
}

detect_platform() {
  local os
  local arch

  os="$(uname -s)"
  arch="$(uname -m)"

  case "$os" in
    Linux)
      case "$arch" in
        x86_64|amd64)
          printf 'linux-x64\n'
          ;;
        aarch64|arm64)
          printf 'linux-arm64\n'
          ;;
        *)
          fail "Unsupported Linux architecture '$arch'."
          ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*)
      case "$arch" in
        x86_64|amd64)
          printf 'win-x64\n'
          ;;
        *)
          fail "Unsupported Windows architecture '$arch'. This installer supports Windows x64 only."
          ;;
      esac
      ;;
    Darwin)
      case "$arch" in
        x86_64)
          printf 'osx-x64\n'
          ;;
        arm64)
          printf 'osx-arm64\n'
          ;;
        *)
          fail "Unsupported macOS architecture '$arch'."
          ;;
      esac
      ;;
    *)
      fail "Unsupported operating system '$os'. This installer supports Windows, Linux, and macOS."
      ;;
  esac
}

path_contains_directory() {
  local path_value="${1:-}"
  local directory="$2"
  local entry
  local IFS=:

  for entry in $path_value; do
    if [[ "$entry" == "$directory" ]]; then
      return 0
    fi
  done

  return 1
}

single_quote() {
  printf "'"
  printf '%s' "$1" | sed "s/'/'\\\\''/g"
  printf "'"
}

fish_quote() {
  printf "'"
  printf '%s' "$1" | sed "s/[\\']/\\&/g"
  printf "'"
}

profile_paths_for_shell() {
  local shell_name="${SHELL##*/}"
  local os

  os="$(uname -s)"

  case "$shell_name" in
    zsh)
      printf '%s\n' "${HOME}/.zshrc"
      printf '%s\n' "${HOME}/.zprofile"
      ;;
    bash)
      printf '%s\n' "${HOME}/.bashrc"
      if [[ "$os" == "Darwin" ]]; then
        printf '%s\n' "${HOME}/.bash_profile"
      else
        printf '%s\n' "${HOME}/.profile"
      fi
      ;;
    fish)
      printf '%s\n' "${HOME}/.config/fish/config.fish"
      ;;
    *)
      printf '%s\n' "${HOME}/.profile"
      ;;
  esac
}

append_posix_path_entry() {
  local profile_path="$1"
  local install_dir="$2"
  local quoted_install_dir
  local quoted_path_match

  quoted_install_dir="$(single_quote "$install_dir")"
  quoted_path_match="$(single_quote ":${install_dir}:")"

  {
    printf '\n# Added by StemCode CLI installer\n'
    printf "if [ -d %s ] && ! printf '%%s' \":\$PATH:\" | grep -qF -- %s; then\n" "$quoted_install_dir" "$quoted_path_match"
    printf '  export PATH=%s:$PATH\n' "$quoted_install_dir"
    printf 'fi\n'
  } >> "$profile_path"
}

append_fish_path_entry() {
  local profile_path="$1"
  local install_dir="$2"
  local quoted_install_dir

  quoted_install_dir="$(fish_quote "$install_dir")"

  {
    printf '\n# Added by StemCode CLI installer\n'
    printf 'if test -d %s; and not contains -- %s $PATH\n' "$quoted_install_dir" "$quoted_install_dir"
    printf '    set -gx PATH %s $PATH\n' "$quoted_install_dir"
    printf 'end\n'
  } >> "$profile_path"
}

add_install_dir_to_shell_profiles() {
  local install_dir="$1"
  local profile_path
  local profile_dir
  local updated_profiles=()

  while IFS= read -r profile_path; do
    if [[ -z "$profile_path" ]]; then
      continue
    fi

    if [[ -f "$profile_path" ]] && grep -F -- "$install_dir" "$profile_path" >/dev/null 2>&1; then
      continue
    fi

    profile_dir="$(dirname "$profile_path")"
    mkdir -p "$profile_dir"

    if [[ "$profile_path" == */config.fish ]]; then
      append_fish_path_entry "$profile_path" "$install_dir"
    else
      append_posix_path_entry "$profile_path" "$install_dir"
    fi

    updated_profiles+=("$profile_path")
  done < <(profile_paths_for_shell)

  printf '%s\n' "${updated_profiles[@]}"
}

link_command_into_existing_path() {
  local destination_binary="$1"
  local install_dir="$2"
  local path_entry
  local path_link
  local IFS=:

  for path_entry in ${PATH:-}; do
    if [[ -z "$path_entry" || "$path_entry" != /* || "$path_entry" == "$install_dir" || "$path_entry" == */sbin ]]; then
      continue
    fi

    if [[ ! -d "$path_entry" || ! -w "$path_entry" ]]; then
      continue
    fi

    path_link="${path_entry}/${COMMAND_NAME}"
    if [[ -e "$path_link" || -L "$path_link" ]]; then
      if [[ "$path_link" -ef "$destination_binary" ]]; then
        printf '%s\n' "$path_link"
        return 0
      fi

      continue
    fi

    if ln -s "$destination_binary" "$path_link" 2>/dev/null; then
      printf '%s\n' "$path_link"
      return 0
    fi
  done

  return 1
}

add_install_dir_to_github_path() {
  local install_dir="$1"
  local platform="${2:-}"
  local github_path_dir
  local github_path_value="$install_dir"

  if [[ -z "${GITHUB_PATH:-}" ]]; then
    return 1
  fi

  if is_windows_platform "$platform"; then
    github_path_value="$(to_windows_path "$install_dir")"
  fi

  github_path_dir="$(dirname "$GITHUB_PATH")"
  if [[ ! -d "$github_path_dir" || ! -w "$github_path_dir" ]]; then
    return 1
  fi

  printf '%s\n' "$github_path_value" >> "$GITHUB_PATH"
  log "Added '${github_path_value}' to GitHub Actions PATH for later steps."
  return 0
}

windows_user_path_contains_directory() {
  local install_dir="$1"
  local install_dir_windows

  if ! command -v powershell.exe >/dev/null 2>&1; then
    return 1
  fi

  install_dir_windows="$(to_windows_path "$install_dir")"

  STEMCODE_INSTALL_DIR_WIN="$install_dir_windows" powershell.exe -NoProfile -Command "\$target = [System.IO.Path]::GetFullPath(\$env:STEMCODE_INSTALL_DIR_WIN).TrimEnd('\\'); \$current = [Environment]::GetEnvironmentVariable('Path', 'User'); if ([string]::IsNullOrWhiteSpace(\$current)) { exit 1 }; foreach (\$entry in (\$current -split ';')) { if ([string]::IsNullOrWhiteSpace(\$entry)) { continue }; try { \$candidate = [System.IO.Path]::GetFullPath(\$entry).TrimEnd('\\') } catch { continue }; if ([string]::Equals(\$candidate, \$target, [StringComparison]::OrdinalIgnoreCase)) { exit 0 } }; exit 1" >/dev/null 2>&1
}

add_install_dir_to_windows_user_path() {
  local install_dir="$1"
  local install_dir_windows

  if ! command -v powershell.exe >/dev/null 2>&1; then
    fail "Unable to update the Windows user PATH because 'powershell.exe' is not available."
  fi

  install_dir_windows="$(to_windows_path "$install_dir")"

  STEMCODE_INSTALL_DIR_WIN="$install_dir_windows" powershell.exe -NoProfile -Command "\$target = \$env:STEMCODE_INSTALL_DIR_WIN; \$current = [Environment]::GetEnvironmentVariable('Path', 'User'); if ([string]::IsNullOrWhiteSpace(\$current)) { \$newPath = \$target } else { \$newPath = \$current.TrimEnd(';') + ';' + \$target }; [Environment]::SetEnvironmentVariable('Path', \$newPath, 'User')"
}

make_command_available() {
  local destination_binary="$1"
  local install_dir="$2"
  local platform="$3"
  local linked_path
  local updated_profiles
  local profile_path

  if path_contains_directory "${PATH:-}" "$install_dir"; then
    log "The install directory is already on PATH."
    COMMAND_AVAILABLE_SCOPE="current"
    return 0
  fi

  if add_install_dir_to_github_path "$install_dir" "$platform"; then
    COMMAND_AVAILABLE_SCOPE="ci"
    return 1
  fi

  if is_windows_platform "$platform"; then
    if windows_user_path_contains_directory "$install_dir"; then
      log "The install directory is already on your user PATH."
    else
      add_install_dir_to_windows_user_path "$install_dir"
      log "Added '${install_dir}' to your user PATH."
    fi

    log "Restart your shell to use '${COMMAND_NAME}'."
    COMMAND_AVAILABLE_SCOPE="new_terminal"
    return 1
  fi

  if linked_path="$(link_command_into_existing_path "$destination_binary" "$install_dir")"; then
    log "Linked '${COMMAND_NAME}' into PATH at ${linked_path}."
    COMMAND_AVAILABLE_SCOPE="current"
    return 0
  fi

  updated_profiles="$(add_install_dir_to_shell_profiles "$install_dir")"

  if [[ -n "$updated_profiles" ]]; then
    while IFS= read -r profile_path; do
      if [[ -n "$profile_path" ]]; then
        log "Added '${install_dir}' to PATH in ${profile_path}."
      fi
    done <<< "$updated_profiles"
  else
    log "The install directory is already listed in your shell profile."
  fi

  log "Open a new terminal to use '${COMMAND_NAME}'."
  COMMAND_AVAILABLE_SCOPE="new_terminal"
  return 1
}

telemetry_enabled() {
  case "${STEMCODE_TELEMETRY_DISABLED:-${DO_NOT_TRACK:-}}" in
    1|true|TRUE|True|yes|YES|Yes|on|ON|On)
      return 1
      ;;
  esac

  [[ -n "$TELEMETRY_PROJECT_TOKEN" ]]
}

telemetry_distinct_id() {
  if command -v uuidgen >/dev/null 2>&1; then
    uuidgen | tr '[:upper:]' '[:lower:]'
  elif [[ -r /proc/sys/kernel/random/uuid ]]; then
    cat /proc/sys/kernel/random/uuid
  else
    printf 'anon-%s-%s' "$$" "${RANDOM:-0}"
  fi
}

# Best-effort anonymous "installed" event. Never fails the install: all errors are
# swallowed and the request is bounded by a short timeout.
send_install_telemetry() {
  local platform="$1"
  local tag="$2"

  if ! telemetry_enabled; then
    return 0
  fi

  local os_family
  case "$platform" in
    linux-*) os_family="linux" ;;
    osx-*) os_family="macos" ;;
    win-*) os_family="windows" ;;
    *) os_family="other" ;;
  esac

  local is_ci="false"
  local execution_environment="local"
  if [[ -n "${CI:-}" || -n "${GITHUB_ACTIONS:-}" || -n "${GITLAB_CI:-}" || -n "${BITBUCKET_BUILD_NUMBER:-}" ]]; then
    is_ci="true"
    execution_environment="ci"
  fi

  local distinct_id
  distinct_id="$(telemetry_distinct_id)"

  local payload
  payload="$(printf '{"api_key":"%s","event":"%s","distinct_id":"%s","properties":{"$lib":"installer","install_method":"install.sh","app_version":"%s","os_family":"%s","platform":"%s","app_surface":"cli","execution_environment":"%s","is_ci":%s}}' \
    "$TELEMETRY_PROJECT_TOKEN" "$TELEMETRY_EVENT" "$distinct_id" "$tag" "$os_family" "$platform" "$execution_environment" "$is_ci")"

  local endpoint="${TELEMETRY_HOST}/i/v0/e/"

  if command -v curl >/dev/null 2>&1; then
    curl -fsS -m 5 -X POST -H 'Content-Type: application/json' -d "$payload" "$endpoint" >/dev/null 2>&1 || true
  elif command -v wget >/dev/null 2>&1; then
    wget -q -T 5 -O /dev/null --header='Content-Type: application/json' --post-data="$payload" "$endpoint" >/dev/null 2>&1 || true
  fi

  return 0
}

main() {
  local requested_install_dir="${STEMCODE_INSTALL_DIR:-${StemCode_INSTALL_DIR:-}}"
  local requested_tag="${STEMCODE_TAG:-${StemCode_TAG:-${1:-}}}"
  local tag="$requested_tag"
  local platform
  local install_dir
  local asset_name
  local voice_asset_name
  local download_url
  local voice_download_url
  local archive_path
  local voice_archive_path
  local extract_dir
  local voice_extract_dir
  local source_binary
  local source_voice_binary
  local destination_binary
  local voice_destination_dir
  local source_binary_name
  local source_voice_binary_name
  local destination_file_name

  log "StemCode CLI Installer"
  start_step "Checking system requirements..."
  require_command unzip
  require_command mktemp
  require_command find

  platform="$(detect_platform)"
  if [[ -n "$requested_install_dir" ]]; then
    install_dir="$(normalize_install_dir "$platform" "$requested_install_dir")"
  else
    install_dir="$(resolve_default_install_dir "$platform")"
  fi

  finish_step "Detected ${platform}."

  start_step "Resolving release..."
  if [[ -z "$tag" ]]; then
    tag="$(resolve_latest_tag)"
  fi

  asset_name="${APP_NAME}-${platform}.zip"
  voice_asset_name="${VOICE_APP_NAME}-${platform}.zip"
  download_url="https://github.com/${OWNER}/${REPO}/releases/download/${tag}/${asset_name}"
  voice_download_url="https://github.com/${OWNER}/${REPO}/releases/download/${tag}/${voice_asset_name}"
  source_binary_name="$(resolve_archive_executable_name "$platform")"
  source_voice_binary_name="$(resolve_voice_archive_executable_name "$platform")"
  destination_file_name="$(resolve_destination_file_name "$platform")"

  finish_step "Using ${APP_NAME} and ${VOICE_APP_NAME} ${tag} for ${platform}."

  start_step "Preparing install directory..."
  log "Install directory: ${install_dir}"
  TEMP_ROOT="$(mktemp -d)"
  archive_path="${TEMP_ROOT}/${asset_name}"
  voice_archive_path="${TEMP_ROOT}/${voice_asset_name}"
  extract_dir="${TEMP_ROOT}/extract"
  voice_extract_dir="${TEMP_ROOT}/voice-extract"

  mkdir -p "$extract_dir" "$voice_extract_dir" "$install_dir"
  finish_step "Workspace ready."

  start_step "Downloading release assets..."
  if ! download_to_file "$download_url" "$archive_path" 1; then
    fail "Download failed from ${download_url}."
  fi
  if ! download_to_file "$voice_download_url" "$voice_archive_path" 1; then
    fail "Download failed from ${voice_download_url}."
  fi
  finish_step "Downloaded ${asset_name} ($(format_bytes "$(file_size "$archive_path")")) and ${voice_asset_name} ($(format_bytes "$(file_size "$voice_archive_path")"))."

  start_step "Verifying downloads..."
  verify_archive_sha256 "$tag" "$asset_name" "$archive_path"
  verify_archive_sha256 "$tag" "$voice_asset_name" "$voice_archive_path"
  finish_step "Checksum verification passed."

  start_step "Extracting archives..."
  unzip -qo "$archive_path" -d "$extract_dir"
  unzip -qo "$voice_archive_path" -d "$voice_extract_dir"

  source_binary="$(find "$extract_dir" -type f -name "$source_binary_name" | head -n 1)"
  source_voice_binary="$(find "$voice_extract_dir" -type f -name "$source_voice_binary_name" | head -n 1)"

  if [[ -z "$source_binary" || ! -f "$source_binary" ]]; then
    fail "Expected executable '${source_binary_name}' was not found in ${asset_name}."
  fi
  if [[ -z "$source_voice_binary" || ! -f "$source_voice_binary" ]]; then
    fail "Expected executable '${source_voice_binary_name}' was not found in ${voice_asset_name}."
  fi
  finish_step "Found ${source_binary_name} and ${source_voice_binary_name}."

  start_step "Installing command and Voice runtime..."
  destination_binary="${install_dir}/${destination_file_name}"
  voice_destination_dir="${install_dir}/voice"

  cp "$source_binary" "$destination_binary"
  cp -R "${extract_dir}/." "$install_dir/"
  if [[ "$source_binary" != "$destination_binary" ]]; then
    cp "${install_dir}/${source_binary_name}" "$destination_binary"
  fi
  rm -rf "$voice_destination_dir"
  mkdir -p "$voice_destination_dir"
  cp -R "${voice_extract_dir}/." "$voice_destination_dir/"

  if ! is_windows_platform "$platform"; then
    chmod 0755 "${install_dir}/${source_binary_name}"
    chmod 0755 "$destination_binary"
    chmod 0755 "${voice_destination_dir}/${source_voice_binary_name}"
  fi

  finish_step "Installed '${COMMAND_NAME}' to ${destination_binary} and Voice runtime to ${voice_destination_dir}."

  send_install_telemetry "$platform" "$tag"

  if make_command_available "$destination_binary" "$install_dir" "$platform"; then
    log "Done. Run '${COMMAND_NAME}' to start StemCode."
  elif [[ "$COMMAND_AVAILABLE_SCOPE" == "ci" ]]; then
    log "Done. '${COMMAND_NAME}' will be available in later GitHub Actions steps."
  else
    log "Done. '${COMMAND_NAME}' will be available in new terminals."
  fi
}

main "${1:-}"
