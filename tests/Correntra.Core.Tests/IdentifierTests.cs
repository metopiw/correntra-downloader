namespace Correntra.Core.Tests;

public sealed class IdentifierTests
{
    [Fact]
    public void JobIdRoundTripsCanonicalValue()
    {
        JobId id = JobId.Create();

        JobId parsed = JobId.Parse(id.ToString());

        Assert.Equal(id, parsed);
        Assert.Equal(32, id.ToString().Length);
        Assert.False(id.IsEmpty);
    }

    [Fact]
    public void JobIdRejectsEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => new JobId(Guid.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("00000000000000000000000000000000")]
    public void JobIdTryParseRejectsNonCanonicalOrEmptyValues(string? value)
    {
        bool parsed = JobId.TryParse(value, out JobId result);

        Assert.False(parsed);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void AllGeneratedIdentifiersAreNonEmpty()
    {
        Assert.NotEqual(Guid.Empty, CategoryId.Create().Value);
        Assert.NotEqual(Guid.Empty, CategoryRuleId.Create().Value);
        Assert.NotEqual(Guid.Empty, QueueId.Create().Value);
        Assert.NotEqual(Guid.Empty, IpcRequestId.Create().Value);
    }
}
