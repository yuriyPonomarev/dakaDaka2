---
name: dakadaka-commit
description: Commit (and, when the user asked, push) a verified DakaDaka slice through the Haiku git agent, with explicit paths and the project's exclusions, then verify the resulting commit yourself. Use when a slice is complete and verified, or when the user asks to commit/push DakaDaka work.
---

# Commit / push a verified slice

You (orchestrator) choose what goes in; `dakadaka-git` (Haiku) runs git; you verify the outcome.

## 1. Decide the content

```
git -C "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff" status --short
git -C "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff" branch --show-current
```

- Only completed, verified work of this slice (plus its task report and `Docs/CURRENT_WORK.md`). No WIP, no inherited unverified changes.
- Exclude: `Assets/Emberdelve/Art/Materials/*.mat` drift, `ProjectSettings/Packages/com.unity.pipeline/`, `.claude/settings.local.json`, logs/builds, secrets.
- Include the `.meta` file for every new/deleted asset or script.
- Group by roadmap milestone; separate unrelated slices into separate commits.

## 2. Message

Project style (see `git log -5`): plain-English imperative title (~70 chars, no prefix), blank line, body wrapped at ~72 explaining behavior and the key numbers, then:

```
Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
```

## 3. Delegate

Launch Agent `subagent_type: "dakadaka-git"` with:

```
Repo: C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff
Branch: <current branch>
Paths:
- <path>
- <path>.meta
Message:
<title>

<body>

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
push: yes|no
```

`push: yes` only if the user asked to push in this conversation (approval does not carry over to later commits).

## 4. Verify (always)

```
git -C "<repo>" show --stat --format='%h %s' HEAD
git -C "<repo>" status -sb
```

- Files in HEAD == your path list; message intact; nothing unintended left staged.
- If pushed: the `status -sb` line shows no "ahead" for this branch.
- If the git agent hung or reported an error twice, escalate it (`model: "sonnet"`), and if that fails do the commit yourself following the same rules. Never fix a bad commit with `--amend`/reset without telling the user; prefer a follow-up commit.

Tell the user the hash, title, file count and push state.
