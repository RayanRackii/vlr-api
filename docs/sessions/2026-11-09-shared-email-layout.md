# Shared email layout — handoff (2026-11-09)

Authorization: Rayan t1377u explicitly requested Codex. Isolated existing-repo worktree `feat/shared-email-layout`, base `86b45b9d24ab57ef349014cb8dc9c2fea9b4f5fb`. No merge/deploy, live mail, env/config/secret/migration or sibling web change.

## Done
- Inventory of every platform-owned transactional mail path: invitations, recovery, customer codes, five reservation events, six catalog events, generic notifications. Web read-only check found API callers, no mail renderer/sender.
- Shared table header/footer and inline light styles; explicit accessible dark palette via media query and Outlook `[data-ogsc]`. Existing tenant name used where carried; no extra branding query or provider added.
- Explicit HTML+text delivered by Resend. Original reservation/plain notification text retained; generic data safely encoded in HTML. Content, links, recipients and routing preserved.
- 20 synthetic HTML/text pairs and index exported from production renderers to `/workspace/rolvix-synthetic-evidence/email-layout/`; desktop 1440px and mobile 390px screenshots inspected locally, including dark palette. No browser network or live app used.

## Independent verification
Standard SDK VSTest (not the author's temporary in-process runner):
- Affected Notifications + CustomerAuth + PasswordRecovery: **336 passed / 0 failed / 28 skipped = 364**.
- Full API suite after final fix: **2225 passed / 2 failed / 182 skipped = 2409**.
- The exact two failures are unchanged Havana 2026-11-01 timezone cases in `TenantTimeZoneProviderTests` and `TenantTimeZoneSettingTests`. Unmodified base 86b45b9 independently reproduces them: **15 passed / 2 failed = 17**. No out-of-scope fix or failing assertion suppressed.
- Skips require unavailable Docker/local isolated PostgreSQL; concurrency coverage not claimed. Build: **0 warnings / 0 errors**. `git diff --check` clean.
- Fresh read-only Codex high diff review found one Medium (missing explicit dark palette). Codex fixed it with contrast/palette tests. Fresh read-only delta review: **APPROVE**, no findings. Evidence under `/workspace/rolvix/rp-run/email-layout/{review,rereview}.out`.

## Reproduce / remaining gates
Render/export command in `docs/notifications/email-layout-inventory.md`; static screenshot helper in evidence directory. Gmail/Outlook device/client QA (including forced dark-mode variants) is not certified by Chromium screenshots. Canonical Claude release gate has NOT run. Full-suite baseline failures, skipped PostgreSQL coverage and CI must be addressed/accepted by the release coordinator; no merge/deploy from this task. API next: CI/release gates; web next: parent-coordinated admin batches, no email web code needed.
