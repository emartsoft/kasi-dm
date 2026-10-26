# ADR 0036 — Speed profiles and scheduled bandwidth

- **Status:** Accepted
- **Date:** 2026-10-26
- **Requirement:** KASI-BWL-001, KASI-BWL-003; TC-BWL-001, TC-BWL-002
- **Context:** A schedule can start and stop a queue, but it cannot change the global bandwidth limit. The tray and
  toolbar only offer fixed presets, and a restart inside a schedule window has no record of the profile that should
  be active.

## Decision

- A speed profile is a named limit in bytes per second or unlimited. It is stored in `speed_profile`; a schedule
  references one through `queue_schedule.profile_id`.
- The existing global token bucket remains the only throttle. Applying a profile sets the same `SpeedLimit` the tray
  already uses; there is no second bucket and no per-profile engine.
- The scheduler applies the profile when a scheduled window opens and records the profile id in the existing
  `setting` table. On startup the scheduler evaluates the current window again, so a restart inside the window
  restores the profile without a separate replay log.
- When the window closes, the scheduler clears the limit only if the active profile is the one that schedule applied.
  A manual limit chosen during the window is therefore not overwritten every tick; it is replaced at the next window
  boundary.
- The Settings window owns profile CRUD. The Scheduler window chooses a profile for its schedule. The tray and toolbar
  presets include the named profiles after the app loads them.
- Profile names are unique. A blank name is rejected in the UI; the database rejects duplicate names. Zero MB/s in the
  editor means unlimited.

## Consequences

- Profiles are data, not code: future schedulers can reuse them without changing the engine.
- The active profile is visible to export/import through the existing settings table.
- Multiple queues with overlapping windows share one global limit. The last window that opens applies its profile;
  the plan does not define a per-queue speed profile, and this ADR keeps that scope.
