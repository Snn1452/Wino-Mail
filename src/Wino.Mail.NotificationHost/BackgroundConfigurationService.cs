using System;
using System.Collections.Concurrent;
using System.Globalization;
using Windows.Foundation.Collections;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.NotificationHost;

internal sealed class BackgroundConfigurationService : IConfigurationService
{
    private static readonly ConcurrentDictionary<CacheKey, object?> Cache = new();
    private static readonly object MissingValue = new();

    public bool Contains(string key)
        => ApplicationData.Current.LocalSettings.Values.ContainsKey(key);

    public bool Remove(string key)
    {
        Invalidate(key);
        return ApplicationData.Current.LocalSettings.Values.Remove(key);
    }

    public T Get<T>(string key, T defaultValue = default!)
        => GetCached(key, false, defaultValue);

    public T GetRoaming<T>(string key, T defaultValue = default!)
        => GetCached(key, true, defaultValue);

    public void Set(string key, object value)
    {
        Invalidate(key);
        SetInternal(key, value, ApplicationData.Current.LocalSettings.Values);
    }

    public void SetRoaming(string key, object value)
    {
        Invalidate(key);
        SetInternal(key, value, ApplicationData.Current.RoamingSettings.Values);
    }

    private static T GetCached<T>(string key, bool roaming, T defaultValue)
    {
        var cacheKey = new CacheKey(key, typeof(T), roaming);
        if (Cache.TryGetValue(cacheKey, out var cached))
            return ReferenceEquals(cached, MissingValue) ? defaultValue : (T)cached!;

        var values = roaming
            ? ApplicationData.Current.RoamingSettings.Values
            : ApplicationData.Current.LocalSettings.Values;

        if (!TryGetStored(values, key, out T value))
        {
            Cache[cacheKey] = MissingValue;
            return defaultValue;
        }

        Cache[cacheKey] = value;
        return value;
    }

    private static bool TryGetStored<T>(IPropertySet values, string key, out T value)
    {
        value = default!;

        if (!values.TryGetValue(key, out var stored) || stored == null)
            return false;

        if (typeof(T).IsEnum)
        {
            if (stored is T typedEnum)
            {
                value = typedEnum;
                return true;
            }

            if (Enum.TryParse(typeof(T), stored.ToString(), true, out var parsed))
            {
                value = (T)parsed!;
                return true;
            }

            return false;
        }

        if (typeof(T) == typeof(Guid?) || typeof(T) == typeof(Guid))
        {
            if (Guid.TryParse(stored.ToString(), out var guid))
            {
                value = (T)(object)guid;
                return true;
            }

            return false;
        }

        if (typeof(T) == typeof(TimeSpan))
        {
            if (TimeSpan.TryParse(stored.ToString(), CultureInfo.InvariantCulture, out var timeSpan))
            {
                value = (T)(object)timeSpan;
                return true;
            }

            return false;
        }

        try
        {
            value = stored is T typed ? typed : (T)Convert.ChangeType(stored.ToString(), typeof(T), CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetInternal(string key, object value, IPropertySet values)
        => values[key] = value?.ToString();

    private static void Invalidate(string key)
    {
        foreach (var cacheKey in Cache.Keys)
        {
            if (string.Equals(cacheKey.Key, key, StringComparison.Ordinal))
                Cache.TryRemove(cacheKey, out _);
        }
    }

    private readonly record struct CacheKey(string Key, Type Type, bool Roaming);
}
