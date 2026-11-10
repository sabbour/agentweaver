---
---

`npm run deploy:kustomize` and `npm run azure:deploy-from-commit` now check every required Secret before they change the cluster. The check covers `agenthost-tls` and each Secret that a rendered Deployment references without `optional: true`, including `identity-broker-internal-tls`. A Secret volume that projects `tls.crt` and `tls.key` must have type `kubernetes.io/tls`. The command reads only Secret names and types, and it reports every missing Secret or wrong type in one error. Before this change, a missing `identity-broker-internal-tls` was found only after migrations and workload apply, and the Identity Broker stopped. The Azure deployment guide now has the operator procedure to issue an internal service certificate and an upgrade note for `identity-broker-internal-tls`.
