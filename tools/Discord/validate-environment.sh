#!/usr/bin/env bash
# Never echo secret values: names alone identify invalid deployment inputs.
validate_discord_environment() {
  local prefix="$1" name value
  local LC_ALL=C
  while IFS= read -r name; do
    value="${!name}"
    value="${value#$'\xef\xbb\xbf'}"
    if [[ -n "$value" && ! "$value" =~ ^[!-~]+$ ]]; then
      echo "::error::discord_configuration_invalid: $name must contain only printable ASCII without whitespace." >&2
      return 1
    fi
    printf -v "$name" '%s' "$value"
    export "$name"
  done < <(compgen -A variable "$prefix")
}
