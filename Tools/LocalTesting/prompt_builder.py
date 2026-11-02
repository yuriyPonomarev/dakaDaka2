"""Compose the local tester's explicit, auditable prompt."""
from pathlib import Path


def build_prompt(contract, profile, brief):
    parts = [Path(p).read_text(encoding="utf-8-sig") for p in (contract, profile, brief)]
    if any(not p.strip() for p in parts):
        raise ValueError("contract, tester profile and brief must not be empty")
    return "\n\n".join(parts)
