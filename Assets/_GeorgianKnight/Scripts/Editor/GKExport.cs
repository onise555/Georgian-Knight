using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Exports a character's bind-pose mesh as OBJ (right-handed, metres) plus the Mixamo auto-rigger
    /// marker positions computed from its current skeleton — used to re-rig through Mixamo (fingers,
    /// better skin weights) without manual marker placement.
    /// </summary>
    public static class GKExport
    {
        static string F(float v) { return v.ToString("0.#####", CultureInfo.InvariantCulture); }

        public static void ExportForMixamo(string modelPath, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var inst = Object.Instantiate(model);
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;
            try
            {
                var map = GKCharacters.MapBones(inst.transform);
                Vector3 right, fwd;
                GKCharacters.CharacterFrame(map, out right, out fwd);
                // face +Z so Mixamo sees the front
                Quaternion face = Quaternion.FromToRotation(fwd, Vector3.forward);
                inst.transform.rotation = face;

                string name = Path.GetFileNameWithoutExtension(modelPath);
                var sb = new StringBuilder();
                sb.AppendLine("# Georgian Knight export for Mixamo");
                sb.AppendLine("mtllib " + name + ".mtl");
                int vBase = 1;
                foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var baked = new Mesh();
                    smr.BakeMesh(baked, true);
                    var vs = baked.vertices; var ns = baked.normals; var uv = baked.uv;
                    Vector3 o = smr.transform.position; Quaternion q = smr.transform.rotation;
                    sb.AppendLine("o " + smr.name);
                    for (int i = 0; i < vs.Length; i++) { Vector3 w = o + q * vs[i]; sb.Append("v ").Append(F(-w.x)).Append(' ').Append(F(w.y)).Append(' ').Append(F(w.z)).Append('\n'); }
                    for (int i = 0; i < uv.Length; i++) sb.Append("vt ").Append(F(uv[i].x)).Append(' ').Append(F(uv[i].y)).Append('\n');
                    for (int i = 0; i < ns.Length; i++) { Vector3 n = q * ns[i]; sb.Append("vn ").Append(F(-n.x)).Append(' ').Append(F(n.y)).Append(' ').Append(F(n.z)).Append('\n'); }
                    sb.AppendLine("usemtl body");
                    var tris = baked.triangles;
                    for (int i = 0; i < tris.Length; i += 3)
                    {
                        // mirrored X flips winding
                        int a = tris[i] + vBase, b = tris[i + 2] + vBase, c = tris[i + 1] + vBase;
                        sb.Append("f ").Append(a).Append('/').Append(a).Append('/').Append(a).Append(' ')
                          .Append(b).Append('/').Append(b).Append('/').Append(b).Append(' ')
                          .Append(c).Append('/').Append(c).Append('/').Append(c).Append('\n');
                    }
                    vBase += vs.Length;
                    Object.DestroyImmediate(baked);
                }
                File.WriteAllText(Path.Combine(outDir, name + ".obj"), sb.ToString());
                File.WriteAllText(Path.Combine(outDir, name + ".mtl"), "newmtl body\nKd 1 1 1\nmap_Kd " + name + "_tex.jpg\n");

                // base colour for Mixamo's preview (1K is plenty)
                var smr0 = inst.GetComponentInChildren<SkinnedMeshRenderer>();
                Texture2D baseTex = null;
                if (smr0 != null && smr0.sharedMaterial != null) baseTex = smr0.sharedMaterial.GetTexture("_BaseMap") as Texture2D;
                if (baseTex == null && GKCharacters.KnightMaterial != null) baseTex = GKCharacters.KnightMaterial.GetTexture("_BaseMap") as Texture2D;
                if (baseTex != null)
                {
                    var rt = RenderTexture.GetTemporary(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(baseTex, rt);
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var t = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
                    t.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); t.Apply();
                    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                    File.WriteAllBytes(Path.Combine(outDir, name + "_tex.jpg"), t.EncodeToJPG(88));
                    Object.DestroyImmediate(t);
                }

                // Mixamo auto-rigger markers (character-local, same space as the OBJ)
                System.Func<Vector3, string> P = w => "{\"x\":" + F(-w.x) + ",\"y\":" + F(w.y) + ",\"z\":" + F(w.z) + "}";
                Vector3 fw = Vector3.forward;
                Transform head = map["Head"], lh = map["LeftHand"], rh = map["RightHand"], le = map["LeftLowerArm"], re = map["RightLowerArm"];
                Transform lk = map["LeftLowerLeg"], rk = map["RightLowerLeg"], lul = map["LeftUpperLeg"], rul = map["RightUpperLeg"];
                Transform neck; map.TryGetValue("Neck", out neck);
                float headLen = neck != null ? Mathf.Max(0.06f, head.position.y - neck.position.y) : 0.1f;
                Vector3 chin = head.position + Vector3.up * (headLen * 0.25f) + fw * 0.06f;
                Vector3 groin = (lul.position + rul.position) * 0.5f + Vector3.down * 0.06f;
                string json = "{\"chin\":" + P(chin) + ",\"larm\":" + P(lh.position) + ",\"rarm\":" + P(rh.position) +
                              ",\"lelbow\":" + P(le.position) + ",\"relbow\":" + P(re.position) +
                              ",\"lknee\":" + P(lk.position) + ",\"rknee\":" + P(rk.position) + ",\"groin\":" + P(groin) + "}";
                File.WriteAllText(Path.Combine(outDir, name + "_markers.json"), json);
                GKBridge.Log("exported " + name + " for Mixamo -> " + outDir + " markers " + json);
            }
            finally { Object.DestroyImmediate(inst); }
        }
    }
}
