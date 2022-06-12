using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Core.Extensions
{
    public static class IDictionaryExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        {
            if (key == null || !dictionary.TryGetValue(key, out var value))
            {
                return default(TValue);
            }

            return value;
        }
    }
}