using System.Globalization;

namespace RQ.UI;

internal static class ListingPlanInput
{
    public const int MaxUnitPrice = 999_999_999;

    public static int Commit(string text, int max)
    {
        if (max < 1) return 0;
        long value = 0;
        var any = false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9') continue;
            any = true;
            value = value > (long.MaxValue - (c - '0')) / 10 ? long.MaxValue : value * 10 + c - '0';
        }
        return any ? (int)Math.Clamp(value, 1, max) : 0;
    }

    public static string Display(int value, bool grouped) => value == 0 ? string.Empty :
        value.ToString(grouped ? "N0" : "0", CultureInfo.InvariantCulture);
}
