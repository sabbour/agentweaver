using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests;

public sealed class ModelSourceTests
{
    [Fact]
    public void Byok_uses_the_new_public_api_value()
    {
        ModelSource.Byok.ToApiString().Should().Be("byok");
    }

    [Fact]
    public void New_api_value_maps_to_byok()
    {
        ModelSourceExtensions.FromApiString("byok").Should().Be(ModelSource.Byok);
    }

    [Fact]
    public void Legacy_api_value_maps_to_byok()
    {
        ModelSourceExtensions.FromApiString("microsoft-foundry").Should().Be(ModelSource.Byok);
    }

    [Fact]
    public void Legacy_enum_name_remains_an_alias_for_byok()
    {
#pragma warning disable CS0618
        ModelSource.MicrosoftFoundry.Should().Be(ModelSource.Byok);
#pragma warning restore CS0618
    }
}
