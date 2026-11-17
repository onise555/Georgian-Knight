using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Imports Blender-built architecture (FBX with named material slots M_House_* and a "House_Collision" mesh):
    /// builds URP materials from the texture sets beside the FBX, remaps the slots, and places the asset in a scene
    /// with its collision mesh as a MeshCollider (renderer off) on the environment layer, static.
    /// </summary>
    public static class GKHouseImport
    {
        public const string Dir = GKPaths.Environment + "/Houses";
        public const string TestHouse = Dir + "/House_Test01.fbx";
        static readonly string[] Pbr = { "M_House_WallCobble", "M_House_Brick", "M_House_Plaster", "M_House_Wood", "M_House_Roof", "M_House_Plinth" };

        public static GameObject PlaceTestHouse(Vector3 spawn)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(TestHouse);
            if (model == null) return null;
            PrepareMaterials(TestHouse);
            model = AssetDatabase.LoadAssetAtPath<GameObject>(TestHouse);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = "House_Test01";
            Vector3 p = spawn + new Vector3(3.5f, 0f, 16f);
            p.y = MinHeightUnder(p, 4.2f, 3.6f) - 0.12f;       // sit on the lowest ground point, plinth sunk 12 cm
            go.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, 180f, 0f));
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI |
                                                       StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
                t.gameObject.layer = GK.LayerEnvironment;
                if (t.name == "House_Collision")
                {
                    var mf = t.GetComponent<MeshFilter>();
                    var mc = t.gameObject.AddComponent<MeshCollider>();
                    if (mf != null) mc.sharedMesh = mf.sharedMesh;
                    var mr = t.GetComponent<MeshRenderer>(); if (mr != null) mr.enabled = false;
                }
            }
            SetupLadder(go);
            GKBridge.Log("house: placed " + go.name + " at " + p.ToString("F1"));
            return go;
        }

        /// <summary>
        /// Turns the ladder markers exported from Blender (GK_LadderRung_NN, GK_LadderFoot, GK_LadderStandBottom,
        /// GK_LadderStandTop; positions only) into a climbable <see cref="Ladder"/>.
        /// </summary>
        public static Ladder SetupLadder(GameObject house)
        {
            var all = house.GetComponentsInChildren<Transform>(true);
            var rungs = all.Where(t => t.name.StartsWith("GK_LadderRung_")).OrderBy(t => t.name, System.StringComparer.Ordinal).ToList();
            Transform foot = all.FirstOrDefault(t => t.name == "GK_LadderFoot");
            Transform sb = all.FirstOrDefault(t => t.name == "GK_LadderStandBottom");
            Transform st = all.FirstOrDefault(t => t.name == "GK_LadderStandTop");
            if (rungs.Count < 2 || foot == null || sb == null || st == null)
            {
                GKBridge.Log("house: no ladder markers (rungs " + rungs.Count + ")");
                return null;
            }
            var lad = house.GetComponent<Ladder>() ?? house.AddComponent<Ladder>();
            lad.rungFirst = rungs[0]; lad.rungLast = rungs[rungs.Count - 1]; lad.rungCount = rungs.Count;
            lad.foot = foot; lad.standBottom = sb; lad.standTop = st;
            GKBridge.Log("house: ladder " + rungs.Count + " rungs, pitch " + lad.Pitch.ToString("F3") + " m, lean " +
                         Vector3.Angle(lad.Up, Vector3.up).ToString("F1") + " deg, top " + lad.TopHeight.ToString("F2"));
            return lad;
        }

        static float MinHeightUnder(Vector3 c, float hx, float hz)
        {
            float m = float.MaxValue;
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                    m = Mathf.Min(m, GKEnvironment.SampleHeight(c + new Vector3(i * hx, 0f, j * hz)));
            return m;
        }

        [GKCommand("housemat", "housemat: rebuild the house materials and slot remaps")]
        static void HouseMat(string a) { PrepareMaterials(TestHouse); }

        public static void PrepareMaterials(string fbx)
        {
            GKPaths.Ensure(GKPaths.Materials);
            var imp = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (imp == null) { GKBridge.Log("house: no model importer for " + fbx); return; }
            // remaps only apply when the importer creates materials at all (our importer defaults environment models to None)
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            string texDir = Path.GetDirectoryName(fbx).Replace('\\', '/') + "/Textures/";
            foreach (var n in Pbr)
            {
                var mat = GKCharacters.MakePbr(GKPaths.Materials + "/" + n + ".mat", texDir + n, 1f, 0f);
                if (mat == null) { GKBridge.Log("house: missing textures for " + n); continue; }
                mat.SetFloat("_EnvironmentReflections", 1f);
                imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), n), mat);
            }
            imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "M_House_Void"), Flat("M_House_Void", new Color(0.012f, 0.010f, 0.009f), 0f, 0.0f));
            imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "M_House_Iron"), Flat("M_House_Iron", new Color(0.09f, 0.085f, 0.08f), 1f, 0.42f));
            imp.importNormals = ModelImporterNormals.Import;
            imp.importTangents = ModelImporterTangents.CalculateMikk;
            imp.SaveAndReimport();
            AssetDatabase.SaveAssets();
            GKBridge.Log("house: materials ready for " + fbx);
        }

        static Material Flat(string name, Color c, float metal, float smooth)
        {
            string path = GKPaths.Materials + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c); m.SetFloat("_Metallic", metal); m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
