#!/usr/bin/env bash
# Rebuild the OP-BENCH Results page from recorded runs, refresh the README
# thumbnails, and (with --publish) push it to GitHub Pages.
#
#   scripts/bench-page.sh             build benchmarks/site/index.html and the thumbnails
#   scripts/bench-page.sh --publish   also commit the page to gh-pages and push it
#
# To add a result: put its run ID in benchmarks/report-runs.json, then run this.
# Nothing on the page is typed by hand; every number comes from the run's result.md.
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
cd "$root"

publish=false
case "${1:-}" in
  --publish) publish=true ;;
  "") ;;
  *) echo "usage: scripts/bench-page.sh [--publish]" >&2; exit 2 ;;
esac

site="benchmarks/site"
thumbs="docs/design/op-bench-report"
chrome="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"

.venv/bin/python -m pytest -q benchmarks/test_bench_page.py
.venv/bin/python benchmarks/bench_page.py --out "$site/index.html"
touch "$site/.nojekyll"

# Thumbnails of the first view, light and dark, for the README
if [[ -x "$chrome" ]]; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  for theme in light dark; do
    scheme=1; [[ $theme == dark ]] && scheme=0
    "$chrome" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=2 \
      --window-size=1440,830 --virtual-time-budget=5000 \
      --blink-settings=preferredColorScheme=$scheme --user-data-dir="$tmp/profile-$theme" \
      --screenshot="$tmp/$theme.png" "file://$root/$site/index.html" >/dev/null 2>&1
    sips -Z 1600 "$tmp/$theme.png" --out "$thumbs/preview-$theme.png" >/dev/null
  done
  echo "thumbnails: $thumbs/preview-light.png, $thumbs/preview-dark.png"
else
  echo "Chrome not found at $chrome; thumbnails not refreshed (set CHROME=...)" >&2
fi

if ! $publish; then
  echo "built $site/index.html; open it to check, then rerun with --publish"
  exit 0
fi

# Publish: commit the built page to gh-pages through a temporary worktree
head="$(git rev-parse --short=7 HEAD)"
wt="$(mktemp -d)/gh-pages"
git fetch -q origin gh-pages
git worktree add -q "$wt" gh-pages
trap 'git -C "$root" worktree remove --force "$wt" >/dev/null 2>&1 || true' EXIT
git -C "$wt" merge -q --ff-only origin/gh-pages
cp "$site/index.html" "$site/.nojekyll" "$wt/"
if git -C "$wt" diff --quiet && [[ -z "$(git -C "$wt" status --porcelain)" ]]; then
  echo "gh-pages already up to date"
  exit 0
fi
git -C "$wt" add index.html .nojekyll
git -C "$wt" -c user.name="$(git config user.name)" -c user.email="$(git config user.email)" \
  commit -q -m "docs: rebuild the OP-BENCH Results page at $head"
git -C "$wt" push -q origin gh-pages
echo "published: https://egannguyen.github.io/Open-Payments/ (gh-pages $(git -C "$wt" rev-parse --short HEAD))"
echo "commit the refreshed thumbnails in $thumbs with your next main commit"
