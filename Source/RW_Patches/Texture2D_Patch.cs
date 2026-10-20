using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System;
using System.Reflection;
using HarmonyLib;
using static System.Threading.Thread;
using static RimThreaded.RimThreaded;

namespace RimThreaded.RW_Patches
{

    public class Texture2D_Patch
    {

        internal static void RunDestructivePatches()
        {
            Type original = typeof(Texture2D);
            Type patched = typeof(Texture2D_Patch);
#if !RW17
            RimThreadedHarmony.Prefix(original, patched, "Internal_Create");
#endif
            // 1.7 (Waylith): Texture2D.Internal_Create no longer exists; textures are built through Waylith.Rendering.GpuTextureService,
            // which serializes every allocation on its own lock, so creation from a worker thread needs no main-thread dispatch.
            RimThreadedHarmony.Prefix(original, patched, "ReadPixels", new Type[] { typeof(Rect), typeof(int), typeof(int), typeof(bool) });
            RimThreadedHarmony.Prefix(original, patched, "Apply", new Type[] { typeof(bool), typeof(bool) });
        }

        public static bool GetPixel(Texture2D __instance, ref Color __result, int x, int y)
        {
            if (!CurrentThread.IsBackground || !allWorkerThreads.TryGetValue(CurrentThread, out ThreadInfo threadInfo))
                return true;
            Func<object[], object> safeFunction3 = parameters =>
                __instance.GetPixel(
                    (int)parameters[0],
                    (int)parameters[1]);
            threadInfo.safeFunctionRequest = new object[] { safeFunction3, new object[] { x, y } };
            mainThreadWaitHandle.Set();
            threadInfo.eventWaitStart.WaitOne();
            __result = (Color)threadInfo.safeFunctionResult;
            return false;
        }
        // 1.6 port. Unity 2022.3 (RimWorld 1.6) has Internal_Create(Texture2D mono, int w, int h, int mipCount, GraphicsFormat format,
        // TextureColorSpace colorSpace, TextureCreationFlags flags, IntPtr nativeTex, string mipmapLimitGroupName). The 1.4 copy hard-coded
        // the older 7-parameter signature: the lookup returned null, CreateDelegate threw, and the exception aborted RimThreaded's start-up
        // (Player.log: "type initializer for 'RimThreaded.RW_Patches.Texture2D_Patch' threw"). The prefix now takes Harmony's __args, and the
        // main thread re-invokes the patched original with the same arguments (the prefix lets main-thread calls through), so the parameter
        // list is never restated here.
        static readonly MethodInfo internalCreate = AccessTools.Method(typeof(Texture2D), "Internal_Create");

        static readonly Action<object[]> invokeInternalCreate = parameters => internalCreate.Invoke(null, parameters);

        public static bool Internal_Create(object[] __args)
        {
            if (!CurrentThread.IsBackground || !allWorkerThreads.TryGetValue(CurrentThread, out ThreadInfo threadInfo))
                return true;
            threadInfo.safeFunctionRequest = new object[] { invokeInternalCreate, (object[])__args.Clone() };
            mainThreadWaitHandle.Set();
            threadInfo.eventWaitStart.WaitOne();
            return false;
        }


        public static bool ReadPixels(Texture2D __instance, Rect source, int destX, int destY, bool recalculateMipMaps = true)
        {
            if (!CurrentThread.IsBackground || !allWorkerThreads.TryGetValue(CurrentThread, out ThreadInfo threadInfo))
                return true;
            Action<object[]> safeFunction = p => __instance.ReadPixels((Rect)p[0], (int)p[1], (int)p[2], (bool)p[3]);
            threadInfo.safeFunctionRequest = new object[] { safeFunction, new object[] { source, destX, destY, recalculateMipMaps } };
            mainThreadWaitHandle.Set();
            threadInfo.eventWaitStart.WaitOne();
            return false;
        }


        public static bool Apply(Texture2D __instance, bool updateMipmaps = true, bool makeNoLongerReadable = false)
        {
            if (!CurrentThread.IsBackground || !allWorkerThreads.TryGetValue(CurrentThread, out ThreadInfo threadInfo))
                return true;
            Action<object[]> safeFunction = p => __instance.Apply((bool)p[0], (bool)p[1]);
            threadInfo.safeFunctionRequest = new object[] { safeFunction, new object[] { updateMipmaps, makeNoLongerReadable } };
            mainThreadWaitHandle.Set();
            threadInfo.eventWaitStart.WaitOne();
            return false;
        }
    }
}
