using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Xml;
using BepInEx;
using HarmonyLib;
using JetBrains.Annotations;
using Serializers;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BytesServer;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class BytesServer : BaseUnityPlugin
{
    private static readonly Queue<IAsyncResult> RequestQueue = new();

    private static Level _level;

    public BytesServer()
    {
        // We specify our individual classes that contain Harmony patches instead of just doing
        // `Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly())` to avoid triggering a Harmony bug that causes
        // some patches to not function (probably related to https://github.com/BepInEx/HarmonyX/issues/71)
        var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        harmony.PatchAll(typeof(BytesServer));
        harmony.PatchAll(typeof(FixXmlDeserializationUintUnsupported));
    }

    private void Start()
    {
        Application.targetFrameRate = 100;
        _level = new Level(true);

        var port = 0;
        var portEnvVar = Environment.GetEnvironmentVariable("BYTES_SERVER_PORT");
        if (portEnvVar != null)
        {
            int.TryParse(portEnvVar, out port);
        }

        if (port is < 1 or > 65535)
        {
            port = 18500;
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        listener.Start();
        Console.WriteLine($"BytesServer listening on port {port}.");

        listener.BeginGetContext(ListenerCallback, listener);
        return;

        static void ListenerCallback(IAsyncResult result)
        {
            var listener = (HttpListener)result.AsyncState;
            listener.BeginGetContext(ListenerCallback, listener);
            lock (RequestQueue)
            {
                RequestQueue.Enqueue(result);
            }
        }
    }

    private void Update()
    {
        IAsyncResult result;
        lock (RequestQueue)
        {
            if (RequestQueue.Count > 0)
                result = RequestQueue.Dequeue();
            else
                return;
        }

        var listener = (HttpListener)result.AsyncState;
        var context = listener.EndGetContext(result);
        var request = context.Request;
        using var response = context.Response;

        var stopWatch = new Stopwatch();
        stopWatch.Start();

        switch (request.Url.AbsolutePath)
        {
            case "/level-bytes-to-xml":
            {
                if (request.HttpMethod != "POST")
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    LogRequest();
                    return;
                }

                var encodedLevelData = ReadBodyToArray(request);
                var isLoadSuccessful = _level.LoadFromBytes(encodedLevelData, true, false);
                if (isLoadSuccessful)
                {
                    var xmlLevelBytes = Serializer.SaveLevelToBytes<XmlSerializer>(_level);
                    response.ContentLength64 = xmlLevelBytes.Length;
                    response.OutputStream.Write(xmlLevelBytes, 0, xmlLevelBytes.Length);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                }

                response.Close();
                _level.ClearAndReset(true);
                break;
            }
            case "/level-xml-to-bytes":
            {
                if (request.HttpMethod != "POST")
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    LogRequest();
                    return;
                }

                var encodedLevelData = ReadBodyToArray(request);
                var deserializer = new XmlDeserializer(encodedLevelData);

                var loadState = new Level.LoadState();
                var enumerator = Traverse.Create(_level)
                    .Method("LoadHelperEnumerator", deserializer, true, false, loadState)
                    .GetValue<IEnumerator>();
                while (enumerator.MoveNext())
                {
                }

                if (loadState.success)
                {
                    var levelBytes = Serializer.SaveLevelToBytes<BinarySerializer>(_level);
                    response.ContentLength64 = levelBytes.Length;
                    response.OutputStream.Write(levelBytes, 0, levelBytes.Length);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                }

                response.Close();
                _level.ClearAndReset(true);
                break;
            }
            case "/gameobject-bytes-to-xml":
            {
                if (request.HttpMethod != "POST")
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    LogRequest();
                    return;
                }

                var encodedGameObjectData = ReadBodyToArray(request);
                var gameObject2 = Deserializer.LoadGameObjectFromBytes<BinaryDeserializer>(encodedGameObjectData);
                if (gameObject2 is not null)
                {
                    var prefab = Resource.LoadPrefab(gameObject2.name, false);
                    var xmlGameObjectBytes = Serializer.SaveGameObjectToBytes<XmlSerializer>(gameObject2, prefab);
                    response.ContentLength64 = xmlGameObjectBytes.Length;
                    response.OutputStream.Write(xmlGameObjectBytes, 0, xmlGameObjectBytes.Length);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                }

                response.Close();
                Destroy(gameObject2);
                break;
            }
            case "/gameobject-xml-to-bytes":
            {
                if (request.HttpMethod != "POST")
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    LogRequest();
                    return;
                }

                var encodedGameObjectData = ReadBodyToArray(request);
                var gameObject2 = Deserializer.LoadGameObjectFromBytes<XmlDeserializer>(encodedGameObjectData);
                if (gameObject2 is not null)
                {
                    var prefab = Resource.LoadPrefab(gameObject2.name, false);
                    var gameObjectBytes = Serializer.SaveGameObjectToBytes<BinarySerializer>(gameObject2, prefab);
                    response.ContentLength64 = gameObjectBytes.Length;
                    response.OutputStream.Write(gameObjectBytes, 0, gameObjectBytes.Length);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                }

                response.Close();
                Destroy(gameObject2);
                break;
            }
            case "/ping":
            {
                if (request.HttpMethod != "GET")
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    LogRequest();
                    return;
                }

                var buffer = Encoding.UTF8.GetBytes("OK");
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);

                response.Close();
                break;
            }
            default:
                response.StatusCode = (int)HttpStatusCode.NotFound;
                break;
        }

        LogRequest();

        return;

        static byte[] ReadBodyToArray(HttpListenerRequest request)
        {
            var stream = request.InputStream;
            using var memoryStream = new MemoryStream();
            var buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                memoryStream.Write(buffer, 0, bytesRead);
            }

            return memoryStream.ToArray();
        }

        void LogRequest()
        {
            stopWatch.Stop();
            Console.WriteLine(
                $"{response.StatusCode} {request.HttpMethod} {request.Url.AbsolutePath} - Request body size: {request.ContentLength64} - Execution: {stopWatch.ElapsedMilliseconds} ms");
        }
    }

    [HarmonyPatch(typeof(GSystemsManager), "Awake")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static void DisableSteam(GSystemsManager __instance)
    {
        __instance.managerPrefabsToCreate_ = __instance.managerPrefabsToCreate_.Where(obj =>
            obj.GetComponentsInChildren<SteamworksManager>(true).Length == 0).ToArray();
    }

    [HarmonyPatch(typeof(SplashScreenLogic), "Awake")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool StopSplashScreenProgressing(SplashScreenLogic __instance)
    {
        DestroyImmediate(__instance.gameObject);
        return false;
    }

    [HarmonyPatch(typeof(Level), "UpgradeLevelVersion")]
    [HarmonyPostfix]
    private static void FixLevelSettingLeak()
    {
        var allLevelSettings = FindObjectsOfType<LevelSettings>();
        foreach (var levelSettings in allLevelSettings)
        {
            if (levelSettings != _level.Settings_ && levelSettings != G.Sys.GameManager_.LevelSettings_)
            {
                Destroy(levelSettings.gameObject);
            }
        }
    }

    [HarmonyPatch]
    private static class FixXmlDeserializationUintUnsupported
    {
        [UsedImplicitly]
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(XmlDeserializer), "ReadSetPOD", null, [typeof(uint)]);

        [UsedImplicitly]
        // ReSharper disable once InconsistentNaming
        private static bool Prefix(XmlReader ___reader_, ref uint val)
        {
            val = (uint)(long)___reader_.ReadElementContentAs(typeof(long), null);
            return false;
        }
    }

    [HarmonyPatch(typeof(XmlDeserializer), "ReadArrayStart")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void FixXmlDeserializationEmptyArray(ref int __result)
    {
        if (__result < 0)
        {
            __result = 0;
        }
    }

    [HarmonyPatch(typeof(XmlDeserializer), nameof(XmlDeserializer.VisitChildren))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool FixXmlDeserializationVisitGroupChildren(XmlDeserializer __instance, Transform parent,
        Transform prefabParent)
    {
        if (!__instance.ReadToStartScope("Children")) return false;
        while (Traverse.Create(__instance).Method("Read", "GameObject", "Children").GetValue<bool>())
        {
            if (prefabParent == null || prefabParent.tag == "PrefabContainer")
            {
                // This is the only behavioral change we make in this method: pass in `parent` instead of `null`.
                Traverse.Create(__instance).Method("VisitGameObject", parent).GetValue<GameObject>();
            }
            else
            {
                var attribute = __instance.GetAttribute("Name");
                var transform = parent.FindChild(attribute);
                if (transform != null)
                {
                    var transform2 = prefabParent.FindChild(attribute);
                    var num = Traverse.Create(__instance).Method("ReadGUID").GetValue<uint>();
                    Traverse.Create(__instance).Method("AddObjectToReferences", transform.gameObject, num).GetValue();
                    Traverse.Create(__instance)
                        .Method("VisitChildContents", transform.gameObject, transform2.gameObject).GetValue();
                }
                else
                {
                    Debug.LogWarning(string.Concat(new string[]
                    {
                        "When XML serializing in a GameObject, the child with name ", attribute,
                        " of parent with name ", parent.name, " wasn't found, was it renamed?"
                    }));
                    Traverse.Create(__instance).Method("LoopReadToEndScopeWithDepth", "GameObject").GetValue();
                }
            }
        }

        return false;
    }

    [HarmonyPatch(typeof(XmlSerializer), "SetupWriterSettings")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool CustomizeXmlWriterSettings(ref XmlWriterSettings ___xmlWriterSettings_)
    {
        ___xmlWriterSettings_ = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CloseOutput = true,
            ConformanceLevel = ConformanceLevel.Auto
        };

        return false;
    }

    [HarmonyPatch(typeof(Serializer), nameof(Serializer.GetPrefabWithName))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool AllowLoadingReplaysWithoutSpecifyingPrefab(ref GameObject __result, string prefabName)
    {
        if (!prefabName.StartsWith("Replay:")) return true;

        __result = Resource.LoadPrefab("CarReplayData", false);
        return false;
    }
}