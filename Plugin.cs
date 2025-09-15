using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UgandanKnucklesDoom
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; } = null!;
        public static Plugin Instance;
        public GameObject Knuckles;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Load prefab from embedded assetbundle
            Knuckles = LoadKnucklesFromEmbeddedBundle();
            
            var harmony = new Harmony("com.IBMOTH.MageArena.UgandanKnuckles");
            harmony.PatchAll();

            Log.LogInfo($"Plugin {PluginInfo.PLUGIN_NAME} is loaded!");
        }

        private GameObject LoadKnucklesFromEmbeddedBundle()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourcePath = "UgandanKnucklesDoom.Assets.knucklesbundle";

            using (Stream stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream == null)
                {
                    Log.LogError($"Embedded AssetBundle '{resourcePath}' not found!");
                    return null;
                }

                byte[] bundleData = new byte[stream.Length];
                stream.Read(bundleData, 0, bundleData.Length);

                AssetBundle bundle = AssetBundle.LoadFromMemory(bundleData);
                if (bundle == null)
                {
                    Log.LogError("Failed to load AssetBundle from memory!");
                    return null;
                }

                GameObject prefab = bundle.LoadAsset<GameObject>("Knuckles");
                return prefab;
            }
        }
    }
    
    // Make sure it catches all blackholes, only when exactly 1 is instantiated
    public static class BlackHoleTracker
    {
        public static BlackHole Latest;
    }

    [HarmonyPatch(typeof(BlackHole), "OnEnable")]
    public static class BlackHole_OnEnable_Patch
    {
        static void Postfix(BlackHole __instance)
        {
            // Overwrite with newest blackhole
            BlackHoleTracker.Latest = __instance;
        }
    }

    [HarmonyPatch(typeof(PageController), "RpcLogic___CastSpellObs_3976682022")]
    public static class DoomPage_CastSpell_Patch
    {
        static void Postfix(PageController __instance, GameObject ownerobj, Vector3 fwdVector, int level, Vector3 spawnpos)
        {
            // Only run if spell is Doom
            if (__instance.spellprefab == null || !__instance.spellprefab.name.Contains("DoomSpell"))
                return;
            
            BlackHole bh = BlackHoleTracker.Latest;
            if (bh == null)
            {
                Plugin.Log.LogError("No BlackHole found in tracker!");
                return;
            }

            GameObject lastBlackHole = bh.gameObject;

            // Remove LookatCamera
            var unwantedComp = lastBlackHole.GetComponent<LookatCamera>();
            if (unwantedComp != null) GameObject.Destroy(unwantedComp);

            Transform holder = lastBlackHole.transform.Find("holder");
            if (holder == null) return;

            var singleplane = holder.transform.Find("singleplane");
            if (singleplane != null)
            {
                var mr = singleplane.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;
            }

            // Instantiate Knuckles as child of holder
            GameObject knucklesInstance = GameObject.Instantiate(Plugin.Instance.Knuckles, holder);
            knucklesInstance.name = "KnucklesVisual";

            knucklesInstance.transform.localPosition = Vector3.zero;
            knucklesInstance.transform.localRotation = Quaternion.identity;
            knucklesInstance.transform.localScale = Vector3.one * 1.2f;

            knucklesInstance.AddComponent<BlackHoleFollower>().target = holder;
        }
    }
    
    public class BlackHoleFollower : MonoBehaviour
    {
        public Transform target;
        private Vector3 initialScale;

        private void Awake()
        {
            initialScale = transform.localScale;
            transform.rotation = Quaternion.identity;
        }

        private void LateUpdate()
        {
            if (target == null) return;

            // Follow blackhole position
            transform.position = target.position;

            // Maintain original scale
            transform.localScale = initialScale;
        }
    }

    // Patch for BlackHole visual updates because it breaks when knuckles is instantiated
    [HarmonyPatch(typeof(BlackHole), "SetupRoutine")]
    public static class BlackHole_SetupRoutine_Patch
    {
        // Prefix replaces the original visuals
        static bool Prefix(BlackHole __instance, ref IEnumerator __result)
        {
            __result = CustomRoutine(__instance);
            return false; // Skip the original SetupRoutine
        }
        private static IEnumerator CustomRoutine(BlackHole bh)
        {
            float timer = 0f;
            Vector3 startpos = bh.transform.position;

            // Rise up over 2 seconds
            while (timer < 2f)
            {
                yield return null;
                timer += Time.deltaTime;
                bh.transform.position = Vector3.Lerp(startpos, startpos + new Vector3(0f, 15f, 0f), timer / 2f);

                if (bh.holder != null)
                    bh.holder.localScale = Vector3.Lerp(Vector3.one * 0.1f, Vector3.one * 0.1f, timer / 2f);
            }
            
            Transform knuckles = bh.transform.Find("holder/KnucklesVisual");
            if (knuckles != null)
            {
                if (knuckles.GetComponent<KnucklesFloatSpin>() == null)
                    knuckles.gameObject.AddComponent<KnucklesFloatSpin>();
            }
            
            yield return new WaitForSeconds(0.3f);
            
            // Expand animation to sync with the spell
            timer = 0f;
            while (timer < 0.2f)
            {
                yield return null;
                timer += Time.deltaTime;
                if (bh.holder != null)
                    bh.holder.localScale = Vector3.Lerp(Vector3.one * 0.1f, Vector3.one, timer * 5f);
            }

            // Force activate blackhole
            typeof(BlackHole).GetField("inited", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(bh, true);

            // Wait 10s (duration active)
            yield return new WaitForSeconds(10f);

            // Shrink and destroy blackhole
            timer = 0f;
            while (timer < 0.2f)
            {
                yield return null;
                timer += Time.deltaTime;
                if (bh.holder != null)
                    bh.holder.localScale = Vector3.Lerp(Vector3.one, Vector3.one * 0.1f, timer * 5f);
            }

            Object.Destroy(bh.gameObject);
            static void Postfix(BlackHole __instance)
            {
                if (BlackHoleTracker.Latest == __instance) // Remove the destroyed blackhole from tracker
                {
                    BlackHoleTracker.Latest = null;
                }
            }
        }
    }
    
    // Knuckles' random spin midair
    public class KnucklesFloatSpin : MonoBehaviour
    {
        private Vector3 currentRotationSpeed;
        private Vector3 targetRotationSpeed;
        private float timer;
        private float changeInterval = 1f;

        private void Start()
        {
            NewSpin();
        }

        private void Update()
        {
            timer += Time.deltaTime;

            if (timer >= changeInterval)
            {
                timer = 0f;
                NewSpin();
            }
            // Ease into new spin and apply rotation
            currentRotationSpeed = Vector3.Lerp(currentRotationSpeed, targetRotationSpeed, Time.deltaTime*.5f);
            transform.Rotate(currentRotationSpeed * Time.deltaTime, Space.Self);
        }
        
        // Pick a random spin for each axis
        private void NewSpin()
        {
            targetRotationSpeed = new Vector3(
                Random.Range(-720f, 720f),
                Random.Range(-720f, 720f),
                Random.Range(-720f, 720f)
            );
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.IBMOTH.MageArena.UgandanKnuckles";
        public const string PLUGIN_NAME = "Ugandan Knuckles Doom";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}