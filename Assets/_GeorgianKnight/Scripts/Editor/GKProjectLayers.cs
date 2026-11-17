using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Contract C14 as data: the project's physics layers and which layers each one collides with. Layers are added
    /// through the running editor (TagManager / Physics API) rather than by editing ProjectSettings files, because the
    /// open editor keeps its own copy of them and would write it back over a hand edit.
    /// </summary>
    public static class GKProjectLayers
    {
        struct LayerSpec
        {
            public int index;
            public string name;
            public string[] collidesWith;   // null = leave the matrix row as it is
            public string why;
        }

        static readonly LayerSpec[] Specs =
        {
            new LayerSpec { index = 8, name = "Character", why = "CharacterControllers of the player and enemies" },
            new LayerSpec { index = 9, name = "Hurtbox", why = "bone hurtboxes / ragdoll colliders" },
            new LayerSpec { index = 10, name = "Terrain", why = "terrain" },
            new LayerSpec { index = 11, name = "Interactable", why = "interaction volumes" },
            new LayerSpec { index = 12, name = "ClothCollider", why = "cloth collision proxies" },
            new LayerSpec
            {
                index = 13, name = "PlayerBlocker", collidesWith = new[] { "Character" },
                why = "invisible walls for the player only (World places them; enemies set CharacterController.excludeLayers)",
            },
        };

        [GKCommand("tools_layers", "tools_layers [check] - add missing C14 layers and set their collision rows; 'check' only reports")]
        public static void Layers(string arg)
        {
            bool apply = arg.Trim() != "check";
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            var report = new List<string>();
            bool changed = false;
            foreach (var s in Specs)
            {
                var slot = layers.GetArrayElementAtIndex(s.index);
                if (slot.stringValue != s.name)
                {
                    if (slot.stringValue.Length > 0) { report.Add(s.index + " is '" + slot.stringValue + "', expected '" + s.name + "': left alone"); continue; }
                    if (apply) { slot.stringValue = s.name; changed = true; }
                    report.Add((apply ? "added " : "missing ") + s.index + " " + s.name);
                }
                if (s.collidesWith == null) continue;
                var allowed = new HashSet<int>();
                foreach (var n in s.collidesWith) allowed.Add(IndexOf(layers, n));
                int wrong = 0;
                for (int other = 0; other < 32; other++)
                {
                    bool collide = allowed.Contains(other);
                    if (Physics.GetIgnoreLayerCollision(s.index, other) != collide) continue;
                    wrong++;
                    if (apply) { Physics.IgnoreLayerCollision(s.index, other, !collide); changed = true; }
                }
                if (wrong > 0)
                    report.Add(s.name + " matrix row " + (apply ? "set" : "wrong in " + wrong + " cells") + ": collides only with " +
                               string.Join(", ", s.collidesWith));
            }
            if (changed)
            {
                tagManager.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
            }
            var rows = new List<string>();
            foreach (var s in Specs) rows.Add(s.index + "=" + s.name);
            GKBridge.Log("tools_layers: " + (report.Count == 0 ? "all C14 layers in place" : string.Join("; ", report.ToArray())) +
                         " | " + string.Join(" ", rows.ToArray()));
        }

        static int IndexOf(SerializedProperty layers, string name)
        {
            for (int i = 0; i < 32; i++) if (layers.GetArrayElementAtIndex(i).stringValue == name) return i;
            return -1;
        }
    }
}
