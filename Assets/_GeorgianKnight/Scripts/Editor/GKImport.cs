using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>Import rules for everything under Assets/_GeorgianKnight.</summary>
    public class GKImport : AssetPostprocessor
    {
        static bool Under(string path, string folder) { return path.Replace('\\', '/').StartsWith(folder + "/"); }

        void OnPreprocessModel()
        {
            var mi = assetImporter as ModelImporter;
            if (mi == null || !Under(assetPath, GKPaths.Root)) return;
            if (Under(assetPath, GKPaths.Mixamo) && IsTraversal(Path.GetFileNameWithoutExtension(assetPath)) && TraversalRig(mi)) return;
            if (!mi.importSettingsMissing) return; // never fight settings applied later by the builders

            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importBlendShapes = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;

            if (Under(assetPath, GKPaths.Characters))
            {
                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                mi.importAnimation = false;
                mi.optimizeGameObjects = false;
                mi.isReadable = true;
                mi.importNormals = ModelImporterNormals.Import;
                mi.importTangents = ModelImporterTangents.CalculateMikk;
            }
            else if (Under(assetPath, GKPaths.Mixamo))
            {
                mi.animationType = ModelImporterAnimationType.Human;
                mi.importAnimation = true;
                mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
                mi.animationRotationError = 0.2f;
                mi.animationPositionError = 0.2f;
                mi.animationScaleError = 0.5f;
                // clips from other rigs keep their own skeleton's avatar: Kevin Iglesias (KI_, HumanM model) and
                // MoCap Online (MO_, MotusMan v50 T pose); the rig models themselves build it
                string fn = Path.GetFileNameWithoutExtension(assetPath);
                string rig = fn.StartsWith("KI_") ? GKPaths.Mixamo + "/KI_HumanM_Model.fbx" :
                             fn.StartsWith("MO_") ? GKPaths.Mixamo + "/MO_MotusMan_TPose.fbx" : GKPaths.XBotFbx;
                // ActionForge (AF_, UE5 mannequin skeleton), Rokoko (RK_) and clips retargeted onto X Bot in Blender (XR_,
                // whose bone axes Blender rewrote) carry their own skinned body:
                // each builds its own humanoid avatar
                bool isXBot = fn == "X Bot" || fn == "KI_HumanM_Model" || fn == "MO_MotusMan_TPose" || fn.StartsWith("AF_") || fn.StartsWith("RK_") || fn.StartsWith("XR_");
                Avatar src = isXBot ? null : AssetDatabase.LoadAssetAtPath<Avatar>(rig);
                if (src != null && src.isHuman)
                {
                    mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                    mi.sourceAvatar = src;
                }
                else mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            }
            else if (Under(assetPath, GKPaths.Weapons) || Under(assetPath, GKPaths.Environment))
            {
                mi.animationType = ModelImporterAnimationType.None;
                mi.importAnimation = false;
                mi.isReadable = true;
                mi.importTangents = ModelImporterTangents.CalculateMikk;
            }
        }

        void OnPreprocessAnimation()
        {
            var mi = assetImporter as ModelImporter;
            if (mi == null || !Under(assetPath, GKPaths.Mixamo)) return;
            var clips = mi.defaultClipAnimations;
            if (clips == null || clips.Length == 0) return;
            string n = Path.GetFileNameWithoutExtension(assetPath);
            // userData "trim:<frames cut from the start>[;end:<last frame kept>]"
            float trim = 0f, end = -1f;
            if (!string.IsNullOrEmpty(mi.userData))
                foreach (var kv in mi.userData.Split(';'))
                {
                    if (kv.StartsWith("trim:")) float.TryParse(kv.Substring(5), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out trim);
                    else if (kv.StartsWith("end:")) float.TryParse(kv.Substring(4), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out end);
                }
            for (int i = 0; i < clips.Length; i++)
            {
                var c = clips[i];
                if (end > 0f) c.lastFrame = Mathf.Min(c.lastFrame, end);
                if (trim > 0f) c.firstFrame = Mathf.Min(c.lastFrame - 6f, c.firstFrame + trim);
                c.name = clips.Length == 1 ? n : n + "_" + i;
                bool loop = IsLoop(n);
                bool trav = IsTraversal(n);
                c.loopTime = loop;
                // a rising loop (ladder) must not have its end pose pulled onto its start pose
                c.loopPose = loop && !trav;
                c.lockRootRotation = !IsTurn(n);
                // ladder clips: everything baked into the pose (the climber steps the transform itself and drives the
                // clip time), the body's own facing defines forward, the transform sits under the centre of mass
                c.keepOriginalOrientation = !trav;
                c.lockRootHeightY = true;
                c.keepOriginalPositionY = true;
                c.heightFromFeet = false;
                c.lockRootPositionXZ = trav;
                // Mixamo's one-handed slashes and thrusts lunge with the hips, not the root: their travel becomes root
                // motion (centre of mass), so the body moves with the step and nothing slides back when the cut ends
                bool lunge = n.StartsWith("MX_") && (n.Contains("Slash") || n.Contains("Thrust"));
                c.keepOriginalPositionXZ = !trav && !lunge;
                c.mirror = false;
                clips[i] = c;
            }
            mi.clipAnimations = clips;
        }

        public static bool IsTraversal(string n) { return n.StartsWith("GK_Ladder_"); }

        /// <summary>
        /// The ladder clips were exported from Mixamo on the knight's own rig, whose file units are tiny (the knight
        /// model is scaled up on import). Copying the X Bot avatar would normalise their translation to nothing (the
        /// climb would not rise), so they use the knight's avatar and the knight's import scale: same skeleton, same
        /// units, exact root motion. Applied on every import (it must hold for reimports too).
        /// </summary>
        static bool TraversalRig(ModelImporter mi)
        {
            var knight = AssetImporter.GetAtPath(GKPaths.KnightFbx) as ModelImporter;
            var avatar = AssetDatabase.LoadAllAssetsAtPath(GKPaths.KnightFbx).OfType<Avatar>().FirstOrDefault();
            if (knight == null || avatar == null || !avatar.isHuman) return false;
            mi.importCameras = false; mi.importLights = false; mi.importVisibility = false; mi.importBlendShapes = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.globalScale = knight.globalScale;
            mi.useFileScale = knight.useFileScale;
            mi.animationType = ModelImporterAnimationType.Human;
            mi.importAnimation = true;
            mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            mi.animationRotationError = 0.2f; mi.animationPositionError = 0.2f; mi.animationScaleError = 0.5f;
            mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            mi.sourceAvatar = avatar;
            return true;
        }

        public static bool IsLoop(string n)
        {
            string s = n.ToLowerInvariant();
            if (s.StartsWith("gk_ladder_climb")) return true;
            if (s.Contains("to_") || s.Contains("_to") || s.Contains("turn") || s.Contains("jump") || s.Contains("death")) return false;
            string[] oneShots = { "roll", "dodge", "attack", "slash", "combo", "hit", "react", "impact", "kick", "draw", "sheath",
                                  "exec", "getup", "kd_", "step", "stab", "melee", "casting", "power", "taunt", "equip", "disarm", "kneel", "uppercut" };
            foreach (var o in oneShots) if (s.Contains(o)) return false;
            return s.Contains("idle") || s.Contains("walk") || s.Contains("run") || s.Contains("strafe") || s.Contains("block_idle")
                || s.Contains("crouch_idle") || s.Contains("crouch_block_idle") || s.EndsWith("_loop");
        }

        static bool IsTurn(string n)
        {
            string s = n.ToLowerInvariant();
            return s.Contains("turn");
        }

        void OnPreprocessTexture()
        {
            var ti = assetImporter as TextureImporter;
            if (ti == null || !Under(assetPath, GKPaths.Root)) return;
            if (!ti.importSettingsMissing) return;
            string n = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
            bool hero = Under(assetPath, GKPaths.Characters) || Under(assetPath, GKPaths.Weapons);
            ti.maxTextureSize = hero ? 4096 : 2048;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.anisoLevel = 8;
            ti.mipmapEnabled = true;
            if (n.Contains("normal") || n.Contains("_nor_gl") || n.EndsWith("_nrm"))
            {
                ti.textureType = TextureImporterType.NormalMap;
            }
            else if (n.Contains("metallic") || n.Contains("roughness") || n.Contains("_arm") || n.Contains("_rough") || n.Contains("_disp")
                     || n.Contains("_mask") || n.Contains("metalsmooth") || n.Contains("occlusion") || n.EndsWith("_ao"))
            {
                ti.sRGBTexture = false;
            }
            if (n.EndsWith("_sky") || assetPath.EndsWith(".hdr") || assetPath.EndsWith(".exr"))
            {
                ti.sRGBTexture = false;
                ti.mipmapEnabled = false;
                ti.maxTextureSize = 8192;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
            }
            if (n.StartsWith("grass_blade") || n.Contains("_alpha"))
            {
                ti.alphaIsTransparency = true;
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.4f;
                ti.wrapMode = TextureWrapMode.Clamp;
            }
        }

        void OnPreprocessAudio()
        {
            var ai = assetImporter as AudioImporter;
            if (ai == null || !Under(assetPath, GKPaths.Root)) return;
            if (!ai.importSettingsMissing) return;
            var s = ai.defaultSampleSettings;
            bool ambience = assetPath.ToLowerInvariant().Contains("/ambience/") || assetPath.ToLowerInvariant().Contains("/music/");
            s.loadType = ambience ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = ambience ? 0.6f : 0.85f;
            s.preloadAudioData = !ambience;
            ai.defaultSampleSettings = s;
            ai.loadInBackground = ambience;
        }
    }
}
