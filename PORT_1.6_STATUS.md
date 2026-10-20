# RimThreaded 1.4 -> RimWorld 1.6.4871 rev590 port: status

Unofficial port. Builds, and every patch applies to the real 1.6.4871 assemblies. It has NOT been run in a live game by the porter.

## Verified (Validator/, run against the 1.6.4871 Assembly-CSharp.dll from a real install, Harmony 2.4.3)
- Build: 0 errors, 0 warnings.
- Patch application: 0 real failures; 1099 methods patched. 90 methods cannot be JIT-compiled outside Unity because their bodies
  call Unity internals (ECall); their transpilers ran, only final compilation is impossible in the harness.
- Signatures: 380 prefix/postfix patches match their targets (names, types, ref/out, __result type).
- Transpilers: 682 transpiled methods, none left unchanged (no silent no-ops).
- replacements_1.6.json: 338 fields exist in 1.6 with the expected types.
- Destructive prefixes: all 147 replacements and 47 guards reviewed side by side with the 1.6 source; IL check finds no path that
  returns false without setting __result.
- Lock test: ReservationManager.IsReserved blocks while another thread holds the instance lock, and returns correct values.

## Not verified
- In-game stability and speed-up (needs a live RimWorld session; send Player.log).
- Deadlock freedom between the locks added here (bounded waits are used where a worker could wait on the main thread).
- Unity API calls from worker threads that are not routed to the main thread.
- Region/room reads on worker threads while a rebuild runs on another thread.
- ListerThings in-place Add with concurrent foreach readers; PawnsFinder AllPawnsAlive AddRange races.
- ContentFinder<T> main-thread dispatch only covers Texture2D.
- Mod-compatibility patches (Source/ModCompatibility) were not audited.
- RimWorld 1.7 (Waylith engine, Steam unstable branch as of Oct 2026): not supported.

## Layout
- Source/RW_Patches: patches; removed_1.6/: upstream files dropped because vanilla 1.6 now does the same thing correctly.
- Validator/run.sh: rebuilds and runs the harness; output in /tmp/validator_out.txt. `ildiff` mode diffs IL before/after patches.
- Package: RimThreaded/About + RimThreaded/1.6/Assemblies (no game DLLs).
