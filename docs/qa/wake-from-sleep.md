# Wake from sleep — hardware evidence (KASI-QUE-005 / TC-QUE-005)

This case is manual by design: it needs a physical laptop, a real power plan and a real sleep state. The unit suite
proves the timer/fake and schedule arithmetic; this file records what only the hardware can show.

## Owner laptops

Three laptops are available. All three are expected to share one sleep type (modern standby or S3); `powercfg /a`
states which. The other sleep type is **untested** and stays on the release checklist.

| Laptop | Model | Windows build | `powercfg /a` sleep type | Wake timers setting | Sleep run | Hibernate run | Lid-close run | Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 |  |  |  |  |  |  |  |  |
| 2 |  |  |  |  |  |  |  |  |
| 3 |  |  |  |  |  |  |  |  |

## Per-laptop procedure

1. Note the model, Windows build and `powercfg /a` output.
2. In Power Options, record whether “Allow wake timers” is on for the active plan (AC and battery).
3. Schedule a queue start two minutes ahead.
4. Put the laptop to sleep; confirm it wakes and starts the queue.
5. Repeat with hibernate.
6. Repeat with the lid closed.
7. Record pass/fail and the observed time; a wake that starts late is a fail for TC-QUE-005.

## Evidence rules

- Do not paste URLs, cookies or user paths into this file.
- A run counts only from the gate build; a developer build is exploratory.
- If the three machines are all one sleep type, write that explicitly and keep the other type as **untested**.
- KASI-QUE-005 is marked verified only when this table has the three rows filled in and the untested type is noted.
