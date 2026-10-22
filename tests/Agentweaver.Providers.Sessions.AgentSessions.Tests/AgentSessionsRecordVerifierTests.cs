using Xunit;
using System.Text;
using System.Text.Json;
using Agentsessions.V1;
using Agentweaver.Providers.Sessions.AgentSessions;
using Google.Protobuf;

namespace Agentweaver.Providers.Sessions.AgentSessions.Tests;

public sealed class AgentSessionsRecordVerifierTests
{
    // Golden vectors copied from agent-primitives/sessions@465e82175da7815db39ec14852deb3949ff82670
    // hack/verify_chain.py. Each vector is the hash at prev_hash "" and seq 1.
    public static TheoryData<string, string> GoldenVectors => new()
    {
        {
            """{"execution_id":"exec-1","schema_version":1,"ts":"2023-11-14T22:13:20Z","kind":"EVENT_OUTPUT","message":{"role":"assistant","parts":[{"text":{"text":"hello"}}]},"actor":{"principal":"agent://a","issuer":"entra","subject":"sub-1"}}""",
            "551bd146050c8d630b0c3b999a4445f3792a470db9bca443d8d4a67706283fcc"
        },
        {
            """{"execution_id":"exec-config","kind":"EVENT_EXECUTION_START","execution_start":{"config":"AP8gCgk=","input_count":"2","resume_from_seq":"9007199254740993"}}""",
            "aa1f42a26b7a227836d32d31608ebb69c59f3b1c57eadd8537b6faf88a01c790"
        },
        {
            """{"execution_id":"exec-default","kind":"EVENT_EXECUTION_START","execution_start":{"input_count":"0"}}""",
            "b09cbff45d5a30990c3b72be8b06af6bc509d5a29a849aab5fd42153ef10ec65"
        },
        {
            """{"execution_id":"exec-default","kind":"EVENT_EXECUTION_START","execution_start":{"input_count":"2"}}""",
            "f06c25ee5b3673ee23b8f91883c7f1c6c5682d94986caf5bc067908a61b2ac4f"
        },
        {
            """{"execution_id":"exec-tool","schema_version":1,"ts":"2023-11-14T22:13:20Z","kind":"EVENT_OUTPUT","message":{"role":"assistant","parts":[{"text":{"text":"checking"}},{"tool_call":{"id":"call-1","tool":"get_weather","args":{"city":"Paris","days":2,"detail":true,"ratio":1.5,"units":{"temp":"C"},"tags":["a","b"]}}}]},"actor":{"principal":"agent://a","issuer":"entra","subject":"sub-1"}}""",
            "e8911826d1370b0b96f3d3a1efaf63bc20f3808cdc189d937b5155ac3f8d509c"
        },
        {
            """{"schema_version":1,"ts":"2023-11-14T22:13:20.123456789Z","kind":"EVENT_LIFECYCLE","lifecycle":{"kind":"LIFECYCLE_DELETE"}}""",
            "bfbda987672b922ac194edadeec95fad94093740ab2a6bf9786f09c4a56d3d90"
        },
        {
            """{"kind":"EVENT_LIFECYCLE","lifecycle":{"kind":"LIFECYCLE_FORK","detail":"parent@2"}}""",
            "a545aeac12036c2b871c394990207d101b93074c6b687830016925bbb3617ea4"
        },
        {
            """{"kind":"EVENT_LIFECYCLE","lifecycle":{"kind":"LIFECYCLE_FORK","detail":"parent@2","compute_instance":"uid-1"}}""",
            "a10f9c7c0bc86360e712fdd926fffa45d56168af238c451d2b494ce0feb8dc8c"
        },
    };

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void ReproducesEveryPinnedGoldenVectorFromProtobufBytes(string eventJson, string expected)
    {
        // Round-trip through protobuf bytes so the vector exercises the same decode path as an export.
        var bytes = JsonParser.Default.Parse<Event>(eventJson).ToByteArray();
        Assert.Equal(expected, AgentSessionsRecordVerifier.ComputeContentHash("", 1, Event.Parser.ParseFrom(bytes)));
    }

    [Fact]
    public void DeleteRecordMatchesTheIndependentCanonicalByteLiteral()
    {
        var ev = JsonParser.Default.Parse<Event>("""{"schema_version":1,"ts":"2023-11-14T22:13:20.123456789Z","kind":"EVENT_LIFECYCLE","lifecycle":{"kind":"LIFECYCLE_DELETE"}}""");
        Assert.Equal(
            """{"event":{"kind":"EVENT_LIFECYCLE","lifecycle":{"kind":"LIFECYCLE_DELETE"},"schema_version":1,"ts":"2023-11-14T22:13:20.123456789Z"},"prev_hash":"","seq":"1"}""",
            Encoding.UTF8.GetString(AgentSessionsRecordVerifier.CanonicalRecord("", 1, ev)));
    }

    [Fact]
    public void PresentZeroAndAbsentOptionalFieldsHashDifferently()
    {
        var present = JsonParser.Default.Parse<Event>("""{"execution_id":"exec-default","kind":"EVENT_EXECUTION_START","execution_start":{"input_count":"0"}}""");
        Assert.True(present.ExecutionStart.HasInputCount);
        var absent = present.Clone();
        absent.ExecutionStart.ClearInputCount();
        Assert.Equal("b09cbff45d5a30990c3b72be8b06af6bc509d5a29a849aab5fd42153ef10ec65", AgentSessionsRecordVerifier.ComputeContentHash("", 1, present));
        Assert.NotEqual(AgentSessionsRecordVerifier.ComputeContentHash("", 1, present), AgentSessionsRecordVerifier.ComputeContentHash("", 1, absent));

        // A record whose stored hash binds the present zero fails when the bytes drop the field.
        var document = Single(absent, "b09cbff45d5a30990c3b72be8b06af6bc509d5a29a849aab5fd42153ef10ec65");
        Assert.Equal("content_hash", AgentSessionsRecordVerifier.Verify(document).Code);
    }

    [Fact]
    public void Int64AboveTwoToThe53IsExact()
    {
        var ev = JsonParser.Default.Parse<Event>("""{"execution_id":"exec-config","kind":"EVENT_EXECUTION_START","execution_start":{"config":"AP8gCgk=","input_count":"2","resume_from_seq":"9007199254740993"}}""");
        Assert.Equal(9007199254740993L, ev.ExecutionStart.ResumeFromSeq);
        Assert.Contains("\"resume_from_seq\":\"9007199254740993\"", Encoding.UTF8.GetString(AgentSessionsRecordVerifier.CanonicalRecord("", 1, ev)));
        var next = ev.Clone();
        next.ExecutionStart.ResumeFromSeq = 9007199254740992L;
        Assert.NotEqual(AgentSessionsRecordVerifier.ComputeContentHash("", 1, ev), AgentSessionsRecordVerifier.ComputeContentHash("", 1, next));
    }

    [Fact]
    public void VerifiesTheReferenceProducedParentAndChildExports()
    {
        var parent = Load("parent.json");
        var child = Load("child.json");
        var parentResult = AgentSessionsRecordVerifier.Verify(parent);
        Assert.True(parentResult.Verified, parentResult.Message);
        Assert.Equal(parent.SourceHead.Hash, parentResult.HeadHash);
        var forkResult = AgentSessionsRecordVerifier.VerifyFork(parent, child);
        Assert.True(forkResult.Verified, forkResult.Message);
        Assert.Equal(AgentSessionsPin.SourceCommit, child.Upstream.Commit);

        // The reference export records host-owned execution starts with config, cursor, input count, and harness version.
        var start = Decode(parent.Records[0]);
        Assert.Equal(EventKind.EventExecutionStart, start.Kind);
        Assert.True(start.ExecutionStart.HasInputCount);
        Assert.Equal(1, start.ExecutionStart.InputCount);
        Assert.Equal(9007199254740993L, start.ExecutionStart.ResumeFromSeq);
        Assert.Equal("""{"profile":"agentweaver-p2-poc-fixture"}""", start.ExecutionStart.Config.ToStringUtf8());
        Assert.NotEmpty(start.ExecutionStart.HarnessVersion);
        Assert.Contains(parent.Records.Select(Decode), ev => ev.Kind == EventKind.EventEnd);

        // Producer context stays outside the hashed records.
        Assert.Equal("agentweaver", child.Producer?.System);
        Assert.Equal("fixture-child-run", child.Producer?.References?["runId"]);
    }

    [Fact]
    public void AlteredEventBytesFail()
    {
        var parent = Load("parent.json");
        var records = parent.Records.ToList();
        var ev = Decode(records[2]);
        ev.ExecutionId += "-altered";
        records[2] = records[2] with { Event = Convert.ToBase64String(ev.ToByteArray()) };
        var result = AgentSessionsRecordVerifier.Verify(parent with { Records = records });
        Assert.Equal(("content_hash", 3L), (result.Code, result.Seq));
    }

    [Fact]
    public void UnknownTopLevelFieldFailsEvenWithTheStoredHash()
    {
        var parent = Load("parent.json");
        var records = parent.Records.ToList();
        // Field 999, wire type varint, value 1. The stored content_hash stays unchanged.
        var bytes = Convert.FromBase64String(records[2].Event).Concat(new byte[] { 0xB8, 0x3E, 0x01 }).ToArray();
        Assert.Equal(Decode(records[2]).ExecutionId, Event.Parser.ParseFrom(bytes).ExecutionId);
        records[2] = records[2] with { Event = Convert.ToBase64String(bytes) };
        var result = AgentSessionsRecordVerifier.Verify(parent with { Records = records });
        Assert.Equal(("unknown_field", 3L), (result.Code, result.Seq));
    }

    [Fact]
    public void UnknownNestedFieldFailsEvenWithTheStoredHash()
    {
        var child = Load("child.json");
        var records = child.Records.ToList();
        var index = records.FindIndex(record => Decode(record).Lifecycle is not null);
        Assert.True(index >= 0);
        var ev = Decode(records[index]);
        var nested = ev.Lifecycle.ToByteArray().Concat(new byte[] { 0xB8, 0x3E, 0x01 }).ToArray();
        ev.Lifecycle = null;
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        ev.WriteTo(output);
        output.WriteTag(Event.LifecycleFieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(nested));
        output.Flush();
        records[index] = records[index] with { Event = Convert.ToBase64String(stream.ToArray()) };
        var result = AgentSessionsRecordVerifier.Verify(child with { Records = records });
        Assert.Equal(("unknown_field", index + 1L), (result.Code, result.Seq));
    }

    [Fact]
    public void UnknownMapEntryFieldFailsEvenWithTheStoredHash()
    {
        var ev = new Event
        {
            Kind = EventKind.EventModelCall,
            Model = new ModelCall { Model = "test", Params = { ["temperature"] = "0.2" } },
        };
        var entry = Encode(output =>
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteString("temperature");
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString("0.2");
            output.WriteTag(3, WireFormat.WireType.Varint);
            output.WriteUInt64(1);
        });
        var model = Encode(output =>
        {
            output.WriteTag(ModelCall.ModelFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString(ev.Model.Model);
            output.WriteTag(ModelCall.ParamsFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(entry));
        });
        var eventBytes = Encode(output =>
        {
            output.WriteTag(Event.KindFieldNumber, WireFormat.WireType.Varint);
            output.WriteEnum((int)ev.Kind);
            output.WriteTag(Event.ModelFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(model));
        });
        var document = Single(ev, AgentSessionsRecordVerifier.ComputeContentHash("", 1, ev));
        var record = document.Records[0] with { Event = Convert.ToBase64String(eventBytes) };
        var result = AgentSessionsRecordVerifier.Verify(document with { Records = [record] });

        Assert.Equal(("unknown_field", 1L), (result.Code, result.Seq));
    }

    [Fact]
    public void MapEntryFieldWithAnIncompatibleWireTypeFailsEvenWithTheStoredHash()
    {
        var ev = new Event
        {
            Kind = EventKind.EventModelCall,
            Model = new ModelCall { Model = "test", Params = { ["temperature"] = "0.2" } },
        };
        var entry = Encode(output =>
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteString("temperature");
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteUInt64(1);
        });
        var model = Encode(output =>
        {
            output.WriteTag(ModelCall.ModelFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteString(ev.Model.Model);
            output.WriteTag(ModelCall.ParamsFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(entry));
        });
        var eventBytes = Encode(output =>
        {
            output.WriteTag(Event.KindFieldNumber, WireFormat.WireType.Varint);
            output.WriteEnum((int)ev.Kind);
            output.WriteTag(Event.ModelFieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(model));
        });
        var document = Single(ev, AgentSessionsRecordVerifier.ComputeContentHash("", 1, ev));
        var record = document.Records[0] with { Event = Convert.ToBase64String(eventBytes) };

        var result = AgentSessionsRecordVerifier.Verify(document with { Records = [record] });

        Assert.Equal(("unknown_field", 1L), (result.Code, result.Seq));
    }

    [Fact]
    public void ReferenceFixturesHaveNoUnknownFields()
    {
        Assert.True(AgentSessionsRecordVerifier.VerifyFork(Load("parent.json"), Load("child.json")).Verified);
    }

    [Fact]
    public void RehashingOneRecordBreaksTheNextLink()
    {
        var parent = Load("parent.json");
        var records = parent.Records.ToList();
        var ev = Decode(records[2]);
        ev.ExecutionId += "-altered";
        var rehashed = AgentSessionsRecordVerifier.ComputeContentHash(records[2].PrevHash, 3, ev);
        records[2] = records[2] with { Event = Convert.ToBase64String(ev.ToByteArray()), ContentHash = rehashed };
        var result = AgentSessionsRecordVerifier.Verify(parent with { Records = records });
        Assert.Equal(("link", 4L), (result.Code, result.Seq));
    }

    [Fact]
    public void WrongPreviousLinkFails()
    {
        var parent = Load("parent.json");
        var records = parent.Records.ToList();
        records[0] = records[0] with { PrevHash = new string('0', 64) };
        Assert.Equal("link", AgentSessionsRecordVerifier.Verify(parent with { Records = records }).Code);
    }

    [Fact]
    public void MissingDuplicateAndOutOfOrderSequencesFail()
    {
        var parent = Load("parent.json");
        var missing = parent.Records.Where((_, index) => index != 3).ToList();
        var duplicate = parent.Records.Take(4).Append(parent.Records[3]).Concat(parent.Records.Skip(4)).ToList();
        var swapped = parent.Records.ToList();
        (swapped[3], swapped[4]) = (swapped[4], swapped[3]);
        foreach (var records in new[] { missing, duplicate, swapped })
            Assert.Equal("sequence", AgentSessionsRecordVerifier.Verify(parent with { Records = records }).Code);
        Assert.Equal("empty", AgentSessionsRecordVerifier.Verify(parent with { Records = [] }).Code);
    }

    [Theory]
    [InlineData("seq", "01")]
    [InlineData("seq", "1.0")]
    [InlineData("fence", "x")]
    [InlineData("content_hash", "ABC")]
    [InlineData("event", "not base64!")]
    [InlineData("event", "/w==")]
    public void InvalidEncodingFails(string field, string value)
    {
        var parent = Load("parent.json");
        var records = parent.Records.ToList();
        records[0] = field switch
        {
            "seq" => records[0] with { Seq = value },
            "fence" => records[0] with { Fence = value },
            "content_hash" => records[0] with { ContentHash = value },
            _ => records[0] with { Event = value },
        };
        Assert.Equal("invalid_encoding", AgentSessionsRecordVerifier.Verify(parent with { Records = records }).Code);
    }

    [Fact]
    public void DocumentShapeAndPinsAreChecked()
    {
        Assert.Equal("invalid_document", AgentSessionsRecordVerifier.Verify("""{"format":"x","format":"y"}"""u8).Code);
        var raw = File.ReadAllText(FixturePath("parent.json"));
        Assert.Equal("invalid_document", AgentSessionsRecordVerifier.Verify(Encoding.UTF8.GetBytes(raw.Replace("\"records\"", "\"extra\":1,\"records\""))).Code);
        Assert.True(AgentSessionsRecordVerifier.Verify(Encoding.UTF8.GetBytes(raw)).Verified);
        var parent = Load("parent.json");
        Assert.Equal("unsupported_source", AgentSessionsRecordVerifier.Verify(parent with { Upstream = parent.Upstream with { Commit = new string('a', 40) } }).Code);
        Assert.Equal("unsupported_format", AgentSessionsRecordVerifier.Verify(parent with { Format = "other" }).Code);
        Assert.Equal("head_mismatch", AgentSessionsRecordVerifier.Verify(parent with { SourceHead = parent.SourceHead with { Seq = "1" } }).Code);
    }

    [Fact]
    public void NullRecordInJsonDocumentReturnsInvalidDocument()
    {
        var parent = Load("parent.json");
        var json = JsonSerializer.SerializeToUtf8Bytes(parent with { Records = [null!] });

        Assert.Equal("invalid_document", AgentSessionsRecordVerifier.Verify(json).Code);
    }

    [Fact]
    public void TailTruncationVerifiesWithoutAnExternalAnchor()
    {
        // The chain is unkeyed: a consistently truncated export verifies. Only a separately held head detects it.
        var parent = Load("parent.json");
        var truncated = parent.Records.Take(5).ToList();
        var result = AgentSessionsRecordVerifier.Verify(parent with
        {
            Records = truncated,
            SourceHead = new(truncated[^1].Seq, truncated[^1].ContentHash),
        });
        Assert.True(result.Verified);
        Assert.NotEqual(parent.SourceHead.Hash, result.HeadHash);
    }

    [Fact]
    public void ForkLineageAndPrefixAreChecked()
    {
        var parent = Load("parent.json");
        var child = Load("child.json");
        Assert.Equal("fork_lineage", AgentSessionsRecordVerifier.VerifyFork(child, parent).Code);
        var otherParent = parent with { Session = parent.Session with { Uid = "someone-else" } };
        Assert.Equal("fork_lineage", AgentSessionsRecordVerifier.VerifyFork(otherParent, child).Code);
        var wrongBoundary = child with { Session = child.Session with { Parent = child.Session.Parent! with { ForkSeq = "4" } } };
        Assert.Equal("fork_marker", AgentSessionsRecordVerifier.Verify(wrongBoundary).Code);
    }

    [Fact]
    public void TransportOnlyParkedEventsAreRejected()
    {
        var parked = new Event { Kind = EventKind.EventParked, ExecutionId = "exec" };
        var document = Single(parked, AgentSessionsRecordVerifier.ComputeContentHash("", 1, parked));
        Assert.Equal("transport_event", AgentSessionsRecordVerifier.Verify(document).Code);
    }

    private static AgentSessionsExportDocument Single(Event ev, string hash) => new(
        AgentSessionsPin.ExportFormat,
        new(AgentSessionsPin.SourceRepository, AgentSessionsPin.SourceCommit, AgentSessionsPin.ContractVersion),
        new("single", null, null),
        null,
        new("1", hash),
        [new("1", "", hash, "1", Convert.ToBase64String(ev.ToByteArray()))]);

    private static byte[] Encode(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using var output = new CodedOutputStream(stream);
        write(output);
        output.Flush();
        return stream.ToArray();
    }

    private static Event Decode(AgentSessionsExportRecord record) => Event.Parser.ParseFrom(Convert.FromBase64String(record.Event));

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSessions", name);

    private static AgentSessionsExportDocument Load(string name) =>
        AgentSessionsRecordVerifier.ParseExport(File.ReadAllBytes(FixturePath(name)));
}
