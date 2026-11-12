#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/muntrade-samples.XXXXXX")"
generated_root="$temporary_root/generated"
trap 'rm -rf "$temporary_root"' EXIT

cd "$repo_root"
dotnet run --project tools/MuNTrade.SampleData -c Release -- --out "$generated_root"

hash_stream() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 | awk '{print $1}'
  else
    echo "A SHA-256 utility (sha256sum or shasum) is required." >&2
    return 1
  fi
}

verified=0
while IFS= read -r -d '' generated_file; do
  relative_path="${generated_file#"$generated_root"/}"
  committed_file="$repo_root/samples/$relative_path"
  if [[ ! -f "$committed_file" ]]; then
    echo "Missing committed sample: $relative_path" >&2
    exit 1
  fi

  if [[ "$generated_file" == *.gz ]]; then
    generated_hash="$(gzip -cd -- "$generated_file" | hash_stream)"
    committed_hash="$(gzip -cd -- "$committed_file" | hash_stream)"
    if [[ "$generated_hash" != "$committed_hash" ]]; then
      echo "Decompressed sample content differs: $relative_path" >&2
      exit 1
    fi
  elif ! cmp -s -- "$generated_file" "$committed_file"; then
    echo "Sample content differs: $relative_path" >&2
    exit 1
  fi
  verified=$((verified + 1))
done < <(find "$generated_root" -type f -print0)

echo "Verified $verified generated sample files (gzip files compared after decompression)."
