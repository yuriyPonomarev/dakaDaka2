using System;
using System.Collections.Generic;
using System.IO;
using Emberdelve.AI;
using Emberdelve.Interaction;
using Emberdelve.Level;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Graphical, Play-mode-only V2 look review. Local verifier executes it; saves no assets/settings.
//
// Order (wait the stated time between the calls so animations can play; all calls are synchronous):
//   ReviewV2.Prepare();                      // freezes the enemy AI, creates the review camera, lists the poses
//   ReviewV2.Pose("corridor_torch");  wait 0.5 s;  ReviewV2.Shoot("corridor_torch");
//   ReviewV2.Pose("torch_close");     wait 0.5 s;  ReviewV2.Shoot("torch_close");
//   ReviewV2.Pose("enemy_idle");      wait 0.8 s;  ReviewV2.Shoot("enemy_idle");
//   ReviewV2.Pose("enemy_attack");    wait 0.45 s; ReviewV2.Shoot("enemy_attack");   // the Attack clip was just triggered
//   ReviewV2.Pose("elite_idle");      wait 0.8 s;  ReviewV2.Shoot("elite_idle");
//   ReviewV2.Pose("chest_closed");    wait 0.5 s;  ReviewV2.Shoot("chest_closed");
//   ReviewV2.Pose("chest_open");      wait 1.2 s;  ReviewV2.Shoot("chest_open");     // lid swings open (presentation only)
//   ReviewV2.Cleanup();
// PNGs: <project>/Logs/v2_review/<pose>_1280x720.png, rendered by a copy of the player camera into a 1280x720 render texture
// (full URP pipeline: post-processing volume and the retro full-screen pass included).
// Compare with the retro pass off: Emberdelve/Visual/Retro Pass/Switch OFF (or EmberdelveRetroSetup.SetEnabled(false)).
public static class ReviewV2
{
    private const int Width = 1280;
    private const int Height = 720;

    private static GameObject _cameraObject;
    private static Camera _camera;
    private static GameObject _handTorchOriginal;
    private static GameObject _handTorchClone;
    private static readonly List<EnemyAgent> Frozen = new List<EnemyAgent>();

    public static object Prepare()
    {
        RequireGraphics();
        Cleanup();

        Camera main = Camera.main;
        if (main == null) throw new InvalidOperationException("No main camera (is the dungeon running?)");

        Frozen.Clear();
        foreach (EnemyAgent agent in UnityEngine.Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None))
        {
            agent.enabled = false; // no brain tick: the visual driver stays on Idle unless a preview is triggered
            Frozen.Add(agent);
        }

        _cameraObject = new GameObject("ReviewV2_Camera");
        _camera = _cameraObject.AddComponent<Camera>();
        _camera.CopyFrom(main);
        _camera.tag = "Untagged";
        _camera.enabled = false; // rendered by hand
        _camera.allowHDR = true;
        UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
        UniversalAdditionalCameraData mainData = main.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.renderShadows = true;
        data.dithering = mainData.dithering;
        data.antialiasing = mainData.antialiasing;
        data.antialiasingQuality = mainData.antialiasingQuality;

        // The player's torch lights the scene around the review camera instead of the player.
        Transform torch = main.transform.Find("Player_Torch");
        if (torch != null)
        {
            _handTorchOriginal = torch.gameObject;
            _handTorchClone = UnityEngine.Object.Instantiate(torch.gameObject, _cameraObject.transform);
            _handTorchClone.transform.localPosition = torch.localPosition;
            _handTorchClone.transform.localRotation = torch.localRotation;
            _handTorchClone.SetActive(true);
            _handTorchOriginal.SetActive(false);
        }

        Directory.CreateDirectory(OutputDirectory());
        return new
        {
            prepared = true,
            frame = Time.frameCount,
            enemies = Frozen.Count,
            width = Width,
            height = Height,
            output = OutputDirectory(),
            poses = new[] { "corridor_torch", "torch_close", "enemy_idle", "enemy_attack", "elite_idle", "chest_closed", "chest_open" }
        };
    }

    public static object Pose(string pose)
    {
        RequireGraphics();
        RequireCamera();

        Vector3 position;
        Vector3 lookAt;

        switch (pose)
        {
            case "corridor_torch":
            case "torch_close":
            {
                Transform sconce = FindSconce();
                Transform flame = sconce.Find("Flame");
                Vector3 target = flame != null ? flame.position : sconce.position + Vector3.up * 0.4f;
                float distance = pose == "torch_close" ? 0.9f : 2.4f;
                Vector3 outward = sconce.forward; // local +Z points into the room
                position = ClearPoint(target, outward, distance) + Vector3.down * (pose == "torch_close" ? 0.1f : 0.6f);
                lookAt = target;
                break;
            }
            case "enemy_idle":
            case "enemy_attack":
            case "elite_idle":
            {
                EnemyAgent agent = FindEnemy(pose == "elite_idle");
                Vector3 centre = agent.transform.position;
                Vector3 direction = FreeDirection(centre + Vector3.up * 1.2f, 3.4f);
                position = centre + direction * 2.8f + Vector3.up * 1.55f;
                lookAt = centre + Vector3.up * 1.1f;
                agent.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);

                EnemyVisualDriver driver = agent.GetComponentInChildren<EnemyVisualDriver>();
                if (driver == null) throw new InvalidOperationException("Enemy has no EnemyVisualDriver");
                if (pose == "enemy_attack") driver.PreviewAttack();
                break;
            }
            case "chest_closed":
            case "chest_open":
            {
                ChestInteractable chest = FindChest();
                Transform visual = chest.transform.parent != null ? chest.transform.parent.Find(chest.name + "_Visual") : null;
                Vector3 centre = chest.transform.position;
                Vector3 direction = visual != null ? visual.forward : Vector3.forward;
                direction.y = 0f;
                direction = direction.sqrMagnitude < 0.01f ? Vector3.forward : direction.normalized;
                position = ClearPoint(centre, direction, 2.4f) + Vector3.up * 0.9f;
                lookAt = centre;

                if (pose == "chest_open")
                {
                    ChestVisualDriver driver = visual != null ? visual.GetComponent<ChestVisualDriver>() : null;
                    if (driver == null) throw new InvalidOperationException("Chest has no ChestVisualDriver");
                    driver.PreviewOpen();
                }
                break;
            }
            default:
                throw new ArgumentException("Unknown pose '" + pose + "'");
        }

        _camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position, Vector3.up));
        Physics.SyncTransforms();
        return new { pose, frame = Time.frameCount, gameTime = Time.time, x = position.x, y = position.y, z = position.z };
    }

    public static object Shoot(string pose)
    {
        RequireGraphics();
        RequireCamera();

        RenderTexture target = new RenderTexture(Width, Height, 24, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
        Texture2D image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        RenderTexture previous = RenderTexture.active;
        try
        {
            _camera.targetTexture = target;
            _camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            image.Apply();

            string path = Path.Combine(OutputDirectory(), pose + "_" + Width + "x" + Height + ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            return new { pose, path, bytes = new FileInfo(path).Length, frame = Time.frameCount, width = Width, height = Height };
        }
        finally
        {
            _camera.targetTexture = null;
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(target);
            UnityEngine.Object.Destroy(image);
        }
    }

    public static object Cleanup()
    {
        if (_handTorchClone != null) UnityEngine.Object.Destroy(_handTorchClone);
        if (_handTorchOriginal != null) _handTorchOriginal.SetActive(true);
        if (_cameraObject != null) UnityEngine.Object.Destroy(_cameraObject);
        for (int i = 0; i < Frozen.Count; i++) if (Frozen[i] != null) Frozen[i].enabled = true;
        Frozen.Clear();
        _cameraObject = null;
        _camera = null;
        _handTorchClone = null;
        _handTorchOriginal = null;
        return new { cleaned = true, frame = Time.frameCount };
    }

    // ---------------------------------------------------------------- helpers

    private static string OutputDirectory()
    {
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "v2_review");
    }

    private static void RequireGraphics()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode first");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Application.isBatchMode)
            throw new InvalidOperationException("The look review requires an owned graphical Editor");
    }

    private static void RequireCamera()
    {
        if (_camera == null) throw new InvalidOperationException("Call ReviewV2.Prepare() first");
    }

    private static Transform FindSconce()
    {
        // The torch in the corridor between the Entry Hall and the Central Hall (Torch_9) if present, else the first one.
        GameObject torch = GameObject.Find("Torch_9");
        if (torch == null) torch = GameObject.Find("Torch_0");
        if (torch == null) throw new InvalidOperationException("No torch in the scene");
        Transform sconce = torch.transform.Find("Sconce");
        if (sconce == null) throw new InvalidOperationException("Torch has no Sconce");
        return sconce;
    }

    private static EnemyAgent FindEnemy(bool elite)
    {
        foreach (EnemyAgent agent in Frozen)
        {
            if (agent == null || agent.Data == null) continue;
            if (agent.Data.isElite == elite) return agent;
        }

        throw new InvalidOperationException(elite ? "No elite enemy" : "No normal enemy");
    }

    private static ChestInteractable FindChest()
    {
        ChestInteractable[] chests = UnityEngine.Object.FindObjectsByType<ChestInteractable>(FindObjectsSortMode.None);
        if (chests.Length == 0) throw new InvalidOperationException("No chest in the scene");
        Array.Sort(chests, (a, b) => string.CompareOrdinal(a.name, b.name));
        return chests[0];
    }

    /// <summary>A horizontal direction (one of eight) along which the level is not closer than <paramref name="distance"/>.</summary>
    private static Vector3 FreeDirection(Vector3 from, float distance)
    {
        int levelMask = LayerMask.GetMask(Emberdelve.Core.GameConfig.LevelLayerName);
        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45f;
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            if (!Physics.Raycast(from, direction, distance, levelMask, QueryTriggerInteraction.Ignore)) return direction;
        }

        return Vector3.forward;
    }

    /// <summary>origin + direction * distance, pulled back towards the origin when a wall is in the way.</summary>
    private static Vector3 ClearPoint(Vector3 origin, Vector3 direction, float distance)
    {
        int levelMask = LayerMask.GetMask(Emberdelve.Core.GameConfig.LevelLayerName);
        direction = direction.sqrMagnitude < 0.01f ? Vector3.forward : direction.normalized;
        if (Physics.Raycast(origin, direction, out RaycastHit hit, distance, levelMask, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(0.5f, hit.distance - 0.2f);
        return origin + direction * distance;
    }
}
