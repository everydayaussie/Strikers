using System.Reflection;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    internal static class TestBuild
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

        public static bool RestoreVerb(string[] args)
        {
            var restores = 0;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--restore-reject":
                    case "--stop-draft-guard":
                    case "--freeze":
                        restores++;
                        break;

                    case "--patch-move-bounds":
                    case "--commit-ring":
                        if (!args.Contains("--clear"))
                        {
                            return false;
                        }

                        restores++;
                        break;

                    case "--force-first":
                        if (i + 1 < args.Length && args[i + 1] == "clear")
                        {
                            i++;
                        }
                        else if (!args.Contains("--clear"))
                        {
                            return false;
                        }

                        restores++;
                        break;

                    case "--parent-pid":
                        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out _))
                        {
                            return false;
                        }

                        i++;
                        break;

                    case "--clear":
                    case "--yes":
                        break;

                    default:
                        return false;
                }
            }

            return restores > 0;
        }
    }
}
