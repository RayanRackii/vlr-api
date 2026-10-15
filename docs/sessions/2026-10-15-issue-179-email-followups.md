# 2026-10-15 — email review follow-ups (#179 a–d)

Branch `fix/issue-179-email-review-followups` in `vlr-api`, from `develop`. Draft PR. No migration, no deploy, no new environment variable.

Invite subjects, reservation subjects, and the From display name drop control characters and Unicode line separators (U+2028, U+2029, U+0085). `DevEmailProvider` logs the recipient and subject only. Agenda links reject `.`, `..`, and any subdomain label outside the admin rule. Item (e), the agenda page reading `?reservation=`, stays in `vlr-web`.
