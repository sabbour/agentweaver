using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CanvasContractValidatorTests
{
    private static CanvasJsonSchema Schema(string json = """{"type":"object"}""")
    {
        using var document = JsonDocument.Parse(json);
        return new CanvasJsonSchema(
            CanvasSchemaDialects.Draft202012SubsetV1,
            document.RootElement.Clone());
    }

    private static CanvasProviderProfile Profile() =>
        new("a2ui", new Version(1, 0), "a2ui.default", "profile-v1", "format-v1");

    private static CanvasTypeDescriptor Descriptor(params CanvasActionDescriptor[] actions) =>
        new("form", "descriptor-v1", Profile(), Schema(), [.. actions], []);

    private static CanvasRequestScope Scope() => new("project-1", "session-1");

    private static CanvasContentReference Content() =>
        new("content-v1", new string('a', 64));

    private static CanvasInstanceBinding Binding(CanvasRequestScope? scope = null) =>
        new(scope ?? Scope(), "instance-1", Profile(), "form", "descriptor-v1",
            Content(), "configuration-v1", 4);

    private static CanvasActionDescriptor Action(
        string id,
        CanvasJsonSchema? input = null,
        CanvasJsonSchema? output = null) =>
        new(id, input ?? Schema(), output ?? Schema());

    [Fact]
    public void ValidatesDescriptorStructureAndOwnsSchemaDocuments()
    {
        using var sourceDocument = JsonDocument.Parse("""{"type":"object"}""");
        var descriptor = Descriptor(Action("submit",
            new CanvasJsonSchema(
                CanvasSchemaDialects.Draft202012SubsetV1,
                sourceDocument.RootElement)));

        var result = CanvasContractValidator.ValidateDescriptorStructure(descriptor);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        sourceDocument.Dispose();
        Assert.Equal("""{"type":"object"}""", result.Value!.Actions[0].InputSchema.Definition.GetRawText());
    }

    [Fact]
    public void RejectsReservedDuplicateActionsAndInvalidSchemaDocuments()
    {
        var arraySchema = JsonDocument.Parse("[]");
        try
        {
            var descriptor = Descriptor(
                Action("canvas.open"),
                Action("Submit"),
                Action("submit", output: new CanvasJsonSchema(
                    CanvasSchemaDialects.Draft202012SubsetV1,
                    arraySchema.RootElement.Clone())));

            var result = CanvasContractValidator.ValidateDescriptorStructure(descriptor);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue =>
                issue.Code == CanvasContractValidationCode.ReservedActionId);
            Assert.Contains(result.Issues, issue =>
                issue.Code == CanvasContractValidationCode.DuplicateActionId);
            Assert.Contains(result.Issues, issue =>
                issue.Code == CanvasContractValidationCode.InvalidSchemaDocument);
        }
        finally
        {
            arraySchema.Dispose();
        }
    }

    [Fact]
    public void ValidatesOpenRequestAgainstExactDescriptorAndRejectsMismatchedProfile()
    {
        var descriptor = Descriptor(Action("submit"));
        using var input = JsonDocument.Parse("""{"answer":"yes"}""");
        var request = new CanvasOpenRequest(
            Scope(), "instance-1", Profile(), "form", "descriptor-v1",
            Content(), "configuration-v1", "open-1", input.RootElement);

        var valid = CanvasContractValidator.ValidateOpenRequestStructure(request, descriptor);
        var mismatched = CanvasContractValidator.ValidateOpenRequestStructure(
            request with { ProviderProfile = Profile() with { ProfileRevision = "profile-v2" } },
            descriptor);

        Assert.True(valid.IsValid);
        Assert.Equal(input.RootElement.GetRawText(), valid.Value!.Input!.Value.GetRawText());
        Assert.Contains(mismatched.Issues, issue =>
            issue.Code == CanvasContractValidationCode.DescriptorMismatch);
    }

    [Fact]
    public void RejectsActionRequestsWithMismatchedScopeOrStaleBinding()
    {
        var descriptor = Descriptor(Action("submit"));
        var binding = Binding();
        using var input = JsonDocument.Parse("""{"answer":"yes"}""");
        var request = new CanvasActionRequest(binding, "submit", "action-1", input.RootElement);

        var valid = CanvasContractValidator.ValidateActionRequestStructure(request, binding, descriptor);
        var wrongScope = CanvasContractValidator.ValidateActionRequestStructure(
            request with { ExpectedBinding = Binding(new CanvasRequestScope("project-2", "session-1")) },
            binding,
            descriptor);
        var stale = CanvasContractValidator.ValidateActionRequestStructure(
            request with { ExpectedBinding = binding with { StateRevision = 3 } },
            binding,
            descriptor);
        var undeclared = CanvasContractValidator.ValidateActionRequestStructure(
            request with { ActionId = "delete" }, binding, descriptor);

        Assert.True(valid.IsValid);
        Assert.Contains(wrongScope.Issues, issue =>
            issue.Code == CanvasContractValidationCode.ScopeMismatch);
        Assert.Contains(stale.Issues, issue =>
            issue.Code == CanvasContractValidationCode.StaleBinding);
        Assert.Contains(undeclared.Issues, issue =>
            issue.Code == CanvasContractValidationCode.UndeclaredAction);

        var missingBinding = CanvasContractValidator.ValidateActionRequestStructure(
            request with { ExpectedBinding = null! }, binding, descriptor);
        Assert.Contains(missingBinding.Issues, issue =>
            issue.Code == CanvasContractValidationCode.StaleBinding);
        var missingInput = CanvasContractValidator.ValidateActionRequestStructure(
            request with { Input = default }, binding, descriptor);
        Assert.Contains(missingInput.Issues, issue =>
            issue.Code == CanvasContractValidationCode.InvalidActionInput);
    }

    [Fact]
    public void ReadAndCloseRequestsRequireTheCurrentScopeAndBinding()
    {
        var binding = Binding();
        var read = CanvasContractValidator.ValidateReadRequestStructure(
            new CanvasReadRequest(Scope(), "instance-1"), binding);
        var wrongScope = CanvasContractValidator.ValidateReadRequestStructure(
            new CanvasReadRequest(new CanvasRequestScope("project-2", "session-1"), "instance-1"),
            binding);
        var close = CanvasContractValidator.ValidateCloseRequestStructure(
            new CanvasCloseRequest(binding, "close-1"), binding);
        var staleClose = CanvasContractValidator.ValidateCloseRequestStructure(
            new CanvasCloseRequest(binding with { ConfigurationRevision = "configuration-v2" }, "close-2"),
            binding);

        Assert.True(read.IsValid);
        Assert.Contains(wrongScope.Issues, issue =>
            issue.Code == CanvasContractValidationCode.ScopeMismatch);
        Assert.True(close.IsValid);
        Assert.Contains(staleClose.Issues, issue =>
            issue.Code == CanvasContractValidationCode.StaleBinding);
        var missingBinding = CanvasContractValidator.ValidateCloseRequestStructure(
            new CanvasCloseRequest(null!, "close-3"), binding);
        Assert.Contains(missingBinding.Issues, issue =>
            issue.Code == CanvasContractValidationCode.StaleBinding);
    }

    [Fact]
    public void RejectsInvalidDigestAndUninitializedDescriptorCollections()
    {
        var descriptor = Descriptor() with { Actions = default };
        var invalid = CanvasContractValidator.ValidateDescriptorStructure(descriptor);
        var request = new CanvasOpenRequest(
            Scope(), "instance-1", Profile(), "form", "descriptor-v1",
            new CanvasContentReference("content-v1", "not-a-digest"),
            "configuration-v1", "open-1", Schema().Definition);
        var badContent = CanvasContractValidator.ValidateOpenRequestStructure(
            request, Descriptor());

        Assert.Contains(invalid.Issues, issue =>
            issue.Code == CanvasContractValidationCode.InvalidDescriptor);
        Assert.Contains(badContent.Issues, issue =>
            issue.Code == CanvasContractValidationCode.InvalidContentReference);
    }

    [Fact]
    public void AppliesSupportedSchemaKeywordsToOpenAndActionInputs()
    {
        var inputSchema = Schema("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "minLength": 2, "maxLength": 4 },
                "count": { "type": "integer", "minimum": 1, "maximum": 3 },
                "tags": {
                  "type": "array",
                  "items": { "type": "string" },
                  "minItems": 1,
                  "maxItems": 2,
                  "uniqueItems": true
                }
              },
              "required": ["name", "count", "tags"],
              "additionalProperties": false,
              "minProperties": 3,
              "maxProperties": 3
            }
            """);
        var descriptor = Descriptor(Action("submit", inputSchema)) with
        {
            OpenInputSchema = inputSchema
        };
        using var validInput = JsonDocument.Parse(
            """{"tags":["a","b"],"count":2.0,"name":"Ada"}""");
        using var validBinding = JsonDocument.Parse("""{"name":"Ada","count":2,"tags":["a"]}""");
        var binding = Binding();
        var open = new CanvasOpenRequest(
            Scope(), "instance-1", Profile(), "form", "descriptor-v1",
            Content(), "configuration-v1", "open-1", validInput.RootElement);
        var action = new CanvasActionRequest(
            binding, "submit", "action-1", validBinding.RootElement);

        Assert.True(CanvasContractValidator.ValidateOpenRequestStructure(open, descriptor).IsValid);
        Assert.True(CanvasContractValidator.ValidateActionRequestStructure(
            action, binding, descriptor).IsValid);

        foreach (var invalidJson in new[]
        {
            """{"name":"A","count":2,"tags":["a"]}""",
            """{"name":"Ada","count":2.5,"tags":["a"]}""",
            """{"name":"Ada","count":2,"tags":["a","a"]}""",
            """{"name":"Ada","count":2,"tags":["a"],"extra":true}""",
            """{"name":"Ada","count":4,"tags":["a"]}"""
        })
        {
            using var invalidInput = JsonDocument.Parse(invalidJson);
            var invalidRequest = CanvasOpenRequestWithInput(invalidInput.RootElement);
            var result = CanvasContractValidator.ValidateOpenRequestStructure(
                invalidRequest, descriptor);
            Assert.Contains(result.Issues, issue =>
                issue.Code == CanvasContractValidationCode.SchemaMismatch);
        }
    }

    [Fact]
    public void RejectsUnsupportedOrMalformedSchemaProfile()
    {
        var schemas = new[]
        {
            (Schema("""{"pattern":"^x"}"""), CanvasContractValidationCode.PatternUnsupported),
            (Schema("""{"$ref":"#/definitions/value"}"""), CanvasContractValidationCode.UnsupportedSchemaKeyword),
            (Schema("""{"type":["string","string"]}"""), CanvasContractValidationCode.InvalidSchemaDocument),
            (Schema("""{"required":["name","name"]}"""), CanvasContractValidationCode.InvalidSchemaDocument),
            (Schema("""{"enum":[1,1.0]}"""), CanvasContractValidationCode.InvalidSchemaDocument),
            (Schema("""{"minItems":-1}"""), CanvasContractValidationCode.InvalidSchemaDocument),
            (Schema("""{"uniqueItems":"yes"}"""), CanvasContractValidationCode.InvalidSchemaDocument),
            (Schema("""{"minimum":"1"}"""), CanvasContractValidationCode.UnsupportedJsonNumber),
            (Schema("""{"$schema":"https://json-schema.org/draft/2020-12/schema"}"""),
                CanvasContractValidationCode.SchemaDialectMismatch),
            (new CanvasJsonSchema("unknown", Schema().Definition),
                CanvasContractValidationCode.SchemaDialectMismatch),
            (Schema("1"), CanvasContractValidationCode.InvalidSchemaDocument)
        };

        foreach (var (schema, expectedCode) in schemas)
        {
            var result = CanvasContractValidator.ValidateDescriptorStructure(
                Descriptor(Action("submit", schema)));
            Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
        }
    }

    [Fact]
    public void DistinguishesOmittedOpenInputFromJsonNull()
    {
        var request = new CanvasOpenRequest(
            Scope(), "instance-1", Profile(), "form", "descriptor-v1",
            Content(), "configuration-v1", "open-1", null);
        var noInputDescriptor = Descriptor() with { OpenInputSchema = null };
        using var nullValue = JsonDocument.Parse("null");
        var nullInputDescriptor = noInputDescriptor with
        {
            OpenInputSchema = Schema("""{"type":"null"}""")
        };

        Assert.True(CanvasContractValidator.ValidateOpenRequestStructure(
            request, noInputDescriptor).IsValid);
        Assert.Contains(
            CanvasContractValidator.ValidateOpenRequestStructure(
                request, nullInputDescriptor).Issues,
            issue => issue.Code == CanvasContractValidationCode.InvalidActionInput);
        Assert.True(CanvasContractValidator.ValidateOpenRequestStructure(
            request with { Input = nullValue.RootElement }, nullInputDescriptor).IsValid);
    }

    [Fact]
    public void EnforcesExactNumericRangePrecisionAndUnderflow()
    {
        var descriptor = Descriptor() with
        {
            OpenInputSchema = Schema(
                """{"type":"number","minimum":0,"maximum":1000000000000}""")
        };
        foreach (var validJson in new[]
        {
            "1000000000000",
            "999999999999.9999999999999999"
        })
        {
            using var validInput = JsonDocument.Parse(validJson);
            Assert.True(CanvasContractValidator.ValidateOpenRequestStructure(
                CanvasOpenRequestWithInput(validInput.RootElement), descriptor).IsValid);
        }

        foreach (var invalidJson in new[]
        {
            "1000000000001",
            "0.12345678901234567890123456789",
            "1e-29",
            new string('9', 60 * 1024)
        })
        {
            using var invalidInput = JsonDocument.Parse(invalidJson);
            var result = CanvasContractValidator.ValidateOpenRequestStructure(
                CanvasOpenRequestWithInput(invalidInput.RootElement), descriptor);
            Assert.Contains(result.Issues, issue =>
                issue.Code == CanvasContractValidationCode.UnsupportedJsonNumber);
        }

        foreach (var invalidSchema in new[]
        {
            Schema("""{"minimum":1000000000001}"""),
            Schema("""{"maximum":0.12345678901234567890123456789}"""),
            Schema("""{"minimum":1e-29}""")
        })
        {
            var result = CanvasContractValidator.ValidateDescriptorStructure(
                Descriptor(Action("submit", invalidSchema)));
            Assert.Contains(result.Issues, issue =>
                issue.Code == CanvasContractValidationCode.UnsupportedJsonNumber);
        }
    }

    [Fact]
    public void CountsNonBmpCharactersAsOneStringCharacter()
    {
        var descriptor = Descriptor() with
        {
            OpenInputSchema = Schema("""{"type":"string","minLength":2,"maxLength":2}""")
        };
        using var twoScalars = JsonDocument.Parse("\"😀a\"");
        using var oneScalar = JsonDocument.Parse("\"😀\"");

        Assert.True(CanvasContractValidator.ValidateOpenRequestStructure(
            CanvasOpenRequestWithInput(twoScalars.RootElement), descriptor).IsValid);
        Assert.Contains(
            CanvasContractValidator.ValidateOpenRequestStructure(
                CanvasOpenRequestWithInput(oneScalar.RootElement), descriptor).Issues,
            issue => issue.Code == CanvasContractValidationCode.SchemaMismatch);
    }

    [Fact]
    public void RejectsLargeInputAgainstBoundedManyValueEnum()
    {
        var values = string.Join(",", Enumerable.Range(0, 1024)
            .Select(index => JsonSerializer.Serialize($"choice-{index:D4}")));
        var descriptor = Descriptor() with
        {
            OpenInputSchema = Schema($"{{\"enum\":[{values}]}}")
        };
        using var largeInput = JsonDocument.Parse(
            JsonSerializer.Serialize(new string('x', 60 * 1024)));

        var result = CanvasContractValidator.ValidateOpenRequestStructure(
            CanvasOpenRequestWithInput(largeInput.RootElement), descriptor);

        Assert.Contains(result.Issues, issue =>
            issue.Code == CanvasContractValidationCode.SchemaMismatch);
    }

    [Fact]
    public void EnforcesSchemaAndInputSizeDepthAndDuplicateNameBounds()
    {
        var oversizedSchema = Schema($$"""{"title":"{{new string('x', 33 * 1024)}}"}""");
        var largeDescriptorResult = CanvasContractValidator.ValidateDescriptorStructure(
            Descriptor(Action("submit", oversizedSchema)));
        Assert.Contains(largeDescriptorResult.Issues, issue =>
            issue.Code == CanvasContractValidationCode.SchemaTooLarge);

        var descriptor = Descriptor() with { OpenInputSchema = Schema("true") };
        var largeValue = JsonSerializer.Serialize(new string('x', 65 * 1024));
        using var largeInput = JsonDocument.Parse(largeValue);
        var largeRequestResult = CanvasContractValidator.ValidateOpenRequestStructure(
            CanvasOpenRequestWithInput(largeInput.RootElement), descriptor);
        Assert.Contains(largeRequestResult.Issues, issue =>
            issue.Code == CanvasContractValidationCode.InputTooLarge);

        var nestedInput = $"{new string('[', 33)}0{new string(']', 33)}";
        using var deepInput = JsonDocument.Parse(nestedInput);
        var deepRequestResult = CanvasContractValidator.ValidateOpenRequestStructure(
            CanvasOpenRequestWithInput(deepInput.RootElement), descriptor);
        Assert.Contains(deepRequestResult.Issues, issue =>
            issue.Code == CanvasContractValidationCode.InputDepthExceeded);

        using var duplicateInput = JsonDocument.Parse("""{"value":1,"value":2}""");
        var duplicateRequestResult = CanvasContractValidator.ValidateOpenRequestStructure(
            CanvasOpenRequestWithInput(duplicateInput.RootElement), descriptor);
        Assert.Contains(duplicateRequestResult.Issues, issue =>
            issue.Code == CanvasContractValidationCode.InvalidActionInput);

        var deepSchemaJson = string.Concat(
            Enumerable.Repeat("""{"items":""", 33)) +
            "true" +
            string.Concat(Enumerable.Repeat("}", 33));
        var deepSchemaResult = CanvasContractValidator.ValidateDescriptorStructure(
            Descriptor(Action("submit", Schema(deepSchemaJson))));
        Assert.Contains(deepSchemaResult.Issues, issue =>
            issue.Code == CanvasContractValidationCode.SchemaDepthExceeded);

        using var duplicateSchema = JsonDocument.Parse("""{"type":"object","type":"array"}""");
        var duplicateSchemaResult = CanvasContractValidator.ValidateDescriptorStructure(
            Descriptor(Action("submit", new CanvasJsonSchema(
                CanvasSchemaDialects.Draft202012SubsetV1,
                duplicateSchema.RootElement.Clone()))));
        Assert.Contains(duplicateSchemaResult.Issues, issue =>
            issue.Code == CanvasContractValidationCode.InvalidSchemaDocument);
    }

    [Fact]
    public void ComparesJsonValuesIndependentOfObjectOrderAndNumberSpelling()
    {
        var schema = Schema("""{"enum":[{"a":1,"b":2}]}""");
        var descriptor = Descriptor(Action("submit", schema));
        var binding = Binding();
        using var equivalentInput = JsonDocument.Parse("""{"b":2.0,"a":1.0}""");

        var result = CanvasContractValidator.ValidateActionRequestStructure(
            new CanvasActionRequest(binding, "submit", "action-1", equivalentInput.RootElement),
            binding,
            descriptor);

        Assert.True(result.IsValid);
    }

    private static CanvasOpenRequest CanvasOpenRequestWithInput(JsonElement input) =>
        new(Scope(), "instance-1", Profile(), "form", "descriptor-v1",
            Content(), "configuration-v1", "open-1", input);
}
