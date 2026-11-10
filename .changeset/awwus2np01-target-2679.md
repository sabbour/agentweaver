---
---

Rename the nonproduction install target `aweast2np01` to `awwus2np01` and move it from `eastus2` to `westus2`, with the Key Vault name `awwus2np01-kv1`. The subscription cannot create PostgreSQL Flexible Server in `eastus2`. Azure Monitor resources also use `westus2`. The node pool sizes stay `Standard_D2ds_v5` and `Standard_D4ds_v5`. The `agentweaver` target does not change.
