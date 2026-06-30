# Elena Park — Learning Designer

## Identity & background

- Designs technical enablement, onboarding courses, workshops, and assessments.
- Works with subject-matter experts but needs learning objectives, practice, and evaluation to be coherent.
- Uses Agentweaver as a curriculum studio with instructional, technical, and learner-perspective agents.

## Domain

Education, instructional design, enablement, assessment design, learner experience.

## Goals & motivations

- Convert expert knowledge into teachable sequences and measurable outcomes.
- Identify where learners will get confused before content ships.
- Produce instructor guides, exercises, and assessment rubrics efficiently.

## What Elena wants from a multi-agent system

- Agents for instructional design, novice learner critique, SME review, accessibility, and assessment quality.
- Clear mapping between objectives, content, practice, and evaluation.
- Feedback that explains why a learner may struggle.

## Behavioral profile & decision patterns

- Starts with audience level, timebox, prerequisites, and learning objectives.
- Reviews structure before wording; checks that every activity maps to an objective.
- High tolerance for iterative brainstorming; low tolerance for inaccessible or jargon-heavy material.
- Reacts to errors by narrowing the lesson scope or asking for a learner walkthrough.
- Expects outputs to be reusable in LMS or workshop materials.

## Agentweaver scenarios

### Course design studio

- **Trigger/goal:** Build a 90-minute workshop introducing multi-agent orchestration.
- **Team/agents:** Instructional designer, SME, novice learner, exercise designer, accessibility reviewer.
- **UI steps attempted:** Create learning project; enter audience and objectives; launch course-design team; inspect outline; request activities and assessment.
- **Success looks like:** Workshop outline, objectives, agenda, exercises, facilitator notes, and accessibility considerations.

### Learner feedback synthesis

- **Trigger/goal:** Improve a course using post-training feedback comments.
- **Team/agents:** Feedback theme analyst, learner advocate, curriculum editor, prioritization reviewer.
- **UI steps attempted:** Paste feedback; run synthesis; review pain points; create improvement tasks.
- **Success looks like:** Ranked issues, quote-backed evidence, suggested curriculum changes, and quick wins versus larger redesigns.

### Assessment quality review

- **Trigger/goal:** Check whether quiz and lab questions measure the intended skills.
- **Team/agents:** Assessment designer, bias reviewer, novice tester, rubric writer.
- **UI steps attempted:** Provide objectives and questions; request quality review; inspect flagged items and improved versions.
- **Success looks like:** Alignment matrix, revised questions, rubric notes, and warnings about ambiguity or bias.

## Failure signals to watch for

- Agentweaver cannot preserve objective-to-activity mappings across outputs.
- Generated material assumes unexplained prior knowledge for novice audiences.
- Elena cannot request perspective-specific review, such as accessibility or novice confusion.
- Outputs are hard to export into slides, LMS pages, or facilitator notes.
- The UI treats learning design as generic content writing and misses assessment criteria.
