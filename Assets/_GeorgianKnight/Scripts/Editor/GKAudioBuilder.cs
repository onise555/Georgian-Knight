using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    public static class GKAudioBuilder
    {
        static AudioClip[] Clips(string folder, string prefix)
        {
            string dir = GKPaths.Audio + "/" + folder;
            if (!AssetDatabase.IsValidFolder(dir)) return new AudioClip[0];
            return AssetDatabase.FindAssets("t:AudioClip", new[] { dir })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => System.IO.Path.GetFileName(p).StartsWith(prefix))
                .OrderBy(p => p)
                .Select(p => AssetDatabase.LoadAssetAtPath<AudioClip>(p)).Where(c => c != null).ToArray();
        }

        public static AudioLibrary Build()
        {
            GKPaths.Ensure(GKPaths.Data);
            string path = GKPaths.Data + "/AudioLibrary.asset";
            var lib = AssetDatabase.LoadAssetAtPath<AudioLibrary>(path);
            if (lib == null) { lib = ScriptableObject.CreateInstance<AudioLibrary>(); AssetDatabase.CreateAsset(lib, path); }
            lib.swingLight = Clips("SFX/Swing", "swing_light").Concat(Clips("SFX/Swing", "swing_fast")).ToArray();
            lib.swingHeavy = Clips("SFX/Swing", "swing_heavy").Concat(Clips("SFX/Swing", "swing_metal")).ToArray();
            lib.flesh = Clips("SFX/Flesh", "flesh_");
            lib.fleshHeavy = Clips("SFX/Flesh", "flesh_heavy");
            lib.flesh = lib.flesh.Where(c => !c.name.StartsWith("flesh_heavy")).ToArray();
            lib.block = Clips("SFX/Block", "");
            lib.parry = Clips("SFX/Parry", "");
            lib.kick = Clips("SFX/Kick", "");
            lib.bodyfall = Clips("SFX/Bodyfall", "");
            lib.stepWalk = Clips("SFX/Footsteps", "step_walk");
            lib.stepRun = Clips("SFX/Footsteps", "step_run");
            lib.stepSprint = Clips("SFX/Footsteps", "step_sprint");
            lib.chainmail = Clips("SFX/Armor", "chainmail");
            lib.gruntAttack = Clips("SFX/Voice", "grunt_attack");
            lib.gruntPain = Clips("SFX/Voice", "grunt_pain");
            lib.groanDeath = Clips("SFX/Voice", "groan_death");
            lib.battleCry = Clips("SFX/Voice", "battle_cry");
            lib.unsheathe = Clips("SFX/Sword", "unsheathe");
            lib.boom = Clips("SFX/Designed", "boom");
            lib.whoosh = Clips("SFX/Designed", "whoosh").Concat(Clips("SFX/Designed", "riser")).ToArray();
            lib.ambienceValley = Clips("Ambience", "amb_didgori_valley").FirstOrDefault();
            lib.ambienceWind = Clips("Ambience", "amb_wind").FirstOrDefault();
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            GKBridge.Log("audio library: swings " + (lib.swingLight.Length + lib.swingHeavy.Length) + ", flesh " + (lib.flesh.Length + lib.fleshHeavy.Length) +
                         ", steel " + (lib.block.Length + lib.parry.Length) + ", steps " + (lib.stepWalk.Length + lib.stepRun.Length + lib.stepSprint.Length) +
                         ", voices " + (lib.gruntAttack.Length + lib.gruntPain.Length + lib.battleCry.Length));
            return lib;
        }
    }
}
