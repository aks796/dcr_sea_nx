// FastBundles.cs -- the game's asset bundles opened in place inside game.apk.
//
// The game's AssetBundleManager (Unity's sample, in firstpass) asks for every
// bundle with WWW.LoadFromCacheOrDownload("jar:file://.../base.apk!/assets/
// AssetBundles/android/<name>"). Unity 5.6 sends that to its Java WWW class
// (jni_www.c streams the entry), then writes the whole bundle into its cache
// on the SD card and reads it back. On a first start that was ~35 MB of SD
// writes while the menu drew: frames of 2-3 s with every thread waiting on
// the card (hardware log 2026-09-28: frames 247/267/291, "PreloadMesh Time
// end: 19.5"). The setup stores game.apk uncompressed, so every bundle is a
// plain byte range of the APK: AssetBundle.LoadFromFileAsync(apk, 0, offset)
// opens it where it lies -- no Java thread, no copy, no cache -- and Unity
// reads its LZ4 blocks lazily, as it does from StreamingAssets on a phone.
//
// LoadAssetBundleInternal starts those loads (anything not in the APK still
// goes the original way), and a step before the manager's own Update moves the
// finished ones into its loaded-bundle table, as its WWW code does.
//
// It also counts what the manager forgets to (on either path, so even with
// bundles_in_place off): a request for a bundle that is still loading returns
// "already there" without a reference, yet every request unloads the bundle a
// second after its asset arrives. The first to do so dropped the count to
// zero and unloaded the bundle under the loads still in flight: "Failed to
// load asset 'disney_mickey_bird_flap_1_optimised.asset'" (hardware log
// 2026-09-29, the bundles all opening at once from game.apk), no callback,
// and the title's mesh preload waiting out its 5 s timeout twice.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AssetBundles;
using UnityEngine;

namespace DcrMod
{
    public static class FastBundles
    {
        const string ApkAndroidPath = "/data/app/net.gogame.disney.crossyroad-1/base.apk";
        class Req { public AssetBundleCreateRequest r; public string url; public long offset; public int form; public bool manifest, backup; }
        static readonly Dictionary<string, Req> pending = new Dictionary<string, Req>();
        // references taken while a bundle was loading, added when it arrives
        static readonly Dictionary<string, int> early = new Dictionary<string, int>();
        static readonly List<string> arrived = new List<string>();
        static bool inPlace;
        static int opened, fallback, counted;
        static long t0;
        // how a bundle is named to Unity: 1 = Unity's own jar:file://...!/ form
        // (StreamingAssets, read in place through its APK reader), 2 = that did
        // not work: the game's own way (WWW + cache). (0, the APK file with the
        // entry's offset, made this Unity run out of memory: Ryujinx 2026-09-29.)
        static int form = 1;

        // a bundle still on its way, by either path
        public static bool Busy { get { return pending.Count > 0 || AssetBundleManager.m_DownloadingWWWs.Count > 0; } }

        public static void Install()
        {
            inPlace = Native.Config("performance.bundles_in_place", 1) != 0;
            if (!inPlace)
                Log.Line("bundles: through Unity's cache (bundles_in_place off)");
            Hook.Install(typeof(AssetBundleManager), "LoadAssetBundleInternal", new[] { typeof(string), typeof(bool), typeof(bool) },
                typeof(FastBundles), "LoadInternal", "Orig_LoadInternal");
            Hook.Install(typeof(AssetBundleManager), "Update", null, typeof(FastBundles), "Update", "Orig_Update");
            Hook.Install(typeof(AssetBundleManager), "Initialize", new[] { typeof(string), typeof(bool) },
                typeof(FastBundles), "Initialize", "Orig_Initialize");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Orig_LoadInternal(string name, bool manifest, bool backup) { throw new InvalidOperationException("hook stub"); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_Update(AssetBundleManager self) { throw new InvalidOperationException("hook stub"); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static AssetBundleLoadManifestOperation Orig_Initialize(string name, bool backup) { throw new InvalidOperationException("hook stub"); }

        static AssetBundleLoadManifestOperation Initialize(string name, bool backup)
        {
            pending.Clear();
            early.Clear();
            return Orig_Initialize(name, backup);
        }

        static bool LoadInternal(string name, bool manifest, bool backup)
        {
            LoadedAssetBundle loaded;
            if (AssetBundleManager.m_LoadedAssetBundles.TryGetValue(name, out loaded) && loaded != null)
            {
                loaded.m_ReferencedCount++;
                return true;
            }
            if (pending.ContainsKey(name) || AssetBundleManager.m_DownloadingWWWs.ContainsKey(name))
            {
                int n;
                early.TryGetValue(name, out n);
                early[name] = n + 1;
                return true;
            }
            if (!inPlace)
                return Orig_LoadInternal(name, manifest, backup);
            string url = (!backup && Array.IndexOf(Utility.AssetBundlesForcedLocal, name) == -1)
                ? AssetBundleManager.BaseDownloadingURL + name : AssetBundleManager.BackupDownloadingURL + name;
            int bang = url.IndexOf("!/", StringComparison.Ordinal);
            long offset, size;
            if (form >= 2 || !url.StartsWith("jar:file://", StringComparison.Ordinal) || bang < 0 ||
                Native.ApkEntry(url.Substring(bang + 2), out offset, out size) != 0)
            {
                if (fallback++ < 8)
                    Log.Line("bundles: " + name + " the usual way (" + url + ")");
                return Orig_LoadInternal(name, manifest, backup);
            }
            if (opened == 0)
                t0 = Native.TicksMs();
            opened++;
            pending[name] = Start(url, offset, manifest, backup);
            return false;
        }

        static Req Start(string url, long offset, bool manifest, bool backup)
        {
            var q = new Req { url = url, offset = offset, form = form, manifest = manifest, backup = backup };
            q.r = form == 0 ? AssetBundle.LoadFromFileAsync(ApkAndroidPath, 0u, (ulong)offset)
                            : AssetBundle.LoadFromFileAsync(url, 0u);
            return q;
        }

        static readonly List<string> done = new List<string>();
        static readonly List<string> retry = new List<string>();

        static void Update(AssetBundleManager self)
        {
            if (pending.Count > 0)
            {
                done.Clear();
                retry.Clear();
                foreach (var kv in pending)
                {
                    if (!kv.Value.r.isDone)
                        continue;
                    AssetBundle b = kv.Value.r.assetBundle;
                    if (b == null)
                    {
                        // the first bundle that fails in a form moves every later one on
                        if (kv.Value.form == form && form < 2)
                        {
                            Log.Line("bundles: " + kv.Key + " did not open as " + (form == 0 ? "a range of game.apk" : kv.Value.url) + (form == 0 ? "; trying Unity's jar: form" : "; back to the game's own loading"));
                            form++;
                        }
                        retry.Add(kv.Key);
                    }
                    else if (!AssetBundleManager.m_LoadedAssetBundles.ContainsKey(kv.Key))
                    {
                        var lb = new LoadedAssetBundle(b);
                        lb.m_ReferencedCount += TakeEarly(kv.Key);
                        AssetBundleManager.m_LoadedAssetBundles.Add(kv.Key, lb);
                    }
                    done.Add(kv.Key);
                }
                foreach (string k in done)
                {
                    Req q = pending[k];
                    pending.Remove(k);
                    if (!retry.Contains(k))
                        continue;
                    if (form < 2)
                        pending[k] = Start(q.url, q.offset, q.manifest, q.backup);
                    else
                        Orig_LoadInternal(k, q.manifest, q.backup);
                }
                if (pending.Count == 0 && done.Count > 0)
                    Log.Line("bundles: " + opened + " opened in place in game.apk so far (" + (Native.TicksMs() - t0) + " ms since the first)");
            }
            Orig_Update(self);
            // the game's own (WWW) loads that arrived, or failed, in that Update
            if (early.Count > 0)
            {
                arrived.Clear();
                foreach (var kv in early)
                    if (!pending.ContainsKey(kv.Key) && !AssetBundleManager.m_DownloadingWWWs.ContainsKey(kv.Key))
                        arrived.Add(kv.Key);
                foreach (string k in arrived)
                {
                    LoadedAssetBundle lb;
                    int n = TakeEarly(k);
                    if (AssetBundleManager.m_LoadedAssetBundles.TryGetValue(k, out lb) && lb != null)
                        lb.m_ReferencedCount += n;
                }
            }
        }

        static int TakeEarly(string name)
        {
            int n;
            if (!early.TryGetValue(name, out n))
                return 0;
            early.Remove(name);
            if (counted++ < 6)
                Log.Line("bundles: " + name + " arrived with " + n + " more request(s) waiting on it; each keeps it loaded");
            return n;
        }
    }
}
