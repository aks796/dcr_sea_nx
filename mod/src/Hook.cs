// Hook.cs -- replace a game method with one of ours (source/dcr_mod.c does the
// patching). The replacement has the target's parameters (an instance method's
// `this` first) and, to call the game's own code, a stub of the same signature
// marked NoInlining that the native side points at the original.
using System;
using System.Reflection;

namespace DcrMod
{
    public static class Hook
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        public static int Installed, Failed;

        public static MethodInfo Find(Type t, string name, params Type[] args)
        {
            if (args == null || args.Length == 0)
            {
                MethodInfo only = null;
                foreach (var m in t.GetMethods(All))
                    if (m.Name == name && m.GetParameters().Length == 0) { only = m; break; }
                return only;
            }
            return t.GetMethod(name, All, null, args, null);
        }

        // hookName/origName: static methods of `on`
        public static bool Install(Type target, string name, Type[] args, Type on, string hookName, string origName)
        {
            try
            {
                MethodInfo t = Find(target, name, args);
                MethodInfo h = on.GetMethod(hookName, All);
                MethodInfo o = origName == null ? null : on.GetMethod(origName, All);
                if (t == null || h == null || (origName != null && o == null))
                {
                    Log.Line("hook " + target.Name + "." + name + ": " + (t == null ? "target" : h == null ? "hook" : "original stub") + " not found");
                    Failed++;
                    return false;
                }
                int r = Native.Detour(t.MethodHandle.Value, h.MethodHandle.Value, o == null ? IntPtr.Zero : o.MethodHandle.Value);
                if (r != 0) { Failed++; return false; }
                Installed++;
                return true;
            }
            catch (Exception e)
            {
                Log.Line("hook " + target.Name + "." + name + " failed: " + e);
                Failed++;
                return false;
            }
        }
    }

    public static class Log
    {
        public static void Line(string s) { Native.Log(s); }
    }
}
