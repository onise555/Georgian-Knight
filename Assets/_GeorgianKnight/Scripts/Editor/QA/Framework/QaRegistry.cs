using System;
using System.Collections.Generic;
using UnityEditor;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Finds every <see cref="IValidator"/> with TypeCache and resolves "qa" filters: nothing/"all" = everything,
    /// "texture,material" = categories, "id:scene" = one validator.
    /// </summary>
    public static class QaRegistry
    {
        /// <summary>Run order: code first (its localization keys feed the localization validator), scenes late, visual last.</summary>
        static readonly string[] CategoryOrder =
        {
            QaCategory.Code, QaCategory.Data, QaCategory.Localization, QaCategory.Texture, QaCategory.Material, QaCategory.Model,
            QaCategory.Animation, QaCategory.Audio, QaCategory.Prefab, QaCategory.Scene, QaCategory.Visual
        };

        public static List<IValidator> All()
        {
            var list = new List<IValidator>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<IValidator>())
            {
                if (t.IsAbstract || t.IsInterface || t.GetConstructor(Type.EmptyTypes) == null) continue;
                list.Add((IValidator)Activator.CreateInstance(t));
            }
            list.Sort((a, b) =>
            {
                int c = Rank(a.Category).CompareTo(Rank(b.Category));
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
            return list;
        }

        static int Rank(string category)
        {
            int i = Array.IndexOf(CategoryOrder, category);
            return i < 0 ? CategoryOrder.Length : i;
        }

        /// <summary>Validators matching the filter; <paramref name="error"/> is set for unknown tokens.</summary>
        public static List<IValidator> Select(string filter, out string scope, out string error)
        {
            error = null;
            var all = All();
            string f = (filter ?? "").Trim();
            if (f.Length == 0 || f.Equals("all", StringComparison.OrdinalIgnoreCase)) { scope = "all"; return all; }
            var picked = new List<IValidator>();
            var tokens = f.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in tokens)
            {
                string token = raw.StartsWith("id:", StringComparison.OrdinalIgnoreCase) ? raw.Substring(3) : raw;
                bool byId = raw.StartsWith("id:", StringComparison.OrdinalIgnoreCase);
                bool found = false;
                foreach (var v in all)
                {
                    bool hit = byId ? v.Id.Equals(token, StringComparison.OrdinalIgnoreCase)
                                    : v.Category.Equals(token, StringComparison.OrdinalIgnoreCase) || v.Id.Equals(token, StringComparison.OrdinalIgnoreCase);
                    if (!hit) continue;
                    found = true;
                    if (!picked.Contains(v)) picked.Add(v);
                }
                if (!found) error = (error == null ? "" : error + "; ") + "unknown category/validator '" + raw + "'";
            }
            picked.Sort((a, b) => all.IndexOf(a).CompareTo(all.IndexOf(b)));
            scope = string.Join(",", tokens);
            return picked;
        }
    }
}
