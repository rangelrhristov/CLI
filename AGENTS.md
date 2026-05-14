# Codex Terminal Room Instructions

These instructions are for Codex sessions launched from `C:\IDE`.

## Output Style

- Be compact by default. Do not narrate every action, file read, command, or internal step.
- In normal task work, show only:
  - a short status when starting meaningful work,
  - important blockers or decisions,
  - the final result and verification.
- Avoid long process logs in the chat. Summarize commands and edits instead of listing every command output line.
- If command output is long, report only the relevant result, error, or next action.

## Tool Activity Labels

- When describing tool use, name the purpose, not the raw command.
- Prefer compact labels like `Using superpowers`, `Reading project instructions`, `Checking build`, `Patching host`, or `Verifying compile`.
- Do not repeat long paths, command flags, or command text in assistant-written status unless the exact command is needed to debug a failure.
- If a tool reads a skill, config, or docs file, summarize it as the thing being used, for example `Using superpowers`, not `Ran Get-Content -Path ...`.

## Message Clarity

- Make Codex responses visually easy to distinguish from the user's text.
- Start final task responses with `CODEX:` unless the answer is a tiny one-liner.
- Use short sections only when helpful, for example `CODEX: Done`, `Changed`, `Checked`, `Blocked`.
- Keep status updates to one or two sentences.

## Terminal Room Constraints

- The terminal panes are small. Prefer concise answers that fit in a narrow pane.
- Do not produce bulky markdown tables unless specifically requested.
- Do not include exhaustive file lists or command transcripts unless they are needed to debug a failure.
- When the user asks for implementation, make the change and verify it; do not leave only a plan.
