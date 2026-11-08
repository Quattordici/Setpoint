# Name collisions: is "Setpoint" free?

Research for [issue #5](https://github.com/Quattordici/Setpoint/issues/5). Checked on 2026-11-08.

**Question.** Is "Setpoint" free as a NuGet package id, as a `dotnet tool` command name, and as a GitHub project name, with no confusing collision in the DevOps, governance or infrastructure-as-code space? Is there a trademark risk? If Setpoint is not clear, run the same quick check on Plumbline, Holdfast, Stanchion, Repolith and Levelset.

**Short answer.** Setpoint is free on NuGet, and no tool uses it. GitHub has no well-known Setpoint project. There are a few small, recent "Setpoint" repos that describe themselves as "declarative control planes" or "desired-state reconciliation" (0 to 3 stars each). The trademark picture is a discontinued Logitech utility, and USPTO filings in unrelated software niches. Recommendation: **keep Setpoint**, with the precautions listed at the end.

## Method

- **NuGet ids.** `GET https://api.nuget.org/v3-flatcontainer/<id>/index.json`. A 404 means no package with that id has ever been pushed, and that includes unlisted packages ([flat container API docs](https://learn.microsoft.com/en-us/nuget/api/package-base-address-resource)).
- **NuGet search.** `https://www.nuget.org/packages?q=<name>&prerel=true` searches all packages, and `&packagetype=dotnettool` restricts the search to tools. The search matches id, title, description and tags. In this sandbox the proxy blocks the JSON search endpoint (`azuresearch-usnc.nuget.org/query`), so I scraped the HTML results page instead. The page uses the same search service.
- **Tool command name.** nuget.org does not index a tool's command name (`<ToolCommandName>` in `DotnetToolSettings.xml`) as its own search field. A package with a different id could in principle ship a command called `setpoint`. The proxy for this is: no DotnetTool package mentions the word at all. As a second check, GitHub code search for `"<ToolCommandName>setpoint</ToolCommandName>"` returned 0 hits.
- **GitHub.** `gh api -X GET search/repositories -f q='<name>'`, run plain, with `in:name`, and with domain keywords (`drift`, `policy`, `governance`, `repository`, `dotnet`, `language:C#`).
- **Products and trademarks.** Web search, plus USPTO data from trademark aggregators. The sandbox could not reach `tsdr.uspto.gov`, `support.logi.com` or `gleanmark.com`, so the trademark statuses below come from search-result snippets and are **unverified**.

## Setpoint

| Check | Result | Source |
|---|---|---|
| NuGet id `Setpoint` | **Free** (404) | https://api.nuget.org/v3-flatcontainer/setpoint/index.json |
| NuGet ids `Setpoint.Cli`, `Setpoint.Tool`, `Setpoint.Core`, `dotnet-setpoint` | **All free** (404) | `https://api.nuget.org/v3-flatcontainer/<id>/index.json` |
| nuget.org search "setpoint" (all types, prerelease included) | **0 packages** | https://www.nuget.org/packages?q=setpoint&prerel=true |
| nuget.org search "setpoint", DotnetTool type | **0 packages** | https://www.nuget.org/packages?q=setpoint&packagetype=dotnettool&prerel=true |
| `dotnet tool` command `setpoint` | **No known user.** No tool package mentions it, and GitHub code search finds no `<ToolCommandName>setpoint</ToolCommandName>` | as above |
| GitHub, all repos | 800 matches for "setpoint", 266 with it in the name. Nearly all use the word in its control-engineering sense (PID, HVAC, PX4 drones, Home Assistant). The most-starred has 209 stars and is a ROS2/PX4 example | `gh api search/repositories q=setpoint` |
| GitHub, same-space repos | [AngelP17/Setpoint](https://github.com/AngelP17/Setpoint) (1 star, created 2026-02): "GitOps control plane for industrial PLC drift detection, desired-state reconciliation". [cahaseler/setpoint](https://github.com/cahaseler/setpoint) (3 stars, created 2026-06): "Declarative control plane for your SpaceMolt fleet" (a game API). [raeinsoltani/setpoint](https://github.com/raeinsoltani/setpoint) (2 stars, Go): a Prometheus-driven Kubernetes autoscaler. [GodParticles1/Setpoint-CI-Mirror](https://github.com/GodParticles1/Setpoint-CI-Mirror) (0 stars, Go): "Public CI mirror of Setpoint", where the product itself is private and undescribed | `gh api search/repositories q='setpoint in:name' / 'setpoint drift'` |
| GitHub, .NET | 8 C# repos match. None is a tool or library named Setpoint (the top hit is a Logitech keyboard utility). The query "setpoint dotnet" returns 0 | `gh api search/repositories q='setpoint language:C#'` |
| Notable products | **Logitech SetPoint**: a Windows mouse and keyboard utility, now listed as no longer supported, with Logi Options+ as its replacement. **Setpoint** (Israel, setpoint.ai): building climate control. **Setpoint Technologies** (Austin): capital-markets software. **Setpoint Systems**: lean-manufacturing automation. **Setpoint Software**: field-work software | [Logitech support](https://support.logi.com/hc/en-gb/articles/360025128474-SetPoint), [Softpedia](https://www.softpedia.com/get/System/System-Miscellaneous/SetPoint.shtml), [CB Insights setpoint-2](https://www.cbinsights.com/company/setpoint-2), [CB Insights setpoint-3](https://www.cbinsights.com/investor/setpoint-3), [Manufacturing Technology Insights](https://www.manufacturingtechnologyinsights.com/setpoint), [setpointsoftware.pages.dev](https://setpointsoftware.pages.dev/) |
| Trademarks (USPTO, unverified) | SETPOINT, serial 99529143, Setpoint Technologies Inc., pending, class 42: non-downloadable software for capital markets and lending. SETPOINT, serial 99648663, SetPointRacquets Inc., pending, class 9: racquet-sports mobile app. SETPOINT INTEGRATED SOLUTIONS, serial 97416739, classes 9 and 42, pending. 75333099 (1997, health records) is abandoned. No mark found for DevOps or source-control tooling | [markinton 99529143](https://markinton.com/trademark/setpoint-99529143), [markinton 99648663](https://markinton.com/trademark/setpoint-99648663), [markinton 75333099](https://markinton.com/trademark/setpoint-75333099), [gleanmark 99529143](https://gleanmark.com/trademark/uspto-99529143) |

**Assessment.**

- **Package registries:** completely clear.
- **GitHub:** no established project. The word is common in control engineering, so searches will be noisy but unconfusing. The "desired state" metaphor is evidently attractive: at least three small 2026 repos use Setpoint for control-plane or reconciler tools. None of them is in repo governance, on .NET, or on NuGet.
- **Trademark:** low risk, but not zero. Logitech's SetPoint is a different product category (device drivers) and is discontinued. The live filings cover capital markets, racquet sports and building automation. A class 9/42 filing does exist, so a proper USPTO TSDR / EUIPO search is worth doing before any commercial branding. This note is not legal advice.

## Alternatives (quick check)

Setpoint came out clear, so I only did a light pass on these. Each NuGet row covers `<name>`, `<name>.Cli`, `<name>.Tool` and `dotnet-<name>` in the flat container, plus a nuget.org search over all package types and over DotnetTool only.

| Name | NuGet ids | `dotnet tool` command | GitHub (repos matching / name matches) | Notable products and collisions |
|---|---|---|---|---|
| **Plumbline** | All free; search 0 | Free (0 tools) | 226 / 224. [sroberts/plumbline](https://github.com/sroberts/plumbline) (Go): "repo-level AI coding readiness assessment", which is **adjacent to our space**. [ActaClad/plumbline](https://github.com/ActaClad/plumbline) (31 stars): LLM reliability analyzer. [nickyfactz/plumbline](https://github.com/nickyfactz/plumbline) (29 stars): Codex/Claude Code workflow plugin | Plumbline Consulting: Microsoft Dynamics ISV services ([Crunchbase](https://www.crunchbase.com/organization/plumbline-consulting)) |
| **Holdfast** | **`Holdfast` taken** (v0.1.0 and v0.2.0, 2026, an arena allocator by Diego Larrosa). The other ids are free | Free (0 tools) | 252 / 238. [rohanprasadofficial/holdfast](https://github.com/rohanprasadofficial/holdfast): "Self-hosted GitHub backups", which is **adjacent**. [shivsarthak/holdfast](https://github.com/shivsarthak/holdfast): file storage | *Holdfast: Nations At War*, a well-known Steam/Xbox/PlayStation game ([Steam](https://store.steampowered.com/app/589290/Holdfast_Nations_At_War)). NuGet: https://www.nuget.org/packages/Holdfast |
| **Stanchion** | All free; search 0 | Free (0 tools) | 48 / 45. [dgllghr/stanchion](https://github.com/dgllghr/stanchion) (682 stars): SQLite columnar extension. [basho/stanchion](https://github.com/basho/stanchion): Riak CS component | Established open-source infrastructure uses, though none in governance |
| **Repolith** | All free; search 0 | Free (0 tools) | 6 / 5, but **most are in the repo/GitHub space**. [centrys-labs/repolith](https://github.com/centrys-labs/repolith): "AI-native GitHub experience". [ethanrickyjrjr-wq/repolith](https://github.com/ethanrickyjrjr-wq/repolith): coordination layer for AI coding agents. [Repolith/repolith-releases](https://github.com/Repolith/repolith-releases): a closed-source "Repolith desktop app". [anatta-rs/repolith](https://github.com/anatta-rs/repolith): multi-repo orchestrator | Repolith desktop app (commercial, private source) |
| **Levelset** | All free; search 0 | Free (0 tools) | 285 / 232, mostly the mathematical level-set method | **Levelset**: a construction lien and payment SaaS that Procore bought for about $500M in 2021, with more than 250k customers. This is a strong brand and the **worst trademark risk of the six** ([Business Wire](https://www.businesswire.com/news/home/20210922005931/en/Procore-to-Acquire-Levelset-to-Simplify-Lien-Management-Workflows-for-Construction), [levelset.com](https://www.levelset.com/news/procore-to-acquire-levelset-to-simplify-lien-management-workflows-for-construction/)) |

## Recommendation

**Keep Setpoint.** It is the only candidate that is fully clear on NuGet and has no established brand in software tooling. Its meaning (the desired value that a control loop drives toward) fits plan/apply/drift exactly.

To keep it clean:

1. **Reserve the NuGet ids early.** Push a placeholder or the first preview under `Setpoint` (the tool package, command `setpoint`). Consider also reserving `Setpoint.Core` and `Setpoint.Cli`. After that, apply for an [ID prefix reservation](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation) for `Setpoint.*`.
2. **Disambiguate in prose.** Use a descriptor such as "Setpoint, repository governance as code for GitHub and Azure DevOps" in the README, the NuGet description and the tags (`governance`, `policy-as-code`, `github`, `azure-devops`). This sets it apart from the PLC, Kubernetes and SpaceMolt "setpoint" repos and from Logitech.
3. **Run a real trademark search** (USPTO TSDR / EUIPO, classes 9 and 42) before any commercial use. The aggregator data used here could not be verified from the sandbox.

Fallback ranking if Setpoint is ever dropped:

1. **Stanchion**: clear on NuGet, and its GitHub collisions are in unrelated domains.
2. **Plumbline**: clear on NuGet, but one adjacent "repo readiness" tool exists.
3. Avoid the other three:
   - **Repolith** is crowded in exactly the repo space.
   - **Holdfast** is taken on NuGet and is the name of a popular game.
   - **Levelset** is a Procore-owned brand.
