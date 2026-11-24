# Mammoth gameplay-rules test results

Tested in Unity 6000.6.6f1, scene `Level_01`, Play mode, with the in-game `MammothSelfTest` (F9 / F8 / F7) plus manual play.
Raw evidence was the `MammothTestLog.txt` that the self-test writes next to the project (not committed).

| # | Requirement | How it was tested | Result |
|---|-------------|-------------------|--------|
| 1 | A mammoth spawns in every new session | 5 consecutive Play sessions | PASS - spawned on attempt 1 each time, at different valid jungle spots: (47,-10), (30,-12), (54,-38), (39,-14), (35,-19) |
| 2 | Exactly one mammoth | F9 `FindObjectsByType<MammothAI>` | PASS - count = 1 |
| 3 | Stays inside the jungle, never in camp | 5 s status lines for ~105 s of patrol + F9 check; `Clamp()` fuzz of 500 random points around the camp | PASS - `inTerritory=True` on every status line, distance to camp 39-63 m (camp radius 30); Clamp 0/500 illegal (a first run found 196/500 illegal, fixed in d5c35f4) |
| 4 | Silence does not attract / trigger a hunt | ~105 s without any input, mammoth 39-63 m away, it even saw the player at 40 m | PASS - action stayed `Patrol`, no HEARD events, no Investigate/Track/Pursue |
| 5 | Noise attracts it | one pistol shot from camp (mammoth 43 m away); earlier an accidental shot at 57 m | PASS - `HEARD Gunshot ~43 m` -> `InvestigateSound` -> `Search` -> back to `Patrol`; it never entered the camp |
| 6 | 15 valid shots kill the mammoth | F8: 15 hits, two damage events per frame (shotgun-pellet case) | PASS - alive and counted once per shot for 1-14, dead exactly on 15 (hp 1400 -> 0) |
| 7 | 20 attacks kill the player | F7: 20 landed attacks | PASS - 7.5 damage each, alive after 19 (hp 7.5), dead on the 20th |

## Not covered / remaining issues
* F8 / F7 drive the same code paths a real shot / real stomp use (`Health.Damage` -> `OnHurt`, `MammothAI.Hit`) but do not fire the actual gun or wait for a real stomp, so shotgun / slingshot / stick hit counting and animation timing in a real fight were not exercised end-to-end.
* Sprint noise (22 m) and "other" noise (12 m) were not exercised; only gunshots.
* When the player stands in camp and shoots, the mammoth comes to the camp edge (33 m from the camp centre) and cannot go further - by design, but it was only observed, not stress-tested.
* The belief filter logs a `Lost <-> Nearby` line every few seconds while the mammoth is idle (log noise only).
* Other enemies still damage the player (observed 150 -> 139 HP with `attacks=0`); the 20-attack rule only counts mammoth attacks.
