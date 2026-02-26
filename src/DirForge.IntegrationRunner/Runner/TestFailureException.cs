namespace DirForge.IntegrationRunner.Runner;

internal sealed class TestFailureException : Exception
{
    public TestFailureException(string message)
        : base(message)
    {
    }
}
