# Team ownership is snapshotted

Panko snapshots the selected Recipe's canonical team onto each Case and Pattern, and authorizes historical access against that stored owner rather than the Recipe's current owner. Treating Recipe configuration as live ownership would make a routine YAML edit silently transfer Case Files, Crumbs, Pattern history, and queued work between teams; the trade-off is that work fails closed when a Recipe is reassigned.

Security audit events are append-only and independent of Case retention so access and rebuild decisions remain reviewable after responder-facing Crumbs have expired.
