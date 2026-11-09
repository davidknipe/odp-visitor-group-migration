# ODP visitor group migration (sample code)

One-off scheduled jobs that move Optimizely CMS 12/13 visitor groups (Audiences) from
**UNRVLD.ODP.VisitorGroups** to Optimizely's official **Optimizely.Cms.Odp** package.

The visitor groups are edited **in place**, so they keep their IDs. Personalised content areas, personalised rich
text and access rights keep working without being touched.

> Sample code, provided as-is. It has not been compiled against every CMS 12/13 minor version. Test it on a copy of
> production data first, and take a database backup before running the swap job.

## What gets migrated

| UNRVLD criterion | What happens |
|---|---|
| Real Time Segments ("Is In Segment") | Swapped automatically for **Data Platform > Audience Membership** (condition "Member of") |
| Engagement rank, Order Likelihood, Winback zone, Observation, Customer property (text/number) | **Reported only.** No equivalent exists. Recreate the rule as an ODP real-time audience and swap it by hand. |
| Any UNRVLD criterion saved by v1.x (old type names) | Reported as needing manual work |

## The jobs

| Job | Writes? | Purpose |
|---|---|---|
| ODP migration 1 - Report (read only) | Only the CSV reports | Lists affected groups and what will happen to each criterion, plus where they are used in content |
| ODP migration 2 - Swap criteria | Visitor groups + backup records | Swaps the Real Time Segment criteria in place. Safe to re-run. |
| ODP migration 3 - Roll back swap | Visitor groups | Puts the UNRVLD criteria back from the backups. Needs UNRVLD still installed. |

The reports are saved as CSV files in **For All Sites > ODP migration reports**. That folder is locked down to the
admin roles on every run. Backups go to the Dynamic Data Store (store name `OdpVisitorGroupMigrationBackup`), not the
media library.

## Setup

1. Keep **UNRVLD.ODP.VisitorGroups** installed and add **Optimizely.Cms.Odp**:
   - CMS 12: `Optimizely.Cms.Odp` 1.2.x
   - CMS 13: `Optimizely.Cms.Odp` 2.x (needs CMS 13.1+)
2. Configure `Optimizely:Odp` (see the official docs). Every UNRVLD endpoint needs a matching instance with **both**
   its Private and Public API keys.
3. Copy the `.cs` files into your site project. On **CMS 13** add the `CMS13` compilation symbol to the project:
   ```xml
   <PropertyGroup>
     <DefineConstants>$(DefineConstants);CMS13</DefineConstants>
   </PropertyGroup>
   ```
4. Register the services in `Startup.cs`:
   ```csharp
   services.AddODPVisitorGroups();                        // existing UNRVLD registration, keep for now
   services.AddOptimizelyOdp(_configuration);             // official package
   services.AddOdpVisitorGroupMigration(_configuration);  // this sample
   ```
5. Make sure one of your `MediaData` types accepts `.csv` files (`[MediaDescriptor(ExtensionString = "...,csv")]`).
6. Optional settings in `appsettings.json`:
   ```json
   "OdpMigration": {
     "InstanceMap": { "US": "My ODP Account" },
     "DefaultInstance": null,
     "IncludePreviousVersions": false,
     "ReportFolderName": "ODP migration reports",
     "ReportReadRoles": [ "CmsAdmins", "Administrators", "WebAdmins" ]
   }
   ```
   - `InstanceMap` maps an UNRVLD endpoint `Name` to an `Optimizely:Odp:Instances` key. You only need it when the
     names differ. With one UNRVLD endpoint and one official instance, they are matched automatically.
   - `DefaultInstance` is used for segment values that don't name an endpoint, when the instance can't be worked
     out automatically. Values that do name an endpoint (`[EU] - my_segment`) are never guessed: if they can't
     be mapped they are reported as needing manual work.

> The official package needs instances under `Optimizely:Odp:Instances`. If you copied a flat
> `Optimizely:Odp:PrivateApiKey` example, every criterion will show as "Unresolved" in the report.

## Coverage of the usage report

It scans everything under the root (pages, shared blocks, media, folders), apart from the wastebasket. For each item
it checks the current versions in every language, and previously published versions too if
`IncludePreviousVersions` is set. It looks in:

- personalised content area items, including inline blocks on CMS 13
- personalised rich text, including personalised inline blocks on CMS 13
- local blocks and block lists
- access rights set directly on an item

It does **not** cover Commerce catalog content, custom content providers outside the root, or visitor groups
referenced from code or custom properties. The swap doesn't change visitor group IDs, so those keep working anyway.
The report exists to tell you what to test.

The report folder is published media. If you run Search & Navigation or Optimizely Graph, check that it doesn't
index the folder, or delete the reports once you are done.

## Using this with Claude Code (or another AI assistant)

[`CLAUDE.md`](CLAUDE.md) gives an AI assistant the package details and the safety rules the code relies on, so it
doesn't "fix" them from memory. Two slash commands are included:

- `/integrate`: checks your site's prerequisites, adds the files and gets them building. It asks before changing
  anything.
- `/review-report <criteria.csv> [usage.csv]`: turns the report job's CSVs into a to-do list for the manual items
  and a test plan.

Copy the `.claude/commands` folder into your site repo if you want the commands available there. As with any AI
help, review the changes yourself and don't let it run the swap job against production for you.

## Clean up afterwards

Once the report job shows nothing left to migrate and testing is complete:

1. Delete these files and the `AddOdpVisitorGroupMigration` call.
2. Remove `AddODPVisitorGroups()`, the `EPiServer:OdpVisitorGroupOptions` config and the UNRVLD package.
3. Optionally delete the `OdpVisitorGroupMigrationBackup` store and the report folder.

## Licence

[MIT](LICENSE). Provided as-is, with no warranty. See the licence for details.
