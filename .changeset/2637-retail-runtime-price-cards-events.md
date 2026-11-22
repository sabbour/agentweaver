---
"Agentweaver.EventsAndSessions": minor
---

Add the Azure Retail runtime price-card write endpoint (`PUT /api/platform/model-pricing/azure-retail/rate-cards/{id}/{version}`), exact meter-ID verification in `AzureRetailPriceLookup`, atomic canonical request digest replay in `RuntimeRateCardStore`, and migration `013_retail_rate_card_digest.sql`.
