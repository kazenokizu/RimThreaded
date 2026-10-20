# RimThreaded 1.4 -> RimWorld 1.6.4871 port: status (NOT working yet)
- csproj now references ../Dependencies/1.6/*.dll (copy your RimWorldWin64_Data/Managed DLLs there).
- Removed (obsolete, pathfinding is Unity-jobs based in 1.6): PathFinder_Patch, PawnPathPool_Patch (kept in removed_1.6/).
- 69 compile errors remain in 30 files: see PORT_1.6_COMPILE_ERRORS.txt.
- Compile errors are only the first layer: Harmony string-based targets are only checked at runtime.
