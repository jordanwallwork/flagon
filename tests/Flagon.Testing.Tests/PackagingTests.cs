namespace Flagon.Testing.Tests;

public class PackagingTests
{
    [Fact]
    public void Flagon_Testing_references_no_test_framework()
    {
        string[] frameworks = ["xunit", "nunit", "mstest", "microsoft.visualstudio.testplatform", "microsoft.testplatform", "fluentassertions", "shouldly"];

        var references = typeof(TestFlagon).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.Contains("Flagon", references);
        Assert.DoesNotContain(references, name => frameworks.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
    }
}
