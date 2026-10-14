# 2026-10-14 — Tenant resolution follow-ups (#154, #151)

Follow-up from #153 and #150. Live Auth `app_metadata` is still the only tenant source for a Super-Admin.

#154 was a real bug. An active `core.users` row whose tenant row is gone still matched the membership check, so the resolver opened that tenant. The check now also requires the tenant row. The access-token `tenant_id` is still ignored. `IgnoreQueryFilters` stays on that one query, with the same reason as before: the session is not resolved yet, and a null ambient tenant would turn the global filter into "match every row".

#151 bounds `_epochs`. Entries older than the 10s cache TTL are dropped, and the map stops tracking new auth ids at 8192. A read still in flight cannot store into a reused epoch: Auth admin calls time out at 3s, which is shorter than the TTL. That 3s timeout is the shared HttpClient timeout and already applies to every Auth admin method, including create user and invite. Those writes did not get a longer timeout; #151 says to split one only if DEV/PROD logs show the 3s bound is too short, and this change does not read those logs.

No migration. Draft until the GLM/Fable gate.
