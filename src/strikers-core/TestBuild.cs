using System.Reflection;

namespace Strikers.Core;

public static class TestBuild
{
    public const string ForKey = "TestBuildFor";
    public const string ExpiresKey = "TestBuildExpires";

    public static (string? For, DateOnly? Expires) Read(Assembly assembly)
    {
        string? tester = null;
        DateOnly? expires = null;
        foreach (var a in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (a.Key == ForKey && !string.IsNullOrWhiteSpace(a.Value))
            {
                tester = a.Value;
            }
            else if (a.Key == ExpiresKey && DateOnly.TryParseExact(a.Value, "yyyy-MM-dd", out var d))
            {
                expires = d;
            }
        }

        return (tester, expires);
    }

    public static string? Mark(string? tester, DateOnly? expires)
    {
        if (tester is null && expires is null)
        {
            return null;
        }

        return tester is null ? "private test build" : $"private test build for {tester}";
    }

    public static bool Expired(DateOnly? expires, DateOnly today)
    {
        return expires is { } e && today > e;
    }

    public static string ExpiredMessage()
    {
        return "This test build of Strikers is no longer active. Ask the person who gave it to you for a new one.";
    }
}
