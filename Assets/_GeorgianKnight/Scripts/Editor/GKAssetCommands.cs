using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// CC0 asset discovery for the art pipeline (the editor has internet; the build machines may not):
    ///   phsearch word1,word2 [type]   list Poly Haven assets (textures | models | hdris) whose id, name, tags or
    ///                                 categories contain every word; writes GKBridge/phsearch.txt
    ///   phfetch id [res]              downloads a texture set (diff, nor_gl, arm, disp) into Art/Environment/Downloaded
    /// </summary>
    public static class GKAssetCommands
    {
        [GKCommand("phsearch", "phsearch word1,word2 [textures|models|hdris]: search Poly Haven (CC0)")]
        static void Search(string arg)
        {
            var parts = (arg ?? "").Split(' ');
            string[] words = parts[0].ToLowerInvariant().Split(',');
            string type = parts.Length > 1 ? parts[1] : "textures";
            string json = Get("https://api.polyhaven.com/assets?t=" + type);
            if (json == null) return;
            // top-level object: "id":{"name":"…", … "categories":[…], "tags":[…] …}
            var rx = new Regex("\"([a-z0-9_]+)\":\\{\"name\":\"([^\"]*)\"(.*?)\"tags\":\\[([^\\]]*)\\]", RegexOptions.Singleline);
            var sb = new StringBuilder();
            int n = 0;
            foreach (Match m in rx.Matches(json))
            {
                string id = m.Groups[1].Value, name = m.Groups[2].Value, rest = m.Groups[3].Value, tags = m.Groups[4].Value;
                var cat = Regex.Match(rest, "\"categories\":\\[([^\\]]*)\\]");
                string hay = (id + " " + name + " " + tags + " " + (cat.Success ? cat.Groups[1].Value : "")).ToLowerInvariant();
                bool all = true;
                foreach (var w in words) if (w.Length > 0 && !hay.Contains(w)) { all = false; break; }
                if (!all) continue;
                n++;
                sb.AppendLine(id + " | " + name + " | cat " + (cat.Success ? cat.Groups[1].Value.Replace("\"", "") : "") + " | tags " + tags.Replace("\"", ""));
            }
            string outPath = Path.Combine(GKBridge.Dir, "phsearch.txt");
            File.WriteAllText(outPath, sb.ToString());
            GKBridge.Log("phsearch " + arg + ": " + n + " match(es) -> GKBridge/phsearch.txt");
        }

        [GKCommand("phfetch", "phfetch id[,id2…] [1k|2k|4k]: download Poly Haven texture sets")]
        static void Fetch(string arg)
        {
            var parts = (arg ?? "").Split(' ');
            string res = parts.Length > 1 ? parts[1] : "2k";
            foreach (var id in parts[0].Split(','))
            {
                if (id.Length == 0) continue;
                var set = GKDownloads.Texture(id, res);
                GKBridge.Log("phfetch " + id + " " + res + ": " + (set.diffuse != null ? "ok" : "FAILED"));
            }
            UnityEditor.EditorUtility.ClearProgressBar();
        }

        /// <summary>Downloads one file (e.g. a Mixamo export link the owner approved) into GKBridge/dl/ — outside
        /// Assets, so nothing imports until the file has been checked and moved into place. The URL is not logged.</summary>
        [GKCommand("urlfetch", "urlfetch <url> <fileName>: download a file into GKBridge/dl/")]
        static void UrlFetch(string arg)
        {
            var parts = (arg ?? "").Trim().Split(' ');
            if (parts.Length < 2 || !parts[0].StartsWith("https://")) { GKBridge.Log("urlfetch: usage urlfetch <https-url> <fileName>"); return; }
            string name = Path.GetFileName(parts[1]);
            string dir = Path.Combine(GKBridge.Dir, "dl");
            Directory.CreateDirectory(dir);
            using (var req = UnityWebRequest.Get(parts[0]))
            {
                string dest = Path.Combine(dir, name);
                req.downloadHandler = new DownloadHandlerFile(dest) { removeFileOnAbort = true };   // streams to disk (packs of 100s of MB)
                req.timeout = 1500;
                var op = req.SendWebRequest();
                var t0 = UnityEditor.EditorApplication.timeSinceStartup;
                while (!op.isDone)
                {
                    UnityEditor.EditorUtility.DisplayProgressBar("Georgian Knight", "Downloading " + name, req.downloadProgress);
                    System.Threading.Thread.Sleep(30);
                    if (UnityEditor.EditorApplication.timeSinceStartup - t0 > 1550) break;
                }
                UnityEditor.EditorUtility.ClearProgressBar();
                if (req.result != UnityWebRequest.Result.Success) { GKBridge.Log("urlfetch " + name + ": FAILED " + req.error); return; }
                GKBridge.Log("urlfetch " + name + ": ok " + new FileInfo(dest).Length + " bytes");
                return;
            }
        }

        static string Get(string url)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 60;
                var op = req.SendWebRequest();
                var t0 = UnityEditor.EditorApplication.timeSinceStartup;
                while (!op.isDone)
                {
                    System.Threading.Thread.Sleep(15);
                    if (UnityEditor.EditorApplication.timeSinceStartup - t0 > 70) break;
                }
                if (req.result != UnityWebRequest.Result.Success) { GKBridge.Log("GET failed " + url + ": " + req.error); return null; }
                return req.downloadHandler.text;
            }
        }
    }
}
