---
name: sol-luna-delegation
description: "For every new manual work task, briefly assess whether a bounded Luna Max subagent would materially save work. Ask only for this task before delegating. Keep small or ambiguous work in the main agent; skip ordinary chat, status questions, and unchanged follow-ups."
---

# Task-scoped Luna execution

Keep decisions and final accountability in the main agent. This skill has one
behavior: briefly assess each new work task, and ask about this task only when
delegation has a concrete benefit. It never changes global configuration or
the main model's reasoning effort.

## Lightweight assessment

Make the assessment inside normal planning using context already available.
Do not start a separate model call, run a status helper, fetch pricing, inspect
usage history, or produce a routing report just to make this choice.

Keep the work in the main agent when it is small, tightly coupled, ambiguous,
or requires difficult diagnosis, architecture, security judgment, or a final
product decision. Also stay in the main agent when delegation is unavailable
or coordination and review would likely erase the benefit.
Skip the proposal when a question would interrupt an unattended or time-critical
flow; preserve that flow's existing execution contract.

Consider Luna Max only for a clear, execution-heavy or repetitive package with
explicit boundaries and objectively checkable acceptance. The main agent must
be able to verify it materially more cheaply than reproducing it. A bounded
read-only observation can qualify; a single wait or command usually does not.
Do not spend extra investigation trying to justify a borderline candidate.

Reassess for a new work task or a material scope change, not for each message,
tool result, status request, or unchanged follow-up.

## Ask for this task, not a global mode

Honor explicit user choices about delegation, model, effort, role, and count.
An explicit request to use Luna already supplies task-scoped consent; do not
ask again. A request to stay in the main agent or use another route wins.

Otherwise, ask at most once for the current task, only when there is a concrete
eligible package. Briefly name the package, proposed child count, expected
benefit, and what the main agent will check. Ask whether to use Luna Max for
that package this time.
Do not promise a savings percentage or ask to enable a global economy mode.

Wait for a clear answer before spawning. A refusal, silence, or an ambiguous
answer is not consent. Continue independently authorized main-agent work when
useful, without duplicating work that has already been delegated.
Consent ends with the agreed task and package; it is not permission for future
tasks, extra children, scope expansion, or external/paid/destructive actions.
After a refusal, do not ask again unless the user explicitly reconsiders.

## Bounded execution and review

Prefer one coherent child package. Use two only when the agreed work is truly
independent and the extra coordination is worthwhile. Give each mutable path
and shared resource one owner; follow the project's normal authority and gates.

For an approved Luna package, use native `explorer` for read-only work or
`worker` for bounded edits/tests with `model="gpt-5.6-luna"`,
`reasoning_effort="max"`, and `fork_turns="none"`. Supply the needed task
brief, files, constraints, stop conditions, and acceptance. Use a small amount
of recent history only when essential; never copy the full conversation by
default. Do not select `luna_executor`, whose fixed effort is XHigh, not Max.

Require a compact result: outcome, changed paths or findings, test evidence,
uncertainty, and remaining risks. Reuse the same child for a bounded correction;
do not spawn blind duplicate retries. Escalate ambiguity or cross-cutting
failures to the main agent. Do not replace an unavailable route with another
model, provider, account, or separate conversation without the user's choice.

Check actual changes and evidence in the main agent. Preserve required tests
and acceptance standards; a short summary or a child saying "done" is not proof.
Avoid repeating the child's entire investigation or implementation.

Prefer effective-model metadata returned by the native lifecycle. If a local
record is needed, use `scripts/verify_subagent_runtime.py` with the exact
`--session` path or native `--session-id`, plus `--parent-thread-id`.
Never find a child by its task name alone. Pending proof means wait for the
same child; a verified current-child mismatch means stop that child. If proof
is unavailable, distinguish requested from verified runtime and do not start
another implicit child.

Report realized savings only from actual measurements. Model choice and Max
effort alone do not prove lower total cost or unchanged quality.
