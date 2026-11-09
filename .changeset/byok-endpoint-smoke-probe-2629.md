---
---

The install smoke check now sends one HTTPS request with no key to each configured BYOK model endpoint host. It fails the deployment and names the host when DNS, egress, or TLS does not work.
