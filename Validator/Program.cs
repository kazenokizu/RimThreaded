// RimThreaded patch validator: runs the mod's real patch registration (Harmony) against the real RimWorld
// Assembly-CSharp.dll on .NET 8, without launching the game. Harmony JIT-compiles every patched method when it
// installs it, so missing targets, wrong patch signatures and invalid transpiler IL all surface here as exceptions.
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

public static class Program
{
    public static readonly List<string> Failures = new List<string>();
    public static readonly List<string> GameErrors = new List<string>();
    public static readonly List<string> GameWarnings = new List<string>();
    static readonly object gate = new object();
    static string runDir;

    public static int Main(string[] args)
    {
        runDir = Path.GetFullPath(args[0]);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.WriteLine("start");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string p = Path.Combine(runDir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        Assembly game = Assembly.LoadFrom(Path.Combine(runDir, "Assembly-CSharp.dll"));
        Assembly mod = Assembly.LoadFrom(Path.Combine(runDir, "RimThreaded.dll"));
        var h = new Harmony("validator.hooks");

        Console.WriteLine("phase: log hooks");
        // 1. swallow game logging (Unity natives are not available) and keep the text
        Type logType = game.GetType("Verse.Log");
        foreach (MethodInfo m in logType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name is "Message" or "Warning" or "Error" or "ErrorOnce" or "WarningOnce" or "Clear")
            {
                try { Console.WriteLine("hook " + m); h.Patch(m, prefix: new HarmonyMethod(typeof(Program), nameof(LogPrefix))); }
                catch (Exception e) { Console.WriteLine("log hook failed " + m + ": " + e.Message); }
            }
        }

        Console.WriteLine("phase: state");
        // 2. minimal static state the mod reads during registration
        Type prefs = game.GetType("Verse.Prefs");
        Type prefsData = game.GetType("Verse.PrefsData");
        AccessTools.Field(prefs, "data").SetValue(null, Activator.CreateInstance(prefsData));
        Type modType = mod.GetType("RimThreaded.RimThreadedMod");
        string asmDir = Path.Combine(runDir, "1.6", "Assemblies");
        Directory.CreateDirectory(asmDir);
        File.Copy(Path.Combine(runDir, "replacements_1.6.json"), Path.Combine(asmDir, "replacements_1.6.json"), true);
        AccessTools.Field(modType, "replacementsFolder").SetValue(null, asmDir);
        AccessTools.Field(modType, "replacementsJsonPath").SetValue(null, Path.Combine(asmDir, "replacements_1.6.json"));

        Console.WriteLine("phase: finalizers");
        // 3. finalizers: turn every exception inside registration code into a recorded failure and continue
        Type rth = mod.GetType("RimThreaded.RimThreadedHarmony");
        var fin = new HarmonyMethod(typeof(Program), nameof(Finalizer));
        int hooked = 0;
        foreach (Type t in SafeTypes(mod))
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                bool isRegistration = m.Name.StartsWith("Run") && m.Name.Contains("Patches");
                bool isHelper = t == rth && m.Name is "Prefix" or "Postfix" or "Transpile" or "TranspileMethodLock" or "TranspileFieldReplacements"
                    or "TranspileLockAdd3" or "LoadFieldReplacements" or "AddAdditionalReplacements" or "ApplyFieldReplacements"
                    or "PatchDestructiveFixes" or "PatchNonDestructiveFixes" or "PatchModCompatibility";
                if (!(isRegistration || (t == rth && isHelper)) || m.IsAbstract || m.ContainsGenericParameters) continue;
                try { h.Patch(m, finalizer: fin); hooked++; }
                catch (Exception e) { Failures.Add("HOOK " + t.Name + "." + m.Name + ": " + e.Message); }
            }
        }
        Console.WriteLine("hooked " + hooked + " registration methods");

        Console.WriteLine("phase: cctor");
        // 4. run the real static constructor: LoadFieldReplacements, ApplyFieldReplacements, all patches
        try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(rth.TypeHandle); }
        catch (Exception e) { Failures.Add("CCTOR " + Unwrap(e)); }

        Console.WriteLine();
        var ecall = GameErrors.Concat(Failures).Where(x => x.Contains("ECall methods must be packaged")).ToList();
        var real = GameErrors.Concat(Failures).Where(x => !x.Contains("ECall methods must be packaged")).Distinct().ToList();
        Console.WriteLine("=== REAL failures: " + real.Count);
        foreach (string s in real) Console.WriteLine("  " + s.Replace("\n", " ").Substring(0, Math.Min(600, s.Replace("\n", " ").Length)));
        Console.WriteLine("=== not JIT-verifiable on this runtime (Unity internal call in body; transpiler itself ran): " + ecall.Count);
        foreach (string s in ecall.Take(400)) Console.WriteLine("  " + s.Replace("\n", " ").Substring(0, Math.Min(200, s.Replace("\n", " ").Length)));
        var patched = Harmony.GetAllPatchedMethods().ToList();
        Console.WriteLine("=== methods patched in process: " + patched.Count);
        SignatureCheck(patched);
        return real.Count == 0 ? 0 : 1;
    }

    // Harmony does not check that prefix/postfix parameter types agree with the original; the JIT only catches some mismatches.
    static void SignatureCheck(List<MethodBase> patched)
    {
        int checkedPatches = 0; var problems = new List<string>();
        foreach (MethodBase original in patched)
        {
            Patches info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            var ops = original.GetParameters();
            foreach (var group in new[] { info.Prefixes, info.Postfixes })
            {
                foreach (Patch patch in group)
                {
                    if (patch.owner != "majorhoff.rimthreaded") continue;
                    checkedPatches++;
                    MethodInfo pm = patch.PatchMethod;
                    foreach (ParameterInfo pp in pm.GetParameters())
                    {
                        string n = pp.Name;
                        Type pt = pp.ParameterType.IsByRef ? pp.ParameterType.GetElementType() : pp.ParameterType;
                        if (n == "__instance")
                        {
                            if (!pt.IsAssignableFrom(original.DeclaringType) && !original.DeclaringType.IsAssignableFrom(pt))
                                problems.Add(Name(original) + " <- " + Name(pm) + ": __instance " + pt + " vs " + original.DeclaringType);
                        }
                        else if (n == "__result")
                        {
                            Type rt = (original as MethodInfo)?.ReturnType;
                            if (rt == null || rt == typeof(void) || !(pt == rt || pt.IsAssignableFrom(rt)))
                                problems.Add(Name(original) + " <- " + Name(pm) + ": __result " + pt + " vs return " + rt);
                        }
                        else if (n.StartsWith("__") || n.StartsWith("___")) { }
                        else
                        {
                            ParameterInfo op = ops.FirstOrDefault(o => o.Name == n);
                            if (op == null) problems.Add(Name(original) + " <- " + Name(pm) + ": parameter '" + n + "' not in original");
                            else
                            {
                                Type ot = op.ParameterType.IsByRef ? op.ParameterType.GetElementType() : op.ParameterType;
                                bool refMismatch = pp.ParameterType.IsByRef && !op.ParameterType.IsByRef && !op.IsOut && false;
                                if (ot != pt && !pt.IsAssignableFrom(ot))
                                    problems.Add(Name(original) + " <- " + Name(pm) + ": parameter '" + n + "' " + pt + " vs original " + ot);
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine("=== signature check: " + checkedPatches + " rimthreaded prefix/postfix patches checked, problems: " + problems.Count);
        foreach (string s in problems.Distinct()) Console.WriteLine("  " + s);
    }
    static string Name(MethodBase m) => m.DeclaringType?.Name + "." + m.Name;

    static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }

    static string Unwrap(Exception e)
    {
        while ((e is TargetInvocationException || e is TypeInitializationException) && e.InnerException != null) e = e.InnerException;
        string s = e.GetType().Name + ": " + e.Message;
        string[] lines = (e.StackTrace ?? "").Split('\n');
        return s + " @ " + string.Join(" | ", lines.Take(3).Select(l => l.Trim()));
    }

    public static bool LogPrefix(MethodBase __originalMethod, object[] __args)
    {
        string text = __args != null && __args.Length > 0 ? __args[0]?.ToString() : "";
        lock (gate)
        {
            if (__originalMethod.Name.StartsWith("Error")) GameErrors.Add(text);
            else if (__originalMethod.Name.StartsWith("Warning")) GameWarnings.Add(text);
        }
        return false;
    }

    public static Exception Finalizer(Exception __exception, MethodBase __originalMethod, object[] __args)
    {
        if (__exception != null)
        {
            string ctx = "";
            if (__originalMethod.DeclaringType.Name == "RimThreadedHarmony" && __args != null)
                ctx = " (" + string.Join(", ", __args.Take(3).Select(a => a is Type t ? t.Name : a is MethodBase mb ? mb.DeclaringType?.Name + "." + mb.Name : a?.ToString())) + ")";
            lock (gate) Failures.Add(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name + ctx + " -> " + Unwrap(__exception));
        }
        return null;
    }
}
