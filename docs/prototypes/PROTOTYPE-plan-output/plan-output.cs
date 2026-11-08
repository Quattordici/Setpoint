// PROTOTYPE - throwaway mock for ticket #22. Not product code: hard-coded sample data, no tests.
// Run:  dotnet run docs/prototypes/PROTOTYPE-plan-output/plan-output.cs -- [a|b|c] [--verbose]
//   a = tree (default), b = table per repo, c = plain one line per change (CI logs)
// It also writes plan.json next to the current directory to show the plan file shape.
#:package Spectre.Console@0.50.0
#:property PublishAot=false

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

var variant = args.FirstOrDefault(a => a is "a" or "b" or "c") ?? "a";
var verbose = args.Contains("--verbose");
var console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi = variant == "c" ? AnsiSupport.No : AnsiSupport.Yes,
    ColorSystem = variant == "c" ? ColorSystemSupport.NoColors : ColorSystemSupport.TrueColor,
    Out = new AnsiConsoleOutput(Console.Out),
});
console.Profile.Width = variant == "c" ? 400 : 120;
var record = args.Contains("--html") ? new Recorder(console) : null;
IAnsiConsole o = (IAnsiConsole?)record ?? console;

// ---------- sample estate (from PROTOTYPE-policy-schema) ----------
var targets = new[]
{
    new Target("ado-onprem", "azuredevops", "Azure DevOps Server 2022.1", "7.0", "integrated", 3),
    new Target("ado-cloud", "azuredevops", "Azure DevOps Services", "7.0", "pat", 1),
    new Target("github", "github", "github.com (Free)", "2022-11-28", "pat", 2),
};

var repos = new List<Repo>
{
    new("ado-onprem", "Payments/payments-api", ["baseline", "payments"], Status.Changes,
        Changes:
        [
            new("~", "minimumReviewers.count", "1", "2", "baseline", Locked: true,
                Native: new("ado:policy/17", "update", "Minimum number of reviewers #17", "minimumApproverCount 1 → 2")),
            new("~", "minimumReviewers.resetOnPush", "false", "true", "baseline", Locked: true,
                Native: new("ado:policy/17", "update", "Minimum number of reviewers #17", "resetOnSourcePush false → true")),
            new("+", "requiredChecks", "(none)", "payments-ci", "payments",
                Native: new("ado:new/build/payments-ci", "create", "Build validation (new)", "buildDefinitionId 42, validDuration 720")),
            new("~", "allowedMergeTypes", "merge, squash, rebase", "squash", "payments",
                Native: new("ado:policy/19", "update", "Require a merge strategy #19", "allowNoFastForward, allowRebase → false")),
        ],
        Unmanaged: 2),
    new("ado-onprem", "Payments/legacy-ledger", ["baseline", "payments", "repo"], Status.Changes,
        Changes:
        [
            new("-", "requireCommentResolution", "true", "(removed)", "repo",
                Native: new("ado:policy/23", "delete", "Comment requirements #23", "delete configuration")),
            new("~", "allowedMergeTypes", "merge, squash, rebase", "merge, squash", "repo",
                Native: new("ado:policy/24", "update", "Require a merge strategy #24", "allowRebase → false")),
        ],
        Unmanaged: 3),
    new("ado-onprem", "Platform/build-tools", ["baseline"], Status.InSync, [], Unmanaged: 1,
        Warnings: [new("SP2201", "minimumReviewers: 2 live policies match type + branch main (#31, #32); left alone")]),
    new("ado-cloud", "Web/storefront", ["baseline"], Status.InSync, [], Unmanaged: 0),
    new("github", "quattordici/docs-site", ["baseline"], Status.Changes,
        Changes:
        [
            new("+", "minimumReviewers", "(none)", "count 2, resetOnPush", "baseline", Locked: true,
                Native: new("gh:ruleset/setpoint", "create", "ruleset setpoint (new)", "pull_request.required_approving_review_count 2, dismiss_stale_reviews_on_push")),
            new("+", "requireCommentResolution", "(none)", "true", "baseline",
                Native: new("gh:ruleset/setpoint", "create", "ruleset setpoint (new)", "pull_request.required_review_thread_resolution")),
            new("+", "allowedMergeTypes", "(none)", "squash, rebase", "baseline",
                Native: new("gh:ruleset/setpoint", "create", "ruleset setpoint (new)", "pull_request.allowed_merge_methods [squash, rebase]")),
            new("+", "protectFromForcePushAndDelete", "(none)", "true", "baseline", Locked: true,
                Native: new("gh:ruleset/setpoint", "create", "ruleset setpoint (new)", "non_fast_forward, deletion")),
            new("+", "github.pullRequest.requireCodeOwnerReview", "(none)", "false", "baseline",
                Native: new("gh:ruleset/setpoint", "create", "ruleset setpoint (new)", "pull_request.require_code_owner_review false")),
        ],
        Unmanaged: 1,
        Warnings: [new("SP2101", "minimumReviewers: unmanaged ruleset 'org-protect' requires 3 approvals; effective value is 3, not 2")]),
    new("github", "quattordici/payments-gateway", ["baseline", "payments"], Status.Skipped, [], Unmanaged: 0,
        Warnings: [new("SP3001", "private repo on GitHub Free: rulesets unavailable, skipped (--strict fails)")]),
};

var changed = repos.Count(r => r.Status == Status.Changes);
var all = repos.SelectMany(r => r.Changes).ToList();
var warnings = repos.SelectMany(r => r.Warnings ?? []).ToList();

// ---------- header ----------
if (variant != "c")
{
    o.MarkupLine("[bold]Setpoint 0.1.0[/] [grey]· plan · policy[/] 3f9c2a1 [grey](5 files) · 6 repos in scope[/]");
    o.WriteLine();
    var t = new Table().Border(TableBorder.Simple).BorderColor(Spectre.Console.Color.Grey)
        .AddColumn("Target").AddColumn("Detected").AddColumn("API").AddColumn("Auth").AddColumn(new TableColumn("Repos").RightAligned());
    foreach (var x in targets) t.AddRow($"[bold]{x.Name}[/]", x.Detected, x.Api, x.Auth, x.Repos.ToString());
    o.Write(t);
}
else
{
    o.WriteLine("setpoint 0.1.0 plan policy=3f9c2a1 repos=6");
    foreach (var x in targets) o.WriteLine($"target {x.Name} detected=\"{x.Detected}\" api={x.Api} auth={x.Auth} repos={x.Repos}");
}

// ---------- body ----------
static string Hue(string sym) => sym switch { "+" => "green", "-" => "red", _ => "yellow" };

foreach (var r in repos)
{
    if (r.Status == Status.InSync && !verbose && (r.Warnings is null)) continue;
    var key = $"{r.Target}/{r.Path}";
    var layers = string.Join(" → ", r.Layers);
    var tag = r.Status switch
    {
        Status.Changes => $"[yellow]{r.Changes.Count} change(s)[/]",
        Status.InSync => "[green]in sync[/]",
        _ => "[grey]skipped[/]",
    };

    switch (variant)
    {
        case "a":
            var tree = new Tree($"[bold]{key}[/]  [grey]{layers}[/]  {tag}  [grey]{r.Unmanaged} unmanaged[/]").Guide(TreeGuide.Line);
            foreach (var c in r.Changes)
            {
                var lock_ = c.Locked ? " [grey]🔒[/]" : "";
                var node = tree.AddNode(
                    $"[{Hue(c.Sym)}]{c.Sym} {c.Rule}[/]  [grey]{Markup.Escape(c.From)}[/] → [bold]{Markup.Escape(c.To)}[/]  [deepskyblue1][[{c.Layer}]][/]{lock_}");
                node.AddNode($"[grey]{c.Native.Op} {Markup.Escape(c.Native.Object)}: {Markup.Escape(c.Native.Detail)}[/]");
            }
            foreach (var w in r.Warnings ?? []) tree.AddNode($"[darkorange]⚠ {w.Code}[/] {Markup.Escape(w.Message)}");
            o.Write(tree);
            o.WriteLine();
            break;

        case "b":
            o.Write(new Rule($"[bold]{key}[/] [grey]({layers})[/] {tag} [grey]· {r.Unmanaged} unmanaged[/]").LeftJustified().RuleStyle("grey"));
            if (r.Changes.Count > 0)
            {
                var t = new Table().Border(TableBorder.Rounded).BorderColor(Spectre.Console.Color.Grey)
                    .AddColumn("").AddColumn("Rule").AddColumn("Live").AddColumn("Policy").AddColumn("Layer").AddColumn("Native change");
                foreach (var c in r.Changes)
                    t.AddRow($"[{Hue(c.Sym)}]{c.Sym}[/]", c.Rule + (c.Locked ? " 🔒" : ""), $"[grey]{Markup.Escape(c.From)}[/]",
                        $"[bold]{Markup.Escape(c.To)}[/]", $"[deepskyblue1]{c.Layer}[/]", $"[grey]{c.Native.Op} {Markup.Escape(c.Native.Object)}[/]");
                o.Write(t);
            }
            foreach (var w in r.Warnings ?? []) o.MarkupLine($"  [darkorange]⚠ {w.Code}[/] {Markup.Escape(w.Message)}");
            o.WriteLine();
            break;

        default:
            foreach (var c in r.Changes)
                o.WriteLine($"{c.Sym} {key} {c.Rule}: {c.From} -> {c.To} [{c.Layer}] ({c.Native.Op} {c.Native.Object})");
            foreach (var w in r.Warnings ?? []) o.WriteLine($"warning {w.Code} {key}: {w.Message}");
            if (r.Status == Status.Skipped) o.WriteLine($"skip {key}");
            break;
    }
}

// ---------- summary ----------
var creates = all.Count(c => c.Sym == "+"); var updates = all.Count(c => c.Sym == "~"); var removes = all.Count(c => c.Sym == "-");
var inSync = repos.Count(r => r.Status == Status.InSync); var skipped = repos.Count(r => r.Status == Status.Skipped);
var unmanaged = repos.Sum(r => r.Unmanaged);
if (variant == "c")
{
    o.WriteLine($"summary repos_changed={changed} create={creates} update={updates} remove={removes} in_sync={inSync} skipped={skipped} unmanaged={unmanaged} warnings={warnings.Count} max_changes={changed}/25");
    o.WriteLine("plan saved to plan.json");
}
else
{
    var grid = new Grid().AddColumn().AddColumn();
    grid.AddRow("[bold]Repos[/]", $"[yellow]{changed} to change[/], [green]{inSync} in sync[/], [grey]{skipped} skipped[/]  [grey](max-changes {changed}/25)[/]");
    grid.AddRow("[bold]Changes[/]", $"[green]{creates} create[/], [yellow]{updates} update[/], [red]{removes} remove[/]  [grey]→ {all.Select(c => c.Native.Key).Distinct().Count()} native writes[/]");
    grid.AddRow("[bold]Unmanaged[/]", $"{unmanaged} settings left alone [grey](--verbose lists them)[/]");
    grid.AddRow("[bold]Warnings[/]", $"[darkorange]{warnings.Count}[/] [grey]({string.Join(", ", warnings.Select(w => w.Code))}; --strict would fail)[/]");
    grid.AddRow("[bold]Saved[/]", "plan.json  [grey]→ setpoint apply plan.json[/]");
    o.Write(new Panel(grid).Header("Plan").BorderColor(Spectre.Console.Color.Grey).Expand());
}

if (record is not null)
    File.WriteAllText($"plan-{variant}.html", record.ExportHtml());

// ---------- plan file ----------
static string Fp(string s) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

var plan = new
{
    schema = "https://setpoint.dev/schema/v1/plan.json",
    formatVersion = 1,
    setpointVersion = "0.1.0",
    createdAt = "2026-11-08T12:00:00Z",
    policy = new { hash = Fp("policy-tree"), files = 5, commit = "3f9c2a1" },
    options = new { strict = false, skipUnreachable = false, maxChanges = 25 },
    targets = targets.Select(t => new { name = t.Name, platform = t.Platform, detected = t.Detected, apiVersion = t.Api }),
    repos = repos.Select(r => new
    {
        target = r.Target,
        path = r.Path,
        status = r.Status.ToString().ToLowerInvariant(),
        layers = r.Layers,
        // Fingerprints are per native object: what apply re-reads and compares before its first write.
        // A create records the fingerprint of "no matching object" so a hand-made one appearing later makes the plan stale.
        objects = r.Changes.GroupBy(c => c.Native.Key).Select(g => new
        {
            key = g.Key,
            operation = g.First().Native.Op,
            fingerprint = g.First().Native.Op == "create" ? Fp("absent:" + g.Key) : Fp(g.Key + ":live"),
            payload = g.First().Native.Op == "delete" ? null : g.Key.StartsWith("gh:") ? (object)new { name = "setpoint", target = "branch", enforcement = "active" } : new { isEnabled = true, isBlocking = true },
        }),
        changes = r.Changes.Select(c => new
        {
            rule = c.Rule,
            action = c.Sym switch { "+" => "create", "-" => "remove", _ => "update" },
            from = c.From,
            to = c.To,
            layer = c.Layer,
            locked = c.Locked,
            @object = c.Native.Key,
        }),
        unmanagedCount = r.Unmanaged,
        warnings = r.Warnings?.Select(w => new { code = w.Code, message = w.Message }) ?? [],
    }),
    summary = new { reposChanged = changed, create = creates, update = updates, remove = removes, inSync, skipped, unmanaged, warnings = warnings.Count },
};
File.WriteAllText("plan.json", JsonSerializer.Serialize(plan, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
}).Replace("\"schema\"", "\"$schema\""));

record Target(string Name, string Platform, string Detected, string Api, string Auth, int Repos);
record Native(string Key, string Op, string Object, string Detail);
record Change(string Sym, string Rule, string From, string To, string Layer, Native Native, bool Locked = false);
record Warn(string Code, string Message);
record Repo(string Target, string Path, string[] Layers, Status Status, List<Change> Changes, int Unmanaged, List<Warn>? Warnings = null);
enum Status { Changes, InSync, Skipped }
