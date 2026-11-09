using Xunit;

namespace OpenPayments.Console.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Console_does_not_reference_Transitions()
    {
        // D2, D40: money moves only through the payment API.
        var references = typeof(Program).Assembly.GetReferencedAssemblies().Select(name => name.Name);
        Assert.DoesNotContain("OpenPayments.Transitions", references);
        Assert.DoesNotContain("OpenPayments.Api", references);
    }
}
