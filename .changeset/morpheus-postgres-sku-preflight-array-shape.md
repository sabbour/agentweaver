---
"agentweaver": patch
---

Fixed the PostgreSQL region/SKU pre-flight check rejecting every region during `provision-infra`. `az postgres flexible-server list-skus` returns a JSON array of capability sets, but the check read the capability fields off the array itself, so it always concluded that no server editions were supported and aborted before creating the Flexible Server — even in regions where the SKU was perfectly available. The failure also reported a fabricated reason (`Azure reported no supported server editions for this subscription/region.`) that hid Azure's real explanation, turning an actionable message such as "Subscriptions are restricted from provisioning in this region ... open a support request with Issue type of 'Service and subscription limits'" into a dead end. Provisioning now succeeds in supported regions, and genuinely restricted regions surface Azure's own wording so you can pick another `--postgres-location` or request an exception.
