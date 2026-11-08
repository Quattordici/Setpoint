using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Setpoint.Core.Diagnostics;
using Setpoint.Core.Planning;
using Setpoint.Core.Policy.Model;

namespace Setpoint.Core.Tests.Planning;

/// <summary>Tests for <see cref="PlanFile"/>: the JSON shape, round trips, strict reading and the no-secrets rule.</summary>
public sealed class PlanFileTests
{
    /// <summary>
    /// Given a plan with an update, a create and a delete,
    /// when it is written and read back,
    /// then it is unchanged and the JSON uses the documented names and lower-case values.
    /// </summary>
    [Fact(DisplayName = "👍 A plan round-trips through JSON")]
    public void Serialize_RoundTrip_Unchanged()
    {
        // Arrange
        PlanDocument plan = Sample();

        // Act
        string json = PlanFile.Serialize(plan);
        PlanDocument read = PlanFile.Deserialize(json);

        // Assert
        PlanFile.Serialize(read).ShouldBe(json);
        JsonNode root = JsonNode.Parse(json)!;
        root["$schema"]!.GetValue<string>().ShouldBe(PlanDocument.SchemaUri);
        root["formatVersion"]!.GetValue<int>().ShouldBe(1);
        JsonNode repo = root["repos"]![0]!;
        repo["status"]!.GetValue<string>().ShouldBe("changes");
        repo["unmanagedCount"]!.GetValue<int>().ShouldBe(1);
        repo["objects"]![0]!["operation"]!.GetValue<string>().ShouldBe("update");
        repo["objects"]![2]!.AsObject().ContainsKey("payload").ShouldBeFalse();
        repo["changes"]![0]!["action"]!.GetValue<string>().ShouldBe("update");
        repo["diagnostics"]![0]!["severity"]!.GetValue<string>().ShouldBe("warning");
        root["targets"]![0]!["platform"]!.GetValue<string>().ShouldBe("azuredevops");
        root["targets"]![0]!["auth"]!.GetValue<string>().ShouldBe("accessToken");
    }

    /// <summary>
    /// Given a plan file with a key the format doesn't define,
    /// when it is read,
    /// then it is refused.
    /// </summary>
    [Fact(DisplayName = "👎 A plan file with an unknown key is refused")]
    public void Deserialize_UnknownKey_Throws()
    {
        // Arrange
        JsonObject root = JsonNode.Parse(PlanFile.Serialize(Sample()))!.AsObject();
        root["token"] = "ghp_should_never_be_here";

        // Act
        Action read = () => PlanFile.Deserialize(root.ToJsonString());

        // Assert
        read.ShouldThrow<JsonException>();
    }

    /// <summary>
    /// Given every type in the plan file contract,
    /// when their property names are checked,
    /// then none is named like a secret, so no field can carry one.
    /// </summary>
    [Fact(DisplayName = "🏛️ The plan file has no field that could hold a secret")]
    public void PlanContract_PropertyNames_NoSecrets()
    {
        // Arrange
        string[] forbidden = ["token", "secret", "password", "key", "credential", "pat"];
        Type[] types = [.. typeof(PlanDocument).Assembly.GetTypes().Where(t => t.Namespace == typeof(PlanDocument).Namespace && t.Name.StartsWith("Plan", StringComparison.Ordinal))];

        // Act
        string[] suspicious =
        [
            .. types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(p => p.Name is not ("Key" or "MatchKey") && Words(p.Name).Any(w => forbidden.Contains(w)))
                .Select(p => $"{p.DeclaringType!.Name}.{p.Name}"),
        ];

        // Assert
        types.ShouldNotBeEmpty();
        suspicious.ShouldBeEmpty();
    }

    private static IEnumerable<string> Words(string name) =>
        System.Text.RegularExpressions.Regex.Matches(name, "[A-Z][a-z0-9]*").Select(m => m.Value.ToLowerInvariant());

    private static PlanDocument Sample() => new()
    {
        FormatVersion = 1,
        SetpointVersion = "0.1.0-alpha",
        CreatedAt = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
        Policy = new PlanPolicy("sha256:abc", 5, "3f9c2a1"),
        Options = new PlanOptionsRecord(false, false, false, 25),
        Targets = [new PlanTarget("ado-cloud", Platform.AzureDevOps, "Azure DevOps Services", "7.0", AuthMethod.AccessToken, "build-service", 3)],
        Repos =
        [
            new PlanRepo
            {
                Target = "ado-cloud",
                Path = "Payments/payments-api",
                Status = PlanRepoStatus.Changes,
                Layers = ["baseline", "payments"],
                Objects =
                [
                    new PlanObject("ado:policy/17", ObjectOperation.Update, "sha256:1", "ado:minimumReviewers", JsonNode.Parse("""{ "isEnabled": true }""")),
                    new PlanObject("ado:new/build/payments-ci", ObjectOperation.Create, "sha256:2", "ado:build/payments-ci", JsonNode.Parse("""{ "isBlocking": true }""")),
                    new PlanObject("ado:policy/23", ObjectOperation.Delete, "sha256:3", "ado:commentRequirements", null),
                ],
                Changes = [new PlanChange("minimumReviewers.count", ChangeAction.Update, "1", "2", "baseline", true, "ado:policy/17")],
                Unmanaged = [new PlanUnmanaged("requiredChecks", "ado:policy/40", "nightly")],
                Diagnostics = [new PlanDiagnostic(DiagnosticCodes.AmbiguousMatch, DiagnosticSeverity.Warning, "two match")],
            },
        ],
        Diagnostics = [],
        Summary = new PlanSummary(1, 1, 1, 1, 0, 0, 0, 1, 1),
    };
}
