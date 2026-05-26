#!/usr/bin/env bash
# Hand-labeling helper for evals/labels/human_tiers.json (task 16).
# Walks the 20-packet stratified set, opening each packet's PDFs in
# Preview and pausing for you to record the tier in human_tiers.json.
#
# Discipline reminders:
#   - Rate from the PDFs only. Do NOT open golden.json (anchors you to
#     planted answers).
#   - Today = 2026-05-26 for expiry judgment.
#   - Red = critical blocker; Yellow = significant issue; Green = ready.
#   - Save human_tiers.json as you go; commit ONLY after all 20 are filled
#     so its mtime is the final-fill timestamp.

set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
DATASET="$REPO_ROOT/evals/dataset"
LABELS="$REPO_ROOT/evals/labels/human_tiers.json"

PACKETS=(
  packet-001-clean-anderson
  packet-005-scanned-anderson
  packet-006-clean-perry
  packet-008-clean-lopez
  packet-010-clean-berry
  packet-014-clean-barker
  packet-015-clean-hall
  packet-017-clean-flores
  packet-019-clean-conflict-name-foster
  packet-021-clean-conflict-name-guzman
  packet-023-clean-conflict-name-cummings
  packet-025-clean-conflict-name-bartlett
  packet-026-clean-conflict-taxonomy-calhoun
  packet-028-clean-conflict-taxonomy-wade
  packet-030-clean-conflict-taxonomy-oliver
  packet-033-scanned-rice
  packet-035-scanned-ferguson
  packet-040-scanned-tucker
  packet-046-scanned-conflict-name-blanchard
  packet-048-scanned-conflict-name-patel
)

total=${#PACKETS[@]}
i=1
for pkt in "${PACKETS[@]}"; do
  echo
  echo "============================================================"
  echo " [$i/$total] $pkt"
  echo "============================================================"
  echo " Cross-check across the 4 PDFs:"
  echo "   1. Name matches on all docs?"
  echo "   2. All expiry dates >= 2026-05-26 (+30 day buffer for Yellow)?"
  echo "   3. License taxonomy code matches board-cert specialty?"
  echo
  echo " Open packet dir: $DATASET/$pkt"
  open "$DATASET/$pkt"/*.pdf
  read -r -p " Tier recorded in human_tiers.json. Press Enter for next... " _
  i=$((i + 1))
done

echo
echo "All 20 packets shown. Verify human_tiers.json has no empty strings,"
echo "then commit:"
echo "  git add $LABELS"
echo "  git commit -m 'feat(evals): hand-label 20 packets for tier agreement (task 16)'"
