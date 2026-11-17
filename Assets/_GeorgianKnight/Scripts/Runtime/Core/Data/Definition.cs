using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// Base of every piece of authored content (items, skills, enemies, quests …). The <see cref="Id"/> is the stable
    /// key used by save files, localization and cross references; it never changes once shipped.
    /// </summary>
    public abstract class Definition : ScriptableObject
    {
        [SerializeField] string id;
        [SerializeField] Sprite icon;
        [Tooltip("Optional override; defaults to '<prefix>.<id>.name'")]
        [SerializeField] string nameKey;
        [SerializeField] string descriptionKey;

        public string Id { get { return id; } }
        public Sprite Icon { get { return icon; } }

        /// <summary>Localization namespace of this kind of content, e.g. "item", "skill", "enemy".</summary>
        public abstract string KeyPrefix { get; }

        public string NameKey { get { return string.IsNullOrEmpty(nameKey) ? KeyPrefix + "." + id + ".name" : nameKey; } }
        public string DescriptionKey { get { return string.IsNullOrEmpty(descriptionKey) ? KeyPrefix + "." + id + ".desc" : descriptionKey; } }
        public string DisplayName { get { return Loc.Get(NameKey); } }
        public string Description { get { return Loc.Get(DescriptionKey); } }

        public override string ToString() { return GetType().Name + "(" + id + ")"; }

#if UNITY_EDITOR
        /// <summary>Editor/builder only.</summary>
        public void EditorSetIdentity(string newId, Sprite newIcon = null)
        {
            id = newId;
            if (newIcon != null) icon = newIcon;
        }

        protected virtual void OnValidate()
        {
            if (string.IsNullOrEmpty(id)) id = IdUtil.Sanitize(name);
        }
#endif
    }

    public static class IdUtil
    {
        /// <summary>lower_snake_case ascii id from any asset name.</summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new System.Text.StringBuilder(s.Length);
            bool lastUnderscore = false;
            foreach (char ch in s.Trim())
            {
                char c = char.ToLowerInvariant(ch);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.') { sb.Append(c); lastUnderscore = false; }
                else if (!lastUnderscore && sb.Length > 0) { sb.Append('_'); lastUnderscore = true; }
            }
            return sb.ToString().TrimEnd('_');
        }
    }
}
