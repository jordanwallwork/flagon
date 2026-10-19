namespace Jorrk.Flagon.Tests;

/// <summary>Reasons and error codes as OpenFeature, OFREP and telemetry spell them.</summary>
public class WireNameTests
{
    [Theory]
    [InlineData(EvaluationReason.Static, "STATIC")]
    [InlineData(EvaluationReason.Default, "DEFAULT")]
    [InlineData(EvaluationReason.TargetingMatch, "TARGETING_MATCH")]
    [InlineData(EvaluationReason.Split, "SPLIT")]
    [InlineData(EvaluationReason.Cached, "CACHED")]
    [InlineData(EvaluationReason.Disabled, "DISABLED")]
    [InlineData(EvaluationReason.Unknown, "UNKNOWN")]
    [InlineData(EvaluationReason.Stale, "STALE")]
    [InlineData(EvaluationReason.Error, "ERROR")]
    [InlineData(EvaluationReason.Archived, "ARCHIVED")]
    public void Every_reason_has_its_OpenFeature_name(EvaluationReason reason, string expected) =>
        Assert.Equal(expected, reason.ToWireName());

    [Theory]
    [InlineData(ErrorCode.None, "NONE")]
    [InlineData(ErrorCode.ProviderNotReady, "PROVIDER_NOT_READY")]
    [InlineData(ErrorCode.FlagNotFound, "FLAG_NOT_FOUND")]
    [InlineData(ErrorCode.ParseError, "PARSE_ERROR")]
    [InlineData(ErrorCode.TypeMismatch, "TYPE_MISMATCH")]
    [InlineData(ErrorCode.TargetingKeyMissing, "TARGETING_KEY_MISSING")]
    [InlineData(ErrorCode.InvalidContext, "INVALID_CONTEXT")]
    [InlineData(ErrorCode.ProviderFatal, "PROVIDER_FATAL")]
    [InlineData(ErrorCode.General, "GENERAL")]
    public void Every_error_code_has_its_OpenFeature_name(ErrorCode code, string expected) =>
        Assert.Equal(expected, code.ToWireName());

    [Fact]
    public void The_theories_above_cover_every_member()
    {
        Assert.Equal(10, Enum.GetValues<EvaluationReason>().Length);
        Assert.Equal(9, Enum.GetValues<ErrorCode>().Length);
    }

    [Fact]
    public void Values_outside_the_enums_fall_back_to_unknown_and_general()
    {
        Assert.Equal("UNKNOWN", ((EvaluationReason)999).ToWireName());
        Assert.Equal("GENERAL", ((ErrorCode)999).ToWireName());
    }
}
