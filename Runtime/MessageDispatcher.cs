using System;
using System.Collections.Generic;

namespace Heartwood
{
    public static class MessageDispatcher
    {
        private interface ISubscribers
        {
            void Clear();
        }

        private static readonly List<ISubscribers> _all = new List<ISubscribers>();

        private class Subscribers<T> : ISubscribers where T : struct
        {
            public static readonly Subscribers<T> Instance;

            static Subscribers()
            {
                Instance = new Subscribers<T>();
                _all.Add(Instance);
            }

            public Action<T> Handlers;

            public void Clear() => Handlers = null;
        }

        public static void Subscribe<T>(Action<T> handler) where T : struct
            => Subscribers<T>.Instance.Handlers += handler;

        public static void Unsubscribe<T>(Action<T> handler) where T : struct
            => Subscribers<T>.Instance.Handlers -= handler;

        public static void Post<T>(T message) where T : struct
            => Subscribers<T>.Instance.Handlers?.Invoke(message);

        public static void Clear()
        {
            foreach (var s in _all)
                s.Clear();
        }
    }
}
