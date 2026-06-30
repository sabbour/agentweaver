# Omar Haddad — Data Analyst

## Identity & background

- Business analyst responsible for dashboards, experiment readouts, and ad hoc executive questions.
- Comfortable with CSVs, metrics definitions, notebooks, and SQL; not always a software engineer.
- Uses Agentweaver to split analysis into profiling, hypothesis generation, validation, and storytelling.

## Domain

Business intelligence, product analytics, metric investigation, survey synthesis, experiment analysis.

## Goals & motivations

- Move faster from raw data and ambiguous questions to defensible insights.
- Separate exploration from final narrative and clearly mark assumptions.
- Avoid false confidence in noisy or incomplete data.

## What Omar wants from a multi-agent system

- Agents for data profiling, statistical review, visualization suggestions, narrative synthesis, and skeptical QA.
- Transparent handling of data shape, missingness, and metric definitions.
- Reproducible outputs that show calculations or at least analysis steps.

## Behavioral profile & decision patterns

- Starts by checking whether data loaded correctly and whether fields were inferred sensibly.
- Drills into anomalies, sample rows, and assumptions before trusting summaries.
- Moderate tolerance for technical setup; low tolerance for hidden calculations.
- Reacts to errors by simplifying the dataset or asking for schema inspection first.
- Expects a final answer that separates insight, evidence, caveat, and recommendation.

## Agentweaver scenarios

### KPI anomaly investigation

- **Trigger/goal:** Explain a sudden drop in activation rate.
- **Team/agents:** Data profiler, metric-definition checker, segment analyst, hypothesis generator, executive-summary writer.
- **UI steps attempted:** Create analytics project; provide CSV/schema or metric notes; run anomaly investigation; inspect segments and caveats; request summary.
- **Success looks like:** Plausible drivers, affected segments, data-quality checks, confidence level, and recommended follow-up queries.

### Survey synthesis

- **Trigger/goal:** Summarize open-ended customer survey responses for product planning.
- **Team/agents:** Theme extractor, sentiment analyst, quote curator, bias reviewer, product recommender.
- **UI steps attempted:** Paste survey responses or attach data; configure audience; start synthesis; review themes and representative quotes.
- **Success looks like:** Ranked themes, sentiment distribution, representative anonymized quotes, caveats, and product opportunities.

### Experiment readout

- **Trigger/goal:** Prepare a decision memo for an A/B test.
- **Team/agents:** Experiment analyst, stats skeptic, segment reviewer, narrative writer.
- **UI steps attempted:** Enter experiment context and results table; ask for readout; inspect significance, guardrails, and recommendation.
- **Success looks like:** Launch/iterate/stop recommendation with evidence, guardrail metrics, limitations, and next experiment ideas.

## Failure signals to watch for

- Data import errors are silent or columns are misinterpreted without warning.
- Agent outputs cite calculations that are not visible or reproducible.
- Omar cannot distinguish sampled data from full data.
- Charts/tables are missing labels, units, or metric definitions.
- The final narrative hides caveats or overstates causality.
