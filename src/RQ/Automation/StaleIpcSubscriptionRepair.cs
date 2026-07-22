using System.Collections;
using System.Reflection;

namespace RQ.Automation;

internal static class StaleIpcSubscriptionRepair
{
    public static int Remove<TDelegate>(
        object subscriber,
        object currentTarget,
        string methodName,
        Action<TDelegate> unsubscribe)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(currentTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(unsubscribe);

        var channel = FindChannel(subscriber);
        var subscriptions = channel?.GetType()
            .GetProperty("Subscriptions", BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(channel) as IEnumerable;
        if (subscriptions is null)
            return 0;

        var targetTypeName = currentTarget.GetType().FullName;
        var stale = subscriptions.Cast<object>()
            .OfType<TDelegate>()
            .Where(candidate =>
                !ReferenceEquals(candidate.Target, currentTarget) &&
                string.Equals(candidate.Method.Name, methodName, StringComparison.Ordinal) &&
                string.Equals(candidate.Method.DeclaringType?.FullName, targetTypeName, StringComparison.Ordinal))
            .ToArray();
        foreach (var candidate in stale)
            unsubscribe(candidate);
        return stale.Length;
    }

    private static object? FindChannel(object subscriber)
    {
        for (var type = subscriber.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty("Channel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
                return property.GetValue(subscriber);
        }
        return null;
    }
}
