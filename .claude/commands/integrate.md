---
description: Add the ODP visitor group migration jobs to an Optimizely CMS 12 or 13 site and get it building
---

Help me add this migration sample to my Optimizely CMS site. Follow CLAUDE.md throughout, especially "Rules that
must not be broken".

1. Ask me where my site project is, if it isn't obvious. Work out the CMS version from its package references:
   `EPiServer.CMS` 12.x or 13.x.
2. Check the prerequisites and list anything missing. Don't change anything yet:
   - UNRVLD.ODP.VisitorGroups is installed (2.x)
   - Optimizely.Cms.Odp is installed: 1.2.x on CMS 12, 2.x on CMS 13.1+
   - `Optimizely:Odp:Instances` exists, with a PublicApiKey for each instance (don't print key values)
   - every UNRVLD endpoint under `EPiServer:OdpVisitorGroupOptions:OdpEndpoints` has an obvious matching instance,
     or needs an `OdpMigration:InstanceMap` entry
   - a `MediaData` type accepts `.csv` files
   - CMS 13 only: `EPiServer.CMS.UI.VisitorGroups` with `AddVisitorGroupsMvc()` / `AddVisitorGroupsUI()`
3. Propose the changes and wait for my OK before making them:
   - copy the `.cs` files into a `Migration/OdpVisitorGroups` folder
   - call `services.AddOdpVisitorGroupMigration(_configuration)` as its own statement
   - on CMS 13, add `CMS13` to `DefineConstants`
   - add any `OdpMigration` settings needed
4. Build. Fix compile errors with the smallest change that keeps the behaviour. Check the API on
   world.optimizely.com/csclasslibraries/cms with `?version=12` or `?version=13` rather than guessing, and tell
   me about each fix.
5. Finish with the run order from README.md. Remind me to back up the database before running job 2.
