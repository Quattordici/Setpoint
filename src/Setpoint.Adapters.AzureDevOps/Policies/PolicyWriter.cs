using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Setpoint.Adapters.AzureDevOps.Api;
using Setpoint.Core.Planning;
using Setpoint.Core.Providers;

namespace Setpoint.Adapters.AzureDevOps.Policies;

/// <summary>
/// Writes one repo's planned objects: policy configurations are created, replaced by id and deleted, and the Force
/// push entry is set on the default branch's token. Payloads are sent exactly as the plan holds them, but only after
/// checking that each one is about this repo's default branch, so a plan file can't be used to write anything else.
/// </summary>
internal static class PolicyWriter
{
    private const string PolicyKeyPrefix = "ado:policy/";

    private const string PolicyPermission =
        "The token needs Code (read & write) (vso.code_write), and its account the Edit policies permission on the repo.";

    private const string EnvironmentPermission =
        "The token needs Environment (read & manage) (vso.environment_manage), and its account the Administrator role on the environment, or the Creator role on the project's environments to create one.";

    private const string AclPermission =
        "The token needs Security (manage) (vso.security_manage), and its account the Manage permissions permission on the repo.";

    /// <summary>Writes the objects in plan order. The first failure stops the repo; the rest are reported as not attempted.</summary>
    public static async Task<ApplyResult> WriteAsync(AzureDevOpsApiClient api, AdoRepository repo, RepoChangeSet changes, CancellationToken cancellationToken)
    {
        var context = new RepoContext(repo, [], null);
        var outcomes = new List<ObjectApplyOutcome>();
        string? stoppedBy = null;
        foreach (PlanObject planned in changes.Objects)
        {
            if (stoppedBy is not null)
            {
                outcomes.Add(new ObjectApplyOutcome(planned.Key, false, $"Not attempted, because writing {stoppedBy} failed."));
                continue;
            }

            string? error = await WriteAsync(api, context, planned, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new ObjectApplyOutcome(planned.Key, error is null, error));
            if (error is not null)
            {
                stoppedBy = planned.Key;
            }
        }

        return new ApplyResult(outcomes);
    }

    private static async Task<string?> WriteAsync(AzureDevOpsApiClient api, RepoContext context, PlanObject planned, CancellationToken cancellationToken)
    {
        if (EnvironmentApproval.IsMatchKey(planned.MatchKey))
        {
            return await WriteEnvironmentAsync(api, context, planned, cancellationToken).ConfigureAwait(false);
        }

        bool isAccess = RepoAccessAcl.IsMatchKey(planned.MatchKey);
        bool isAcl = isAccess || planned.MatchKey == ForcePushAcl.MatchKey(context.RefName);
        if ((isAccess ? AccessRefusal(context, planned) : Refusal(context, planned, isAcl)) is { } refusal)
        {
            return refusal;
        }

        string projectId = context.Repo.Project.Id;
        HttpResponseMessage response;
        try
        {
            response = (isAcl, planned.Operation, planned.Payload, PolicyId(planned.Key)) switch
            {
                (true, ObjectOperation.Create or ObjectOperation.Update, JsonObject payload, _) =>
                    await api.SetAccessControlEntriesAsync(ForcePushAcl.NamespaceId, payload, cancellationToken).ConfigureAwait(false),
                (false, ObjectOperation.Create, JsonObject payload, _) =>
                    await api.CreatePolicyConfigurationAsync(projectId, payload, cancellationToken).ConfigureAwait(false),
                (false, ObjectOperation.Update, JsonObject payload, int id) =>
                    await api.UpdatePolicyConfigurationAsync(projectId, id, payload, cancellationToken).ConfigureAwait(false),
                (false, ObjectOperation.Delete, _, int id) =>
                    await api.DeletePolicyConfigurationAsync(projectId, id, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"The plan's {planned.Operation.ToString().ToLowerInvariant()} of {planned.Key} has no payload or policy id to write."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A connection failure or timeout fails this repo only; the caller carries on with the others.
            return ex.Message;
        }

        using (response)
        {
            return await ErrorAsync(response, planned, isAcl, cancellationToken).ConfigureAwait(false);
        }
    }

    // An environment: created if missing, then its Approval check created, replaced or removed. The environment is
    // looked up by name at apply, because a new one has no id at plan time, and a check id from the plan must be an
    // Approval check on that environment.
    private static async Task<string?> WriteEnvironmentAsync(AzureDevOpsApiClient api, RepoContext context, PlanObject planned, CancellationToken cancellationToken)
    {
        string projectId = context.Repo.Project.Id;
        string? name = planned.Payload?["environment"] is JsonValue n && n.TryGetValue(out string? text) ? text : null;
        JsonObject? check = planned.Payload?["check"] as JsonObject;
        int? checkId = WholeNumber(planned.Payload?["checkId"]);
        if (name is null || planned.Operation == ObjectOperation.Delete || EnvironmentApproval.MatchKey(projectId, name) != planned.MatchKey
            || (check is not null && !EnvironmentApproval.IsApproval(check)))
        {
            return $"{planned.Key} isn't an environment of {context.Repo.Project.Name} with its Approval check; environments are never deleted. Nothing was written.";
        }

        try
        {
            List<JsonObject>? environments = await api.ListEnvironmentsAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (environments is null)
            {
                return $"Azure DevOps refused to list {context.Repo.Project.Name}'s environments. {EnvironmentPermission}";
            }

            JsonObject? environment = environments.FirstOrDefault(e => string.Equals(PolicyMapper.Text(e["name"]), name, StringComparison.OrdinalIgnoreCase));
            if (environment is null)
            {
                using HttpResponseMessage created = await api.CreateEnvironmentAsync(projectId, name, cancellationToken).ConfigureAwait(false);
                if (await EnvironmentErrorAsync(created, $"creating environment {name}", cancellationToken).ConfigureAwait(false) is { } failed)
                {
                    return failed;
                }

                environment = JsonNode.Parse(await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
            }

            long environmentId = environment?["id"] is JsonValue e && e.TryGetValue(out long found) ? found : 0;
            if (checkId is { } existing)
            {
                List<JsonObject>? checks = await api.ListEnvironmentChecksAsync(projectId, environmentId, cancellationToken).ConfigureAwait(false);
                if (checks?.Any(k => PolicyMapper.Number(k["id"]) == existing && EnvironmentApproval.IsApproval(k)) is not true)
                {
                    return $"Check #{existing} is no Approval check on environment {name}, or the token can't see it. Plan again.";
                }
            }

            if (check is null && checkId is null)
            {
                return null;
            }

            JsonObject? body = check?.DeepClone() as JsonObject;
            if (body?["resource"] is JsonObject resource)
            {
                resource["id"] = environmentId.ToString(CultureInfo.InvariantCulture);
            }

            using HttpResponseMessage response = (body, checkId) switch
            {
                (null, int remove) => await api.DeleteCheckAsync(projectId, remove, cancellationToken).ConfigureAwait(false),
                (JsonObject add, null) => await api.CreateCheckAsync(projectId, add, cancellationToken).ConfigureAwait(false),
                (JsonObject replace, int update) => await api.UpdateCheckAsync(projectId, update, replace, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("unreachable"),
            };
            return await EnvironmentErrorAsync(response, $"writing the Approval check on environment {name}", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    // A number read from a plan file, or built in this run, whichever type it holds.
    private static int? WholeNumber(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue(out long big) ? (int)big : value.TryGetValue(out int small) ? small : null
        : null;

    private static async Task<string?> EnvironmentErrorAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NonAuthoritativeInformation)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
        {
            return $"Azure DevOps answered {what} with its sign-in page, so it didn't accept the token. {EnvironmentPermission}";
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string message = Message(body) ?? response.ReasonPhrase ?? "no message";
        return (int)response.StatusCode switch
        {
            401 or 403 => $"Azure DevOps refused {what} ({(int)response.StatusCode}: {message}). {EnvironmentPermission}",
            404 => $"Azure DevOps couldn't find what {what} needs (404: {message}). Plan again.",
            400 or 409 => $"Azure DevOps rejected {what} ({(int)response.StatusCode}: {message}).",
            int status => $"{char.ToUpperInvariant(what[0])}{what[1..]} failed ({status}: {message}).",
        };
    }

    // An access entry must be on this repo's token, for the identity its match key names, and touch only role bits.
    private static string? AccessRefusal(RepoContext context, PlanObject planned)
    {
        bool ok = planned.Payload is JsonObject payload
            && string.Equals(PolicyMapper.Text(payload["token"]), context.RepoToken, StringComparison.OrdinalIgnoreCase)
            && ForcePushAcl.FromPayload(payload) is { } entry
            && RepoAccessAcl.MatchKey(entry.Descriptor) == planned.MatchKey;
        return ok ? null : $"{planned.Key} isn't a permission entry on {context.Repo.Name}'s repo token; nothing was written.";
    }

    // Checks that the planned object is one Setpoint owns on this repo's default branch, before anything is sent.
    private static string? Refusal(RepoContext context, PlanObject planned, bool isAcl)
    {
        if (isAcl)
        {
            return planned.Payload is JsonObject payload
                && string.Equals(PolicyMapper.Text(payload["token"]), context.BranchToken, StringComparison.OrdinalIgnoreCase)
                && ForcePushAcl.FromPayload(payload) is not null
                    ? null
                    : $"{planned.Key} isn't the Force push entry on {context.Repo.Name}'s default branch; nothing was written.";
        }

        if (!planned.MatchKey.StartsWith("ado:", StringComparison.Ordinal) || planned.MatchKey.StartsWith("ado:forcePush/", StringComparison.Ordinal))
        {
            return $"{planned.Key} isn't a policy configuration Setpoint owns; nothing was written.";
        }

        if (planned.Operation != ObjectOperation.Create && PolicyId(planned.Key) is null)
        {
            return $"{planned.Key} isn't a policy configuration key; nothing was written.";
        }

        if (planned.Payload is null)
        {
            return null;
        }

        JsonObject? settings = planned.Payload["settings"] as JsonObject;
        bool scoped = settings?["scope"] is JsonArray { Count: 1 } scope
            && scope[0] is JsonObject only
            && string.Equals(PolicyMapper.Text(only["repositoryId"]), context.Repo.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(PolicyMapper.Text(only["refName"]), context.RefName, StringComparison.Ordinal)
            && string.Equals(PolicyMapper.Text(only["matchKind"]), "Exact", StringComparison.OrdinalIgnoreCase);
        return scoped
            ? null
            : $"{planned.Key} isn't scoped to exactly {context.Repo.Name}'s default branch ({context.RefName}); nothing was written.";
    }

    private static int? PolicyId(string key) =>
        key.StartsWith(PolicyKeyPrefix, StringComparison.Ordinal)
        && int.TryParse(key.AsSpan(PolicyKeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            ? id
            : null;

    private static async Task<string?> ErrorAsync(HttpResponseMessage response, PlanObject planned, bool isAcl, CancellationToken cancellationToken)
    {
        // Azure DevOps answers a token it doesn't accept with 203 and its sign-in page, which is a success code.
        if (response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NonAuthoritativeInformation)
        {
            return null;
        }

        string what = isAcl ? (RepoAccessAcl.IsMatchKey(planned.MatchKey) ? $"the permission entry {planned.Key}" : "the Force push entry") : planned.Key;
        string permission = isAcl ? AclPermission : PolicyPermission;
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
        {
            return $"Azure DevOps answered writing {what} with its sign-in page, so it didn't accept the token. {permission}";
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string message = Message(body) ?? response.ReasonPhrase ?? "no message";
        return (int)response.StatusCode switch
        {
            401 or 403 => $"Azure DevOps refused to write {what} ({(int)response.StatusCode}: {message}). {permission}",
            404 when !isAcl && planned.Operation != ObjectOperation.Create =>
                $"The policy configuration {planned.Key} no longer exists, or the token can't see it (404: {message}). Plan again.",
            404 => $"Azure DevOps couldn't find where to write {what} (404: {message}). {permission}",
            400 or 409 => $"Azure DevOps rejected {what} ({(int)response.StatusCode}: {message}).",
            int status => $"Writing {what} failed ({status}: {message}).",
        };
    }

    private static string? Message(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["message"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
