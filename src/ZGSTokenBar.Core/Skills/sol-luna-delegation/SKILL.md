---
name: sol-luna-delegation
description: "Use whenever native Codex subagents are considered to check and apply the global economy policy. `off` has no routing effect, `ask` raises one selective cost-saving question only when the likely benefit is material, and `on` routes suitable bounded work to Luna Max while keeping complex reasoning in the root."
---

# Global Subagent Economy Mode

Before applying this policy, run the bundled helper with `status`.

- For `off` or `unconfigured`, stop using this skill without influencing native
  routing.
- For `ask`, apply only the Ask Mode gate below. If it does not pass, stop using
  this skill without influencing native routing.
- For `on`, apply the cost-first delegation policy below.
- For `inconsistent`, report the configuration problem and do not infer a route.

While on, keep requirements, decisions, complex reasoning, integration, and
final accountability in the root. Honor the user's explicit route first;
otherwise use Luna Max only for packages that pass the delegation gate below.
The policy does not change the root model or effort.

## Global Switch

Use the bundled helper in each Codex profile (`CODEX_HOME`) whose configuration
should change. Conversations sharing that profile read the same switch:

```text
python[3] <skill-directory>/scripts/set_economy_mode.py on|ask|off|status
```

- `on` enables this skill and installs managed global child defaults for
  `gpt-5.6-luna` at `max` effort.
- `ask` enables this skill without installing child defaults. It may recommend
  `on` only when the Ask Mode gate passes; otherwise native routing is unchanged.
- `off` removes those managed defaults and disables this skill. Native Codex
  multi-agent behavior remains available and follows its ordinary configuration.
- `status` prints `on`, `ask`, `off`, `unconfigured`, or `inconsistent` without
  writing.

The helper writes `${CODEX_HOME}/config.toml` atomically (defaulting
`CODEX_HOME` to `~/.codex`), owns only its marked blocks, and stops on conflicting
unmanaged settings. Each invocation reads the current state, so cached skill
instructions still no-op after `off`; configuration reload controls whether the
skill remains listed. The switch never changes an already-running turn or child.
An explicit choice in the current user request still overrides the economy
route, subject to higher-priority instructions, safety, authorization, runtime
capacity, and resource ownership.

## Ask Mode

In `ask`, first decide whether native delegation would materially help the
current task. Ask about economy mode only when all of these are true:

- The current manual request contains no explicit cost, latency, quality,
  model, effort, delegation, or child-count choice.
- Native routing would otherwise use either multiple substantial independent
  children, or one long-running, high-volume, or repetitive package that is a
  plausible Luna Max fit.
- Choosing the economy route is likely to change total child cost materially,
  and the user's answer would change the route.
- Asking will not interrupt an unattended, automatic, or time-sensitive flow.

Ask at most once in a root task, before the first implicit child spawn. Keep the
question concise, explain why this task crossed the threshold, and state that
changing the profile to `on` affects future conversations sharing that profile.
Never change global configuration without an explicit answer. A request to use
Luna only for the current task is a turn-level route choice and must not change
the global switch.

Do not ask for short answers, a single small read, command, edit, or test, work
that would remain in the root, or merely because the skill was loaded or child
capacity exists. Do not ask again after the user declines or chooses a route in
the same task. When the expected benefit is borderline or cannot be explained
concretely, preserve native routing without asking. Do not claim quantified
savings unless the runtime exposes actual measurements.

## Precedence And Gate

An explicit user choice of whether to delegate, model, reasoning effort, role,
or child count overrides inferred cost, latency, and quality preferences. Do
not substitute Luna Max, a peer, or root-only execution for a supported explicit
choice. The explicit choice never overrides authorization, safety, runtime
availability, concurrency limits, or one-writer ownership; report the exact
constraint when it cannot be honored.

When the user has not selected a route, delegate only when every condition is
true:

- The task is manual and already authorized. It is not an automatic ZGS task,
  external write, paid provider call, destructive action, permission change,
  credential operation, or unowned shared-runtime action.
- The root retains requirements, planning, architecture, ambiguity, risk,
  integration, and final review.
- The child objective, allowed scope, exclusions, stop conditions, and
  objectively checkable acceptance are explicit.
- The work is clear, repeatable, execution-heavy, or noisy enough to keep out
  of the root context.
- The root can verify the result materially more cheaply than reproducing the
  whole child task.
- One child owns every mutable path or shared resource in its scope.

Otherwise stay in the root. This includes a short answer, one small file read,
one command, a tiny edit, unresolved requirements, final product or architecture
decisions, ambiguous cross-cutting debugging, security judgment, irreversible
work, or any package whose coordination and review overhead is likely to erase
the benefit. Ultra effort and a general preference for speed or quality do not
by themselves justify a child.

A bounded long-running observation is a preferred delegation package when an
operation already exists, monitoring is read-only, its identity and terminal
states are known, the wait has a finite deadline, and it is likely to outlast
one ordinary tool wait or require repeated polls. Apply the gate before the
root enters a polling loop; do not spend root turns first proving that the wait
is long. For this case, delegation is the default even when each individual
poll is only one simple command: the useful saving comes from elapsed waiting,
repeated output, and context retention. Keep the work in the root when an
intermediate state can require a timely decision or the observer would need to
retry, cancel, repair, resubmit, or otherwise mutate the operation or its
runtime.

## Route Selection

Treat cost efficiency as the standing objective while the switch is `on`.

- Use Luna Max for a package that passes the gate and is clear, repeatable,
  execution-heavy, noisy, high-volume, or a bounded read-only wait.
- Keep difficult diagnosis, architecture, ambiguity, cross-cutting reasoning,
  and final review in the root. Max effort does not guarantee parity with the
  root; quality comes from task selection, objective acceptance, and root
  verification.
- Delegate concurrently only when independent packages shorten the critical
  path. For sequential or tightly coupled work, stay in the root.
- Do not infer a same-strength peer while economy mode is on. Use one only when
  the user explicitly requests it or a higher-priority acceptance contract
  requires it, and then select the exact supported root model and effort.
- Do not create a child merely because capacity exists.

## Runtime

The global configuration provides a backstop for unqualified native child
spawns. For the economy route, still select native `explorer` for read-only
discovery and `worker` for bounded implementation or tests, and spawn with
`fork_turns="none"`, `model="gpt-5.6-luna"`, and
`reasoning_effort="max"`. Include all context needed by the bounded package.
Use a small positive `fork_turns` value only when recent turns are essential;
never use `fork_turns="all"` for the inferred economy route. Do not select
`luna_executor`, whose fixed effort is XHigh rather than Max.

For an explicitly requested different route, select the requested supported
model, effort, role, and count. If an exact same-strength peer cannot be
selected and verified, report the limitation instead of claiming parity.

Resolve this skill's directory and verify the effective runtime with
`python[3] <skill-directory>/scripts/verify_subagent_runtime.py --agent-path <canonical-agent-path>`
for Luna Max, or add `--expected-model <selected-model> --expected-effort
<selected-effort>` for any other route.
The verifier waits for the child task boundary and reads its current
`turn_context`; inherited parent turns, base instructions, model lists, and
other model strings are not runtime proof. A `pending` result means wait and
retry the same child, not stop it or spawn a duplicate. Stop a child only when
the verifier reports an explicit current-turn mismatch. If proof remains
unavailable, do not claim verification or start another implicit child.

## Execution

1. Prefer one coherent package over microtasks. When the user has not selected
   a count or route, use one child by default. Infer at most two concurrent
   Luna children, and only when both packages are independent, on the critical
   path, and have non-overlapping scopes and resources. Honor an explicit count
   or route within runtime capacity and the precedence limits above.
2. Give the child only the context and files needed for its bounded objective.
   Require a compact result with outcome, paths or scope, commands and
   evidence, uncertainty, remaining risk, and the minimum blocker if stopped.
3. After spawning, do not repeat the delegated discovery or implementation in
   the root. Wait for the child unless there is genuinely independent
   critical-path work. Preparing acceptance checks is allowed; duplicating the
   task is not.
4. Require the child to stop on ambiguity, scope growth, unsafe action, or an
   unrelated failure. Escalate decisions and cross-cutting failures to the
   root agent; never launch a blind duplicate retry.
5. Inspect the returned diff or evidence in the root. Run the narrowest check
   that proves acceptance, sampling source when a full duplicate review would
   erase the saving. A child completion statement is not proof.
6. Close or release the child when the active tool exposes that lifecycle.

## Long-Running Observation

For a qualifying wait, poll, or monitor:

1. The root captures one initial baseline and gives the child the stable job or
   task identity, exact read route, terminal states, polling interval, deadline,
   and required evidence.
2. Spawn the child before starting repeated application polling. Explicitly
   forbid retries, duplicate submissions, cancellation, process or service
   changes, GPU or queue control, and unrelated investigation.
3. The child retains intermediate samples in its own context and reports only
   a terminal result, an anomaly that needs a root decision, or timeout. Its
   result includes the last state, final output identifiers, error details, and
   elapsed time available from the monitored system.
4. The root waits through the native child lifecycle instead of polling the
   application in parallel. After the child returns, the root performs one
   fresh read-back of the terminal state or output before final judgment.

Report the governing objective or explicit user choice, each child's route,
identity, verified effective model and effort, bounded scope, result, and root
verification. Report token, cost, or latency savings only when the runtime
exposes actual measurements; never infer realized savings from list price.
