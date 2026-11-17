using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>
    /// Typed service registry. Services are registered by <see cref="GameRoot"/> (and its modules) during boot and
    /// looked up by interface. Lookups of unregistered services fail loudly so wiring mistakes surface immediately.
    /// </summary>
    public static class Services
    {
        static readonly Dictionary<Type, object> map = new Dictionary<Type, object>();

        /// <summary>Raised after a service is registered (type is the registration key).</summary>
        public static event Action<Type> Registered;

        public static void Register<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException("service", "Cannot register null for " + typeof(T).Name);
            object existing;
            if (map.TryGetValue(typeof(T), out existing) && !ReferenceEquals(existing, service))
                Debug.LogWarning("[Services] replacing " + typeof(T).Name);
            map[typeof(T)] = service;
            var h = Registered;
            if (h != null) h(typeof(T));
        }

        public static void Unregister<T>(T service) where T : class
        {
            object cur;
            if (map.TryGetValue(typeof(T), out cur) && ReferenceEquals(cur, service)) map.Remove(typeof(T));
        }

        public static T Get<T>() where T : class
        {
            object s;
            if (map.TryGetValue(typeof(T), out s)) return (T)s;
            throw new InvalidOperationException("[Services] not registered: " + typeof(T).Name +
                                                " (is GameRoot booted and the module installed in GameConfig?)");
        }

        public static bool TryGet<T>(out T service) where T : class
        {
            object s;
            if (map.TryGetValue(typeof(T), out s)) { service = (T)s; return true; }
            service = null;
            return false;
        }

        public static bool Has<T>() where T : class { return map.ContainsKey(typeof(T)); }

        internal static void Clear() { map.Clear(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { map.Clear(); Registered = null; }
    }
}
