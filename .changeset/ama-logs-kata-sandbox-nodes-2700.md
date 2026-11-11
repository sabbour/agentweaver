---
---

Fix deployments to clusters with a Kata sandbox node pool. On AKS Kata VM isolation nodes, the Azure Monitor add-on `addon-token-adapter` container can fail with `can't initialize iptables table 'nat'`, so `npm run deploy:kustomize` stopped with `DaemonSet/ama-logs is not ready`. The preflight now continues, with a warning that names the nodes, only when the DaemonSet is not in a rollout, each scheduled node has an `ama-logs` pod, and every pod that is not Ready runs on a node that matches the sandbox RuntimeClass node selector. This check needs `list pods` in `kube-system`. Container Insights, `ContainerNetworkLogs`, and egress audit data from those sandbox nodes are not collected. An unready `ama-logs` pod on any other node still stops the deployment.
