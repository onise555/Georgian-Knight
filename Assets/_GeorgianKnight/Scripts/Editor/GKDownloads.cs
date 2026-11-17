using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace GeorgianKnight.EditorTools
{
    /// <summary>CC0 texture / HDRI fetching from Poly Haven (cached under Art/Environment/Downloaded).</summary>
    public static class GKDownloads
    {
        static string Abs(string assetPath) { return Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath); }

        public static bool Fetch(string url, string assetPath)
        {
            string abs = Abs(assetPath);
            if (File.Exists(abs) && new FileInfo(abs).Length > 1024) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 180;
                var op = req.SendWebRequest();
                var t0 = EditorApplication.timeSinceStartup;
                while (!op.isDone)
                {
                    EditorUtility.DisplayProgressBar("Georgian Knight", "Downloading " + Path.GetFileName(assetPath), req.downloadProgress);
                    System.Threading.Thread.Sleep(15);
                    if (EditorApplication.timeSinceStartup - t0 > 240) break;
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    GKBridge.Log("download failed " + url + " : " + req.error);
                    return false;
                }
                File.WriteAllBytes(abs, req.downloadHandler.data);
            }
            return true;
        }

        public class PbrSet
        {
            public string id;
            public Texture2D diffuse, normal, mask;   // mask = URP terrain/ Lit mask (R metal, G AO, B height, A smoothness)
            public bool ok;
        }

        /// <summary>Downloads diff / nor_gl / arm / disp (2k jpg) and builds a packed mask.</summary>
        public static PbrSet Texture(string id, string res = "2k")
        {
            var set = new PbrSet { id = id };
            string dir = GKPaths.Downloaded + "/" + id;
            string baseUrl = "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/" + res + "/" + id + "/" + id + "_";
            bool d = Fetch(baseUrl + "diff_" + res + ".jpg", dir + "/" + id + "_diff.jpg");
            bool n = Fetch(baseUrl + "nor_gl_" + res + ".jpg", dir + "/" + id + "_nor_gl.jpg");
            bool a = Fetch(baseUrl + "arm_" + res + ".jpg", dir + "/" + id + "_arm.jpg");
            bool h = Fetch(baseUrl + "disp_" + res + ".jpg", dir + "/" + id + "_disp.jpg");
            if (!d) return set;
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            set.diffuse = AssetDatabase.LoadAssetAtPath<Texture2D>(dir + "/" + id + "_diff.jpg");
            if (n)
            {
                string np = dir + "/" + id + "_nor_gl.jpg";
                var ti = AssetImporter.GetAtPath(np) as TextureImporter;
                if (ti != null && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); }
                set.normal = AssetDatabase.LoadAssetAtPath<Texture2D>(np);
            }
            if (a)
            {
                string mp = dir + "/" + id + "_mask.png";
                if (!File.Exists(Abs(mp)))
                {
                    var arm = GKCharacters.LoadFileTexture(dir + "/" + id + "_arm.jpg", true);
                    var disp = h ? GKCharacters.LoadFileTexture(dir + "/" + id + "_disp.jpg", true) : null;
                    if (disp != null && (disp.width != arm.width || disp.height != arm.height)) disp = GKCharacters.Resize(disp, arm.width, arm.height);
                    var ap = arm.GetPixels32();
                    var dp = disp != null ? disp.GetPixels32() : null;
                    var o = new Color32[ap.Length];
                    for (int i = 0; i < ap.Length; i++)
                        o[i] = new Color32(ap[i].b, ap[i].r, dp != null ? dp[i].r : (byte)128, (byte)(255 - ap[i].g));
                    var t = new Texture2D(arm.width, arm.height, TextureFormat.RGBA32, true, true);
                    t.SetPixels32(o); t.Apply();
                    File.WriteAllBytes(Abs(mp), t.EncodeToPNG());
                    Object.DestroyImmediate(t); Object.DestroyImmediate(arm); if (disp != null) Object.DestroyImmediate(disp);
                    AssetDatabase.ImportAsset(mp, ImportAssetOptions.ForceSynchronousImport);
                    var ti = AssetImporter.GetAtPath(mp) as TextureImporter;
                    if (ti != null) { ti.sRGBTexture = false; ti.SaveAndReimport(); }
                }
                set.mask = AssetDatabase.LoadAssetAtPath<Texture2D>(mp);
            }
            set.ok = set.diffuse != null;
            return set;
        }

        public static string Hdri(string id, string res = "4k")
        {
            string p = GKPaths.Downloaded + "/hdri/" + id + "_" + res + ".hdr";
            if (!Fetch("https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/" + res + "/" + id + "_" + res + ".hdr", p)) return null;
            AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceSynchronousImport);
            var ti = AssetImporter.GetAtPath(p) as TextureImporter;
            if (ti != null && (ti.mipmapEnabled || ti.wrapModeV != TextureWrapMode.Clamp))
            {
                ti.mipmapEnabled = false;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.maxTextureSize = 8192;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            return p;
        }

        /// <summary>
        /// Finds the sun in a Radiance .hdr (RGBE) file. Returns texture coordinates (u right, v up) of the
        /// brightest region.
        /// </summary>
        public static bool FindSunUV(string assetPath, out Vector2 uv)
        {
            uv = new Vector2(0.5f, 0.75f);
            byte[] b;
            try { b = File.ReadAllBytes(Abs(assetPath)); } catch { return false; }
            int pos = 0;
            int W = 0, H = 0;
            // header
            while (pos < b.Length)
            {
                int start = pos;
                while (pos < b.Length && b[pos] != '\n') pos++;
                string line = System.Text.Encoding.ASCII.GetString(b, start, pos - start);
                pos++;
                if (line.StartsWith("-Y "))
                {
                    var parts = line.Split(' ');
                    H = int.Parse(parts[1]); W = int.Parse(parts[3]);
                    break;
                }
            }
            if (W == 0 || H == 0) return false;
            const int GW = 256, GH = 128;
            var grid = new double[GW * GH];
            var scan = new byte[W * 4];
            for (int y = 0; y < H; y++)
            {
                if (pos + 4 > b.Length) break;
                if (b[pos] == 2 && b[pos + 1] == 2 && ((b[pos + 2] << 8) | b[pos + 3]) == W)
                {
                    pos += 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int x = 0;
                        while (x < W && pos < b.Length)
                        {
                            int count = b[pos++];
                            if (count > 128)
                            {
                                count -= 128;
                                byte v = b[pos++];
                                for (int i = 0; i < count && x < W; i++) scan[(x++) * 4 + c] = v;
                            }
                            else
                            {
                                for (int i = 0; i < count && x < W; i++) scan[(x++) * 4 + c] = b[pos++];
                            }
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < W && pos + 3 < b.Length; x++) { scan[x * 4] = b[pos]; scan[x * 4 + 1] = b[pos + 1]; scan[x * 4 + 2] = b[pos + 2]; scan[x * 4 + 3] = b[pos + 3]; pos += 4; }
                }
                int gy = y * GH / H;
                for (int x = 0; x < W; x++)
                {
                    int e = scan[x * 4 + 3];
                    if (e == 0) continue;
                    double f = System.Math.Pow(2, e - 136);
                    double lum = (0.2126 * scan[x * 4] + 0.7152 * scan[x * 4 + 1] + 0.0722 * scan[x * 4 + 2]) * f;
                    grid[gy * GW + x * GW / W] += lum;
                }
            }
            int best = 0;
            for (int i = 1; i < grid.Length; i++) if (grid[i] > grid[best]) best = i;
            int bx = best % GW, by = best / GW;
            uv = new Vector2((bx + 0.5f) / GW, 1f - (by + 0.5f) / GH);
            return true;
        }

        /// <summary>Direction (sky space, before _Rotation) that Skybox/Panoramic samples at a texture uv.</summary>
        public static Vector3 PanoramicDir(Vector2 uv)
        {
            float lat = (1f - uv.y) * Mathf.PI;
            float lon = (0.5f - uv.x) * 2f * Mathf.PI;
            return new Vector3(Mathf.Sin(lat) * Mathf.Cos(lon), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(lon)).normalized;
        }
    }
}
