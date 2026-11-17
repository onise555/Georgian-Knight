using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight.Core
{
    /// <summary>Marker for events carried by the <see cref="EventBus"/>. Events are small immutable structs.</summary>
    public interface IGameEvent { }

    /// <summary>
    /// Typed publish/subscribe bus. Publishing does not allocate; handlers may subscribe or unsubscribe while an event
    /// is being dispatched (changes take effect for the next publish). A throwing handler is logged and does not stop
    /// the remaining handlers.
    /// </summary>
    public static class EventBus
    {
        static readonly List<Action> resetters = new List<Action>();

        static class Channel<T> where T : struct, IGameEvent
        {
            public static Action<T>[] handlers = new Action<T>[8];
            public static int count;
            public static int depth;
            public static bool dirty;

            static Channel() { lock (resetters) resetters.Add(Reset); }

            static void Reset() { handlers = new Action<T>[8]; count = 0; depth = 0; dirty = false; }

            public static void Compact()
            {
                int w = 0;
                for (int r = 0; r < count; r++)
                {
                    if (handlers[r] == null) continue;
                    handlers[w++] = handlers[r];
                }
                for (int i = w; i < count; i++) handlers[i] = null;
                count = w;
                dirty = false;
            }
        }

        sealed class Token<T> : IDisposable where T : struct, IGameEvent
        {
            Action<T> handler;
            public Token(Action<T> h) { handler = h; }
            public void Dispose()
            {
                if (handler == null) return;
                Unsubscribe(handler);
                handler = null;
            }
        }

        public static IDisposable Subscribe<T>(Action<T> handler) where T : struct, IGameEvent
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (Channel<T>.count == Channel<T>.handlers.Length)
                Array.Resize(ref Channel<T>.handlers, Channel<T>.handlers.Length * 2);
            Channel<T>.handlers[Channel<T>.count++] = handler;
            return new Token<T>(handler);
        }

        public static void Unsubscribe<T>(Action<T> handler) where T : struct, IGameEvent
        {
            var hs = Channel<T>.handlers;
            for (int i = Channel<T>.count - 1; i >= 0; i--)
            {
                if (hs[i] != handler) continue;
                if (Channel<T>.depth > 0) { hs[i] = null; Channel<T>.dirty = true; }
                else
                {
                    Array.Copy(hs, i + 1, hs, i, Channel<T>.count - i - 1);
                    hs[--Channel<T>.count] = null;
                }
                return;
            }
        }

        public static void Publish<T>(T evt) where T : struct, IGameEvent
        {
            Channel<T>.depth++;
            try
            {
                var hs = Channel<T>.handlers;
                int n = Channel<T>.count;
                for (int i = 0; i < n; i++)
                {
                    var h = hs[i];
                    if (h == null) continue;
                    try { h(evt); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
            finally
            {
                Channel<T>.depth--;
                if (Channel<T>.depth == 0 && Channel<T>.dirty) Channel<T>.Compact();
            }
        }

        public static int SubscriberCount<T>() where T : struct, IGameEvent
        {
            int n = 0;
            for (int i = 0; i < Channel<T>.count; i++) if (Channel<T>.handlers[i] != null) n++;
            return n;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetAll()
        {
            lock (resetters) foreach (var r in resetters) r();
        }
    }

    /// <summary>Collects subscriptions so a component can release them all in OnDisable/OnDestroy.</summary>
    public sealed class Subscriptions : IDisposable
    {
        readonly List<IDisposable> items = new List<IDisposable>();

        public Subscriptions On<T>(Action<T> handler) where T : struct, IGameEvent
        {
            items.Add(EventBus.Subscribe(handler));
            return this;
        }

        public void Add(IDisposable d) { if (d != null) items.Add(d); }

        public void Dispose()
        {
            for (int i = items.Count - 1; i >= 0; i--) items[i].Dispose();
            items.Clear();
        }
    }
}
