using System.Text.RegularExpressions;
using FluentAssertions;

namespace CalisBakalimEnik.ArchitectureTests;

/// <summary>
/// Rules for the SECURITY DEFINER functions the migrations install.
/// </summary>
/// <remarks>
/// These functions run as the role that owns the schema, so the application —
/// which deliberately cannot issue DDL — can still create and drop partitions.
/// That is a privilege boundary with a door in it, and the door has two locks:
/// <c>SET search_path</c>, without which a caller can put their own schema in
/// front and have the function call their code; and an allowlist, without which
/// the function is a general-purpose DDL service.
///
/// A future migration that adds one and forgets either is a privilege
/// escalation that nothing else in this suite would notice. It reads the
/// migration SOURCE because the SQL only exists as strings inside a method —
/// there is nothing in the compiled assembly to inspect.
/// </remarks>
public class DefinerFunctionTests
{
    private static readonly Regex Definer =
        new(@"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+(?<name>\w+)\s*\((?<args>[^)]*)\)"
            + @"(?<body>.*?)\$fn\$;",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static string MigrationsSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root has to be findable from the test binary");

        var migrations = Path.Combine(
            directory!.FullName,
            "src", "CalisBakalimEnik.Infrastructure", "Persistence", "Migrations");

        Directory.Exists(migrations).Should().BeTrue($"{migrations} should exist");

        // Concatenated rather than checked per file: the rule is about every
        // definer function in the repository, not about any one migration.
        return string.Concat(
            Directory.EnumerateFiles(migrations, "*.cs")
                .Select(File.ReadAllText));
    }

    [Fact]
    public void Every_definer_function_pins_its_search_path()
    {
        var source = MigrationsSource();

        foreach (Match match in Definer.Matches(source))
        {
            var body = match.Groups["body"].Value;
            var name = match.Groups["name"].Value;

            if (!body.Contains("SECURITY DEFINER", StringComparison.OrdinalIgnoreCase))
                continue;

            body.Should().Contain("SET search_path",
                $"'{name}' runs as the schema owner, so an unpinned search_path lets " +
                "a caller decide which code it runs");

            // public MUST NOT be in the path. It is writable, and an
            // unqualified CREATE resolves to the first schema in the path —
            // which is how the first version of this ended up trying to create
            // tables in pg_catalog.
            var path = Regex.Match(body, @"SET\s+search_path\s*=\s*(?<value>[^\r\n;]+)")
                .Groups["value"].Value;

            path.Should().NotContain("public",
                $"'{name}' must qualify what it touches rather than rely on the path");
        }
    }

    [Fact]
    public void Every_definer_function_refuses_a_table_it_does_not_manage()
    {
        var source = MigrationsSource();
        var checkedAny = false;

        foreach (Match match in Definer.Matches(source))
        {
            var body = match.Groups["body"].Value;
            var name = match.Groups["name"].Value;

            if (!body.Contains("SECURITY DEFINER", StringComparison.OrdinalIgnoreCase))
                continue;

            checkedAny = true;

            // Without this the function is a general-purpose DDL service for
            // anyone who can call it.
            body.Should().Contain("RAISE EXCEPTION",
                $"'{name}' has to reject a table outside its allowlist");
        }

        checkedAny.Should().BeTrue(
            "the migrations should contain definer functions — if this fails, the " +
            "regex has drifted from the SQL and these rules are checking nothing");
    }
}
