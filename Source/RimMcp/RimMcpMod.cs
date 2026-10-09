using UnityEngine;
using Verse;

namespace RimMcp
{
    public class RimMcpMod : Mod
    {
        public RimMcpMod(ModContentPack content) : base(content)
        {
        }
    }

    // Runs on the main thread once defs are loaded: the only safe place to create the
    // Unity object that pumps requests into the game.
    [StaticConstructorOnStartup]
    public static class RimMcpStartup
    {
        static RimMcpStartup()
        {
            var go = new GameObject("RimMcpPump");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Pump>();
            Bridge.Start();
        }
    }

    public class Pump : MonoBehaviour
    {
        public void Update()
        {
            Bridge.DrainOnMainThread();
            try { Tools.Tick(); } catch (System.Exception e) { Log.ErrorOnce("[RimMcp] auto-pause: " + e, 0x52a3c1); }
        }

        public void OnApplicationQuit() => Bridge.Stop();
    }
}
