using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// Index of every <see cref="Definition"/> in the game, rebuilt by the editor (Georgian Knight ▸ Rebuild Database).
    /// Runtime lookups are by id; the type parameter guards against id collisions across kinds.
    /// </summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Game Database", fileName = "GameDatabase")]
    public sealed class GameDatabase : ScriptableObject
    {
        [SerializeField] List<Definition> definitions = new List<Definition>();

        Dictionary<string, Definition> byId;
        readonly Dictionary<Type, Array> byType = new Dictionary<Type, Array>();

        public int Count { get { return definitions.Count; } }

        void Build()
        {
            byId = new Dictionary<string, Definition>(definitions.Count, StringComparer.Ordinal);
            byType.Clear();
            foreach (var d in definitions)
            {
                if (d == null) continue;
                if (string.IsNullOrEmpty(d.Id)) { Debug.LogError("[Database] definition without id: " + d.name, d); continue; }
                Definition other;
                if (byId.TryGetValue(d.Id, out other)) { Debug.LogError("[Database] duplicate id '" + d.Id + "': " + other.name + " / " + d.name, d); continue; }
                byId.Add(d.Id, d);
            }
        }

        public T Get<T>(string id) where T : Definition
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (byId == null) Build();
            Definition d;
            if (!byId.TryGetValue(id, out d)) { Debug.LogWarning("[Database] unknown id '" + id + "' (" + typeof(T).Name + ")"); return null; }
            var t = d as T;
            if (t == null) Debug.LogWarning("[Database] id '" + id + "' is " + d.GetType().Name + ", not " + typeof(T).Name);
            return t;
        }

        public bool TryGet<T>(string id, out T def) where T : Definition
        {
            def = null;
            if (string.IsNullOrEmpty(id)) return false;
            if (byId == null) Build();
            Definition d;
            if (!byId.TryGetValue(id, out d)) return false;
            def = d as T;
            return def != null;
        }

        public T[] All<T>() where T : Definition
        {
            if (byId == null) Build();
            Array cached;
            if (byType.TryGetValue(typeof(T), out cached)) return (T[])cached;
            var list = new List<T>();
            foreach (var d in byId.Values) { var t = d as T; if (t != null) list.Add(t); }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            var arr = list.ToArray();
            byType[typeof(T)] = arr;
            return arr;
        }

#if UNITY_EDITOR
        public void EditorSet(List<Definition> defs)
        {
            definitions = defs;
            byId = null;
            byType.Clear();
        }
        public IReadOnlyList<Definition> EditorAll { get { return definitions; } }
#endif
    }
}
