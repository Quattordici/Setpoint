using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Setpoint.ArchitectureTests;

/// <summary>
/// Security rules from the MVP spec: TLS uses the operating system's certificate store, and nothing in
/// Setpoint can replace or disable certificate validation.
/// </summary>
[Trait("Category", "Architecture.Security")]
public sealed class SecurityArchitectureTests
{
    /// <summary>
    /// Given all production code, when dependency checks run, then nothing uses a certificate validation callback.
    /// </summary>
    [Fact(DisplayName = "🏛️ No code replaces TLS certificate validation")]
    public void ProductionCodeShouldNotReplaceCertificateValidation()
    {
        // Arrange
        IArchRule rule = Types().That().Are(ArchitectureTestContext.ProductionTypes)
            .Should()
            .NotDependOnAny(ArchitectureTestContext.CertificateValidationOverrides)
            .Because("TLS uses the OS certificate store only; there is no --insecure and no custom CA bundle");

        // Act
        Exception? exception = Record.Exception(() => rule.Check(ArchitectureTestContext.Architecture));

        // Assert
        exception.ShouldBeNull();
    }

    /// <summary>
    /// Given all production code, when the members it calls are inspected, then none switches off certificate validation.
    /// </summary>
    [Fact(DisplayName = "🏛️ No code sets a certificate validation callback or accepts any certificate")]
    public void ProductionCodeShouldNotSetCertificateCallbacks()
    {
        // Arrange
        string[] forbidden =
        [
            "set_ServerCertificateCustomValidationCallback",
            "set_RemoteCertificateValidationCallback",
            "get_DangerousAcceptAnyServerCertificateValidator",
        ];
        var production = ArchitectureTestContext.ProductionTypes.GetObjects(ArchitectureTestContext.Architecture).ToHashSet();

        // Act
        string[] violations = [.. production
            .SelectMany(type => type.Dependencies.OfType<MethodCallDependency>())
            .Where(call => forbidden.Any(name => call.TargetMember.Name.StartsWith(name, StringComparison.Ordinal)))
            .Select(call => $"{call.Origin.FullName} calls {call.TargetMember.FullName}")
            .Distinct()];

        // Assert
        violations.ShouldBeEmpty("TLS uses the OS certificate store only; there is no --insecure and no custom CA bundle");
    }
}
