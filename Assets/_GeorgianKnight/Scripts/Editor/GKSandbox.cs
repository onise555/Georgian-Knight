using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Combat sandbox: an empty, flat 100 x 100 m arena with a 1 m grid (10 m major lines), the Didgori sky and sun, the
    /// player with the same camera rig and game systems as Didgori, short practice waves close by, and a corner of
    /// reference blocks (step heights, a wall and a pillar for the camera). Scene: Scenes/Combat_Sandbox.unity.
    /// </summary>
    public static class GKSandbox
    {
        public const string ScenePath = GKPaths.Scenes + "/Combat_Sandbox.unity";
        public const string CleanScenePath = GKPaths.Scenes + "/Clean_Sandbox.unity";
        const string Dir = GKPaths.Generated + "/Sandbox";

        [GKCommand("sandbox", "sandbox [v2|old|clean]: build the empty combat test arena (flat 1 m grid, the CC-dressed hero, Hero V2 or the Didgori player, camera, practice waves); 'clean': Knight_Clean in its own scene, Clean_Sandbox")]
        static void Build(string arg)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { GKBridge.Log("sandbox: stop play mode first"); return; }
            // the Character Creator-dressed hero when built (herocc), else Hero V2 (herov2); 'sandbox v2' / 'sandbox old'
            // ask for Hero V2 or the Didgori player
            var heroCC = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.PrefabPath);
            var heroV2 = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroV2.PrefabPath);
            var playerPrefab = arg == "old" ? null : arg == "v2" ? heroV2 : heroCC != null ? heroCC : heroV2;
            bool clean = arg == "clean";
            if (clean) playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKHeroCC.CleanPrefabPath);
            if (clean && playerPrefab == null) { GKBridge.Log("sandbox: no Knight_Clean prefab (run 'herocc clean')"); return; }
            string scenePath = clean ? CleanScenePath : ScenePath;
            if (playerPrefab == null) playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.PlayerPrefabPath);
            var enemyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GKGameBuilder.EnemyPrefabPath);
            if (playerPrefab == null || enemyPrefab == null) { GKBridge.Log("sandbox: player / enemy prefab missing (run 'game' once)"); return; }
            GKPaths.Ensure(GKPaths.Scenes); GKPaths.Ensure(Dir);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- sky, sun and post exactly as on the plateau, so materials read the same in both places
            var root = new GameObject("Environment");
            GKEnvironment.Root = root;
            GKEnvironment.BuildSkyAndLight();
            GKEnvironment.BuildPost();
            EditorUtility.ClearProgressBar();
            RenderSettings.fogStartDistance = 120f; RenderSettings.fogEndDistance = 1800f;

            // ---- the floor: 100 x 100 m, a 1 m grid with 10 m major lines
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor 100m";
            floor.transform.SetParent(root.transform, false);
            floor.transform.localScale = new Vector3(10f, 1f, 10f);
            floor.layer = GK.LayerTerrain;
            floor.isStatic = true;
            floor.GetComponent<MeshRenderer>().sharedMaterial = GridMaterial();

            // ---- reference blocks in one corner, away from the fight
            var refs = new GameObject("Reference").transform; refs.SetParent(root.transform, false);
            var blockMat = BlockMaterial();
            Block(refs, blockMat, "Step 0.25m", new Vector3(-14f, 0f, 12f), new Vector3(2f, 0.25f, 2f));
            Block(refs, blockMat, "Box 0.5m", new Vector3(-11f, 0f, 12f), new Vector3(1f, 0.5f, 1f));
            Block(refs, blockMat, "Box 1m", new Vector3(-9f, 0f, 12f), new Vector3(1f, 1f, 1f));
            Block(refs, blockMat, "Box 1.5m", new Vector3(-7f, 0f, 12f), new Vector3(1f, 1.5f, 1f));
            Block(refs, blockMat, "Wall 3m", new Vector3(13f, 0f, 13f), new Vector3(6f, 3f, 0.4f));
            Block(refs, blockMat, "Pillar 3m", new Vector3(10f, 0f, -12f), new Vector3(0.6f, 3f, 0.6f));

            // ---- navigation
            var navGo = new GameObject("NavMesh");
            var surface = navGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Volume;
            surface.center = Vector3.zero;
            surface.size = new Vector3(100f, 20f, 100f);
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = (1 << GK.LayerEnvironment) | (1 << GK.LayerTerrain);
            surface.BuildNavMesh();
            if (surface.navMeshData != null)
            {
                string np = Dir + (clean ? "/CleanSandboxNavMesh.asset" : "/SandboxNavMesh.asset");
                if (AssetDatabase.LoadAssetAtPath<Object>(np) != null) AssetDatabase.DeleteAsset(np);
                AssetDatabase.CreateAsset(surface.navMeshData, np);
            }

            // ---- the player, facing +z across the arena
            Vector3 spawn = new Vector3(0f, 0.05f, -4f);
            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            var idle = GKPreview.Clip("AX_standing_idle");
            if (idle != null) idle.SampleAnimation(player, 0.6f);
            player.transform.SetPositionAndRotation(spawn, Quaternion.identity);   // sampling a humanoid clip moves the root

            GKGameBuilder.BuildCameraRig(player, spawn);
            var waves = GKGameBuilder.BuildGameSystems(player, enemyPrefab, Vector3.zero);
            // practice: one opponent at a time first, close by
            waves.spawnRadius = 9f; waves.spawnSpread = 3f;
            waves.waves = new[]
            {
                new WaveManager.Wave { title = "Duel", warriors = 1, delayBefore = 3f },
                new WaveManager.Wave { title = "Two at once", warriors = 2, delayBefore = 4f },
                new WaveManager.Wave { title = "Veteran", veterans = 1, delayBefore = 4f },
                new WaveManager.Wave { title = "Group", warriors = 2, veterans = 1, delayBefore = 5f },
                new WaveManager.Wave { title = "Champion", champion = true, delayBefore = 5f },
            };

            EditorSceneManager.SaveScene(scene, scenePath);
            if (!EditorBuildSettings.scenes.Any(s => s.path == scenePath))
                EditorBuildSettings.scenes = EditorBuildSettings.scenes.Concat(new[] { new EditorBuildSettingsScene(scenePath, true) }).ToArray();

            // a still: over the shoulder, across the arena
            var shot = new GameObject("~shot").AddComponent<Camera>();
            shot.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing = true;
            shot.fieldOfView = 50f; shot.farClipPlane = 4000f; shot.nearClipPlane = 0.05f;
            shot.transform.position = spawn + new Vector3(0.9f, 1.75f, -3.2f);
            shot.transform.LookAt(spawn + new Vector3(-0.2f, 1.1f, 6f));
            GKPreview.Capture(shot, "sandbox_start");
            // the hero up close (face, full figure front and back) to judge materials against Character Creator
            var anim = player.GetComponent<Animator>();
            var head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head != null)
            {
                Vector3 hp = head.position + Vector3.up * 0.06f;
                shot.fieldOfView = 30f;
                shot.transform.position = hp + new Vector3(0.18f, 0.02f, 0.75f); shot.transform.LookAt(hp);
                GKPreview.Capture(shot, "sandbox_face", 1200, 1200);
                Vector3 mid = spawn + Vector3.up * 0.95f;
                shot.transform.position = mid + new Vector3(0.6f, 0.15f, 4.2f); shot.transform.LookAt(mid);
                GKPreview.Capture(shot, "sandbox_full", 900, 1400);
                shot.transform.position = mid + new Vector3(-0.6f, 0.15f, -4.2f); shot.transform.LookAt(mid);
                GKPreview.Capture(shot, "sandbox_back", 900, 1400);
            }
            Object.DestroyImmediate(shot.gameObject);
            EditorSceneManager.SaveScene(scene, scenePath);
            GKBridge.Log("sandbox ready: " + scenePath + " (player " + playerPrefab.name + ", navmesh " + (surface.navMeshData != null) + ", " + waves.waves.Length + " practice waves)");
        }

        static void Block(Transform parent, Material mat, string name, Vector3 foot, Vector3 size)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = name;
            b.transform.SetParent(parent, false);
            b.transform.position = foot + Vector3.up * size.y * 0.5f;
            b.transform.localScale = size;
            b.layer = GK.LayerEnvironment;
            b.isStatic = true;
            b.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static Material GridMaterial()
        {
            string tp = Dir + "/Grid10m.png", mp = Dir + "/SandboxGrid.mat";
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(tp) == null)
            {
                // one texture = 10 m: 1 m lines, a heavier line on its edge (the 10 m line)
                const int N = 1024; float pxPerM = N / 10f;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, true, false);
                var px = new Color32[N * N];
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float fx = x % pxPerM, fy = y % pxPerM;
                        bool minor = fx < 2f || fy < 2f;
                        bool major = x < 5 || y < 5;
                        float g = major ? 0.20f : minor ? 0.30f : 0.42f;
                        byte v = (byte)(Mathf.LinearToGammaSpace(g) * 255f);
                        px[y * N + x] = new Color32(v, v, v, 255);
                    }
                tex.SetPixels32(px); tex.Apply();
                System.IO.File.WriteAllBytes(tp, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(tp, ImportAssetOptions.ForceSynchronousImport);
                var ti = (TextureImporter)AssetImporter.GetAtPath(tp);
                ti.wrapMode = TextureWrapMode.Repeat; ti.anisoLevel = 8; ti.mipmapEnabled = true; ti.SaveAndReimport();
            }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, mp); }
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(tp));
            mat.SetTextureScale("_BaseMap", new Vector2(10f, 10f));   // the 100 m plane carries ten 10 m tiles
            mat.SetColor("_BaseColor", new Color(0.5f, 0.5f, 0.5f));   // mid-grey under the plateau sun, not glare
            mat.SetFloat("_Smoothness", 0.12f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material BlockMaterial()
        {
            string mp = Dir + "/SandboxBlock.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, mp); }
            mat.SetColor("_BaseColor", new Color(0.62f, 0.58f, 0.52f));
            mat.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
